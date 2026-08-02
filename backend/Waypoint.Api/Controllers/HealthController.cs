using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Waypoint.Api.Contracts;
using Waypoint.Core.Configuration;

namespace Waypoint.Api.Controllers;

/// <summary>Unauthenticated liveness/version probe — what nginx and an operator's monitoring hit first.</summary>
[ApiController]
[Route("api/v1/health")]
[AllowAnonymous]
public sealed class HealthController : ControllerBase
{
	private readonly IOptionsMonitor<WaypointBuildOptions> _buildOptions;

	public HealthController(IOptionsMonitor<WaypointBuildOptions> buildOptions)
	{
		_buildOptions = buildOptions;
	}

	[HttpGet]
	[ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
	public ActionResult<HealthResponse> Get()
	{
		WaypointBuildOptions build = _buildOptions.CurrentValue;

		return Ok(new HealthResponse(
			Status: "ok",
			Version: build.Version,
			Sha: build.Sha,
			BuiltAt: build.BuiltAt,
			Timestamp: DateTimeOffset.UtcNow));
	}
}
