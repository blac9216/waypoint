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
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

// The LocalAuth__AdminPasswordHash generator (see PasswordHashCli, issue #62): the
// same binary that verifies the hash also produces it, so the stored format's
// parameters (KDF, iterations) can never drift between the two. Handled before any
// host or logging setup for the same reason as --health-check above.
if (PasswordHashCli.IsHashPasswordInvocation(args))
{
	return PasswordHashCli.Run();
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

	// Issue #443: the API-only half of the control-plane composition -- SSE fan-out
	// and the run-secret cleanup sweep. Deliberately a second call, not folded into
	// AddWaypointInfrastructure above: both dedicated runners also call that method
	// for the control-plane repositories they share with the API, and neither has the
	// job_events SELECT grant JobEventStreamService's poll loop needs (migration
	// 0025) -- see AddWaypointApiSurface's doc comment for how that was found.
	builder.Services.AddWaypointApiSurface(builder.Configuration);

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

	// Issue #29: OIDC (Keycloak) bearer validation is the production auth path.
	// LocalAuth:Enabled (off by default -- LocalAuthOptions doc comment) is an explicit
	// dev-flag escape hatch the e2e suite and fresh-stack-smoke-test.sh still use.
	//
	// Both schemes are ALWAYS registered (deliberately not gated on reading
	// LocalAuth:Enabled here): WebApplicationFactory-based test hosts overlay their
	// config (e.g. WaypointApiFactory setting LocalAuth:Enabled=true) after this
	// top-level Program.cs code already ran, so an eager `builder.Configuration` read
	// at this point does not see it -- only options resolved later, through DI, do.
	// Registering LocalSession unconditionally and deciding per-request is what makes
	// this correct in both the real app and the test host without special-casing
	// either: OidcOrLocalPolicySchemeDefaults.SelectScheme reads
	// IOptionsMonitor<LocalAuthOptions> live and routes every request to Oidc whenever
	// the flag is off, so a disabled local scheme is still never reachable by a caller
	// no matter what they present -- see that class's doc comment.
	OidcAuthOptions oidcOptions = builder.Configuration.GetSection(OidcAuthOptions.SectionName).Get<OidcAuthOptions>()
		?? new OidcAuthOptions();

	builder.Services
		.AddAuthentication(OidcOrLocalPolicySchemeDefaults.Scheme)
		.AddJwtBearer(options =>
		{
			options.Authority = oidcOptions.Authority;
			options.Audience = oidcOptions.Audience;
			options.RequireHttpsMetadata = oidcOptions.RequireHttpsMetadata;
		})
		.AddScheme<AuthenticationSchemeOptions, LocalSessionAuthenticationHandler>(
			LocalSessionAuthenticationDefaults.Scheme, _ => { })
		.AddPolicyScheme(OidcOrLocalPolicySchemeDefaults.Scheme, OidcOrLocalPolicySchemeDefaults.Scheme, options =>
		{
			options.ForwardDefaultSelector = OidcOrLocalPolicySchemeDefaults.SelectScheme;
		});
	// Configured via IPostConfigureOptions (not inline in AddJwtBearer above) so the
	// claims-mapping events get a real ILoggerFactory from the built container instead
	// of standing up a throwaway one -- BuildServiceProvider() mid-registration would
	// construct a second container the rest of the app never uses.
	builder.Services.ConfigureOptions<OidcClaimsMappingOptionsSetup>();

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

	// Issue #29: logged once at startup from the built app's own options (not the
	// eager builder.Configuration read Program.cs deliberately avoids -- see the auth
	// registration block above), so this reflects the same value the request-time
	// policy scheme selector actually uses.
	if (app.Services.GetRequiredService<IOptions<LocalAuthOptions>>().Value.Enabled)
	{
		Log.Warning(
			"LocalAuth:Enabled is true -- the dev-grade local-session auth scheme is reachable alongside OIDC. " +
			"This is a development/test convenience (issue #29), never a supported production identity path.");
	}

	// ADR-0009 expects the schema to be current before the API takes traffic — this
	// runs (and, on failure, throws into the fatal-startup catch below) before the
	// request pipeline is wired up at all, not lazily on first request. The "Testing"
	// environment configuration turns RunMigrationsOnStartup off: the in-process test
	// host has no Postgres to migrate against (see appsettings.Testing.json).
	//
	// ApplyAsync is passed the host's shutdown token (issue #231, deferred from #108/
	// #229): #229 made the advisory-lock acquire wait unbounded (CommandTimeout = 0),
	// so a second instance can now block indefinitely behind another instance's
	// in-progress migration. Without a real token, nothing could interrupt that wait
	// or an in-flight migration on host shutdown -- ApplicationStopping is the token
	// IHostApplicationLifetime signals when the host begins a graceful shutdown, and
	// it is already available here because it comes from the built host's DI
	// container, before app.Run() starts taking traffic.
	WaypointDatabaseOptions databaseOptions = app.Services.GetRequiredService<IOptions<WaypointDatabaseOptions>>().Value;
	if (databaseOptions.RunMigrationsOnStartup)
	{
		ISchemaMigrator migrator = app.Services.GetRequiredService<ISchemaMigrator>();
		IHostApplicationLifetime lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
		await migrator.ApplyAsync(lifetime.ApplicationStopping);
	}

	// First in the pipeline (#61): the appliance always sits behind nginx (ADR-0003),
	// which terminates TLS and forwards the original scheme and client IP. Without
	// this, every request looks like plain HTTP from the proxy container -- useless
	// for the audit trail's initiating-identity record (security.md control 4) and
	// wrong for any scheme-dependent logic. Trust is restricted to the configured
	// known networks (ForwardedHeaders:KnownNetworks, CIDR list). deploy/docker-compose.yml
	// pins the `edge` network nginx and the backend share to a fixed subnet
	// (192.168.240.0/24) and sets ForwardedHeaders__KnownNetworks__0 to exactly that
	// subnet (#191), so the real compose deployment trusts only nginx's network, not
	// every private address anywhere. The fallback below (all three RFC 1918 spaces)
	// applies only when that env var is absent -- e.g. a non-compose/dev run -- and
	// stays broad on purpose since such a run has no pinned subnet to name precisely.
	// The Testing host sets ForwardedHeaders:TrustAnyProxy=true because TestServer
	// connections carry no remote address to match against.
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

	// Issue #512: after UseAuthentication (needs a populated HttpContext.User) and
	// before UseAuthorization/controllers, so the users row reflects the caller's
	// claims even on a request that authorization later rejects (e.g. a Viewer hitting
	// an Admin-only route still "showed up" and should count as seen).
	app.UseMiddleware<Waypoint.Api.Middleware.UserUpsertMiddleware>();

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
