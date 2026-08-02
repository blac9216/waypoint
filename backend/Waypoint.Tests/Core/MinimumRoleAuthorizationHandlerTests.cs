using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Waypoint.Core.Authorization;

namespace Waypoint.Tests.Core;

public sealed class MinimumRoleAuthorizationHandlerTests
{
	[Theory]
	[InlineData(WaypointRole.Viewer, WaypointRole.Viewer, true)]
	[InlineData(WaypointRole.Cyber, WaypointRole.Viewer, true)]
	[InlineData(WaypointRole.Admin, WaypointRole.Viewer, true)]
	[InlineData(WaypointRole.Viewer, WaypointRole.Cyber, false)]
	[InlineData(WaypointRole.Operator, WaypointRole.Admin, false)]
	[InlineData(WaypointRole.Admin, WaypointRole.Admin, true)]
	public async Task HandleRequirementAsync_ComparesRoleOrdinally(WaypointRole userRole, WaypointRole requiredRole, bool expectedSucceeded)
	{
		bool succeeded = await EvaluateAsync(userRole.ToString(), requiredRole);

		Assert.Equal(expectedSucceeded, succeeded);
	}

	[Fact]
	public async Task HandleRequirementAsync_MissingRoleClaim_FailsClosed()
	{
		bool succeeded = await EvaluateAsync(roleClaimValue: null, WaypointRole.Viewer);

		Assert.False(succeeded);
	}

	[Fact]
	public async Task HandleRequirementAsync_UnparseableRoleClaim_FailsClosed()
	{
		bool succeeded = await EvaluateAsync("NotARealRole", WaypointRole.Viewer);

		Assert.False(succeeded);
	}

	private static async Task<bool> EvaluateAsync(string? roleClaimValue, WaypointRole requiredRole)
	{
		List<Claim> claims = [];
		if (roleClaimValue is not null)
		{
			claims.Add(new Claim(WaypointClaimTypes.Role, roleClaimValue));
		}

		ClaimsPrincipal principal = new(new ClaimsIdentity(claims, "TestScheme"));
		MinimumRoleRequirement requirement = new(requiredRole);
		AuthorizationHandlerContext context = new([requirement], principal, resource: null);

		MinimumRoleAuthorizationHandler handler = new();
		await handler.HandleAsync(context);

		return context.HasSucceeded;
	}
}
