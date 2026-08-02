using System.Diagnostics;
using Waypoint.Tests.Support;

namespace Waypoint.Tests.Api;

/// <summary>
/// A fatal startup failure must be visible to whatever supervises the process. Compose's
/// <c>restart: on-failure</c>, <c>condition: service_healthy</c> gating and any CI step
/// that reads <c>$?</c> all read exit 0 as "the process did its job and stopped" — so a
/// backend that cannot bind its port and still exits 0 fails silently, forever.
/// </summary>
public sealed class StartupExitCodeTests
{
	[Fact]
	public void Startup_WhenTheListenUrlCannotBeBound_ExitsNonZero()
	{
		// Port 99999 is outside the valid TCP range, so binding fails deterministically —
		// no race with whatever else happens to hold a port on the build machine.
		int exitCode = ApiProcess.Run(
			environment: new Dictionary<string, string>
			{
				["ASPNETCORE_URLS"] = "http://127.0.0.1:99999",
				["ASPNETCORE_ENVIRONMENT"] = "Testing"
			},
			timeout: TimeSpan.FromSeconds(90));

		Assert.NotEqual(0, exitCode);
	}

	[Fact]
	public void Startup_WhenTheListenUrlIsValid_KeepsRunning()
	{
		// The counterpart assertion: the non-zero exit above is a real failure signal, not
		// the app exiting non-zero on every start.
		using Process process = ApiProcess.Start(
			environment: new Dictionary<string, string>
			{
				["ASPNETCORE_URLS"] = $"http://127.0.0.1:{ApiProcess.GetFreePort()}",
				["ASPNETCORE_ENVIRONMENT"] = "Testing"
			});

		try
		{
			Assert.False(
				process.WaitForExit(5_000),
				"A healthy configuration must not terminate the host during startup.");
		}
		finally
		{
			ApiProcess.Kill(process);
		}
	}
}
