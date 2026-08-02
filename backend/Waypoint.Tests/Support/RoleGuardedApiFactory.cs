using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Waypoint.Tests.Support;

/// <summary>
/// A host identical to <see cref="WaypointApiFactory"/> except the default
/// authentication scheme is swapped for <see cref="TestAuthHandler"/>, so a request
/// carrying <c>X-Test-Role</c> authenticates as that role. Used only to exercise the
/// <c>Require*Role</c> guards across the full hierarchy — production code and the
/// "LocalSession" scheme are untouched by this swap.
/// </summary>
public sealed class RoleGuardedApiFactory : WaypointApiFactory
{
	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		base.ConfigureWebHost(builder);

		builder.ConfigureTestServices(services =>
		{
			services
				.AddAuthentication(TestAuthHandler.SchemeName)
				.AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

			services.PostConfigure<AuthenticationOptions>(options =>
			{
				options.DefaultScheme = TestAuthHandler.SchemeName;
				options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
				options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
				options.DefaultForbidScheme = TestAuthHandler.SchemeName;
			});
		});
	}
}
