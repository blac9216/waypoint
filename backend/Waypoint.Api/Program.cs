using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using Serilog.Formatting.Compact;
using Waypoint.Api.Authentication;
using Waypoint.Api.Logging;
using Waypoint.Api.Middleware;
using Waypoint.Api.Validation;
using Waypoint.Core.Authorization;
using Waypoint.Core.Logging;
using Waypoint.Core.Serialization;
using Waypoint.Infrastructure.DependencyInjection;

// Bootstrap logger: captures anything that happens before the host (and DI, and the
// redaction hook it resolves) is up. Replaced by the fully configured logger below.
Log.Logger = new LoggerConfiguration()
	.WriteTo.Console()
	.CreateBootstrapLogger();

try
{
	WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

	builder.Host.UseSerilog((context, services, loggerConfiguration) =>
	{
		// The seam from docs/security.md control 1: every rendered log line passes
		// through ISecretRedactor before reaching the console sink. Today that's
		// NoOpSecretRedactor (see Waypoint.Infrastructure DI wiring); issue #6 swaps
		// in the real scrubber with no change to this pipeline.
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

	app.UseHttpsRedirection();

	app.UseAuthentication();
	app.UseAuthorization();

	app.MapControllers();

	app.Run();
}
catch (Exception exception)
{
	Log.Fatal(exception, "Waypoint.Api terminated unexpectedly during startup");
}
finally
{
	Log.CloseAndFlush();
}

/// <summary>Partial Program class so <c>WebApplicationFactory&lt;Program&gt;</c> can boot this app in tests.</summary>
public partial class Program
{
}
