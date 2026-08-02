using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Waypoint.Tests.Support;

/// <summary>
/// The default in-process test host: real local auth (<see cref="TestAdminPassword"/>
/// configured as the single admin user's password), real "LocalSession" authentication
/// scheme. Use this for anything that should behave exactly as production does —
/// health, login, and the unauthenticated (401) path.
/// </summary>
public class WaypointApiFactory : WebApplicationFactory<Program>
{
	/// <summary>The password the in-memory admin user accepts in this test host.</summary>
	public const string TestAdminPassword = "test-only-dev-password";

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseEnvironment("Testing");
		builder.ConfigureAppConfiguration((_, configBuilder) =>
		{
			configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["LocalAuth:AdminPasswordHash"] = HashPassword(TestAdminPassword)
			});
		});
	}

	private static string HashPassword(string password)
	{
		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
		return Convert.ToHexString(hash).ToLowerInvariant();
	}
}
