// Copyright 2026 Justin Black
//
// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Formatting.Compact;
using Waypoint.Api.Authentication;
using Waypoint.Api.Diagnostics;
using Waypoint.Api.Logging;
using Waypoint.Api.Middleware;
using Waypoint.Api.Validation;
using Waypoint.Core.Authorization;
using Waypoint.Core.Configuration;
using Waypoint.Core.Logging;
using Waypoint.Core.Serialization;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.DependencyInjection;

// The container health probe (see HealthCheckProbe): the same binary answers
// `--health-check` with an exit code, so the slim runtime image needs no curl/wget.
// Handled before any host or logging setup — it must stay cheap and side-effect free.
if (HealthCheckProbe.IsHealthCheckInvocation(args))
{
	return HealthCheckProbe.Run();
}

// Bootstrap logger: captures anything that happens before the host (and DI, and the
// redaction hook it resolves) is up. Kept as the static Log.Logger for the lifetime of
// the process (see preserveStaticLogger below); it is only ever used by the startup
// failure path in this file — everything else logs through DI.
Log.Logger = new LoggerConfiguration()
	.WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
	.CreateBootstrapLogger();

try
{
	WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

	// preserveStaticLogger: true leaves the bootstrap Log.Logger alone instead of
	// freezing it into the host's logger. Without it, a second host built in the same
	// process (every extra WebApplicationFactory in the test suite) calls Freeze() on an
	// already-frozen global ReloadableLogger and throws "The logger is already frozen."
	// The host's own logger — the one with the redaction seam — is unaffected either way.
	builder.Host.UseSerilog(preserveStaticLogger: true, configureLogger: (context, services, loggerConfiguration) =>
	{
		// The seam from docs/security.md control 1: every rendered log line passes
		// through ISecretRedactor before reaching the console sink. The registered
		// implementation is InPlaySecretRedactor (epic #6 slice 1) -- it scrubs
		// whatever secret values are currently Track()ed as in play.
		ISecretRedactor redactor = services.GetRequiredService<ISecretRedactor>();

		loggerConfiguration
			.ReadFrom.Configuration(context.Configuration)
			.ReadFrom.Services(services)
			.Enrich.FromLogContext()
			.WriteTo.Console(new RedactingTextFormatter(new CompactJsonFormatter(), redactor));
	});

	builder.Services.AddWaypointInfrastructure(builder.Configuration);

	builder.Services
		.AddControllers()
		.AddJsonOptions(options => WaypointJsonOptions.Apply(options.JsonSerializerOptions));

	// [ApiController]'s automatic model-state 400 otherwise bypasses the error envelope
	// entirely and returns RFC 7807 ProblemDetails in camelCase. Route it through the same
	// writer as every other error so missing fields, malformed JSON and mistyped query
	// parameters answer with { "error": { "code": "validation_error", ... } }.
	builder.Services.Configure<ApiBehaviorOptions>(options =>
	{
		options.InvalidModelStateResponseFactory = ValidationErrorFactory.Create;
	});

	builder.Services.AddEndpointsApiExplorer();
	builder.Services.AddSwaggerGen();

	builder.Services
		.AddAuthentication(LocalSessionAuthenticationDefaults.Scheme)
		.AddScheme<AuthenticationSchemeOptions, LocalSessionAuthenticationHandler>(
			LocalSessionAuthenticationDefaults.Scheme, _ => { });

	builder.Services.AddAuthorization(options =>
	{
		foreach (WaypointRole role in Enum.GetValues<WaypointRole>())
		{
			options.AddPolicy(
				WaypointAuthorizationPolicies.MinimumRole(role),
				policy => policy.Requirements.Add(new MinimumRoleRequirement(role)));
		}
	});
	builder.Services.AddSingleton<IAuthorizationHandler, MinimumRoleAuthorizationHandler>();

	WebApplication app = builder.Build();

	// ADR-0009 expects the schema to be current before the API takes traffic — this
	// runs (and, on failure, throws into the fatal-startup catch below) before the
	// request pipeline is wired up at all, not lazily on first request. The "Testing"
	// environment configuration turns RunMigrationsOnStartup off: the in-process test
	// host has no Postgres to migrate against (see appsettings.Testing.json).
	WaypointDatabaseOptions databaseOptions = app.Services.GetRequiredService<IOptions<WaypointDatabaseOptions>>().Value;
	if (databaseOptions.RunMigrationsOnStartup)
	{
		ISchemaMigrator migrator = app.Services.GetRequiredService<ISchemaMigrator>();
		await migrator.ApplyAsync();
	}

	// First in the pipeline (#61): the appliance always sits behind nginx (ADR-0003),
	// which terminates TLS and forwards the original scheme and client IP. Without
	// this, every request looks like plain HTTP from the proxy container -- useless
	// for the audit trail's initiating-identity record (security.md control 4) and
	// wrong for any scheme-dependent logic. Trust is restricted to the configured
	// known networks (ForwardedHeaders:KnownNetworks, CIDR list; defaults to the
	// RFC 1918 docker address pools the compose stack uses) so a client outside the
	// proxy cannot spoof its source. The Testing host sets
	// ForwardedHeaders:TrustAnyProxy=true because TestServer connections carry no
	// remote address to match against.
	ForwardedHeadersOptions forwardedHeaders = new()
	{
		ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
			| Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
	};
	// PR #190 round 1: TrustAnyProxy is honoured ONLY in the Testing environment --
	// in any other environment the flag is ignored outright, so no production
	// configuration can widen trust to "everyone" (one env var must never be able
	// to disable a spoofing control).
	if (app.Environment.IsEnvironment("Testing") && app.Configuration.GetValue<bool>("ForwardedHeaders:TrustAnyProxy"))
	{
		forwardedHeaders.KnownNetworks.Clear();
		forwardedHeaders.KnownProxies.Clear();
	}
	else
	{
		foreach (string cidr in app.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>()
			?? ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"])
		{
			// Fail fast and name the fix: a malformed entry must stop startup with an
			// operator-actionable message, not an anonymous IndexOutOfRange.
			string[] parts = cidr.Split('/');
			if (parts.Length != 2
				|| !System.Net.IPAddress.TryParse(parts[0], out System.Net.IPAddress? network)
				|| !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int prefix)
				|| prefix is < 0 or > 32)
			{
				throw new InvalidOperationException(
					$"ForwardedHeaders:KnownNetworks entry '{cidr}' is not a valid CIDR (expected e.g. '172.16.0.0/12').");
			}

			forwardedHeaders.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(network, prefix));
		}
	}

	app.UseForwardedHeaders(forwardedHeaders);

	app.UseSerilogRequestLogging();

	// Outermost: catch anything that throws before it reaches the client unshaped.
	app.UseMiddleware<ErrorHandlingMiddleware>();

	// Catches 401/403/404/etc. produced *without* an exception (auth challenge/forbid,
	// unmatched route) and gives them the same envelope shape as a thrown ApiException.
	app.UseStatusCodePages(async statusCodeContext =>
	{
		Microsoft.AspNetCore.Http.HttpContext httpContext = statusCodeContext.HttpContext;
		await ErrorEnvelopeWriter.WriteAsync(
			httpContext,
			(System.Net.HttpStatusCode)httpContext.Response.StatusCode,
			ErrorEnvelopeWriter.ForStatusCode(httpContext.Response.StatusCode));
	});

	if (app.Environment.IsDevelopment())
	{
		app.UseSwagger();
		app.UseSwaggerUI();
	}

	// No UseHttpsRedirection (#61): TLS is the edge's job (ADR-0003); the backend
	// never listens on HTTPS in any deployment. The old call was a boot-log warning
	// today and a latent 307 loop if ASPNETCORE_HTTPS_PORTS ever appeared.
	app.UseAuthentication();
	app.UseAuthorization();

	app.MapControllers();

	await app.RunAsync();

	return 0;
}
catch (Exception exception)
{
	Log.Fatal(exception, "Waypoint.Api terminated unexpectedly during startup");

	// Non-zero is load-bearing, not cosmetic: `restart: on-failure`, compose health
	// gating and any CI that reads $? all treat exit 0 as "the process did its job and
	// stopped". A backend that cannot bind its port must not report success.
	return 1;
}
finally
{
	Log.CloseAndFlush();
}

/// <summary>Partial Program class so <c>WebApplicationFactory&lt;Program&gt;</c> can boot this app in tests.</summary>
public partial class Program
{
}
