using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.Pagination;

namespace Waypoint.Api.Controllers;

/// <summary>
/// Scaffold-only endpoints (issue #3) that exist to prove the conventions this
/// milestone establishes actually work over real HTTP requests: pagination headers, a
/// role-guarded 403, and an authenticated-but-anonymous-blocked 401. Delete this
/// controller once a real paginated resource (e.g. <c>/sites</c>, issue #4+) exists to
/// demonstrate the same conventions in production code — the fictional data below is
/// not a resource contract.
/// </summary>
[ApiController]
[Route("api/v1/_stub")]
public sealed class ScaffoldStubController : ControllerBase
{
	private static readonly ScaffoldStubItem[] Items =
	[
		new ScaffoldStubItem("stub-1", "site-alpha"),
		new ScaffoldStubItem("stub-2", "site-bravo"),
		new ScaffoldStubItem("stub-3", "site-charlie")
	];

	/// <summary>Any authenticated role may list — demonstrates 401 (no token) and X-Total-Count pagination.</summary>
	[HttpGet("items")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ScaffoldStubItem[]), StatusCodes.Status200OK)]
	public ActionResult<IReadOnlyList<ScaffoldStubItem>> GetItems([FromQuery] PageRequest page)
	{
		ScaffoldStubItem[] slice = Items
			.Skip(page.Offset)
			.Take(page.Limit)
			.ToArray();

		Response.Headers["X-Total-Count"] = Items.Length.ToString();

		return Ok(slice);
	}

	/// <summary>Admin only — demonstrates 403 for an authenticated principal below the required role.</summary>
	[HttpGet("admin-only")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(ScaffoldStubMessage), StatusCodes.Status200OK)]
	public ActionResult<ScaffoldStubMessage> GetAdminOnly()
	{
		return Ok(new ScaffoldStubMessage("You have Admin access."));
	}
}
