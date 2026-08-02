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

using System.Diagnostics;
using Waypoint.Api.Diagnostics;
using Waypoint.Tests.Support;

namespace Waypoint.Tests.Api;

/// <summary>
/// The container health probe (<see cref="HealthCheckProbe"/>). The URL resolution is
/// asserted in-process; the verdict is asserted by running the real binary, because the
/// only thing Docker's <c>HEALTHCHECK</c> observes is the process exit code.
/// </summary>
public sealed class HealthCheckProbeTests
{
	[Theory]
	[InlineData(HealthCheckProbe.Argument, true)]
	[InlineData("--HEALTH-CHECK", true)]
	[InlineData("--healthcheck", false)]
	[InlineData("", false)]
	public void IsHealthCheckInvocation_RecognizesOnlyTheDocumentedArgument(string argument, bool expected)
	{
		Assert.Equal(expected, HealthCheckProbe.IsHealthCheckInvocation([argument]));
	}

	[Fact]
	public void IsHealthCheckInvocation_WithNoArguments_IsFalse()
	{
		Assert.False(HealthCheckProbe.IsHealthCheckInvocation([]));
	}

	[Theory]
	// Wildcard binds are not dialable — the probe runs inside the container, so loopback.
	[InlineData(null, "http://+:8080", "http://127.0.0.1:8080/api/v1/health")]
	[InlineData(null, "http://*:8080", "http://127.0.0.1:8080/api/v1/health")]
	[InlineData(null, "http://0.0.0.0:5000", "http://127.0.0.1:5000/api/v1/health")]
	// https entries are skipped: the probe must not depend on a trusted dev certificate.
	[InlineData(null, "https://+:8443;http://+:8080", "http://127.0.0.1:8080/api/v1/health")]
	// Nothing usable configured — the documented container default.
	[InlineData(null, null, "http://127.0.0.1:8080/api/v1/health")]
	[InlineData(null, "https://+:8443", "http://127.0.0.1:8080/api/v1/health")]
	// The explicit override wins, with or without a path of its own.
	[InlineData("http://127.0.0.1:9999", null, "http://127.0.0.1:9999/api/v1/health")]
	[InlineData("http://127.0.0.1:9999/custom/health", "http://+:8080", "http://127.0.0.1:9999/custom/health")]
	public void ResolveUrl_PicksTheDialableLoopbackHealthUrl(string? explicitUrl, string? aspNetCoreUrls, string expected)
	{
		Assert.Equal(expected, HealthCheckProbe.ResolveUrl(explicitUrl, aspNetCoreUrls));
	}

	[Fact]
	public void Probe_WhenNothingIsListening_ExitsNonZero()
	{
		int exitCode = ApiProcess.Run(
			arguments: [HealthCheckProbe.Argument],
			environment: new Dictionary<string, string>
			{
				["WAYPOINT_HEALTHCHECK_URL"] = $"http://127.0.0.1:{ApiProcess.GetFreePort()}/api/v1/health"
			},
			timeout: TimeSpan.FromSeconds(60));

		Assert.Equal(1, exitCode);
	}

	/// <summary>
	/// The end-to-end contract the container's <c>HEALTHCHECK</c> relies on: with the API
	/// serving, the very same image (no curl, no wget) answers its own probe with exit 0.
	/// </summary>
	[Fact]
	public void Probe_AgainstARunningApi_ExitsZero()
	{
		int port = ApiProcess.GetFreePort();
		string urls = $"http://127.0.0.1:{port}";

		using Process api = ApiProcess.Start(
			environment: new Dictionary<string, string>
			{
				["ASPNETCORE_URLS"] = urls,
				["ASPNETCORE_ENVIRONMENT"] = "Testing"
			});

		try
		{
			// Poll rather than sleep-and-hope: startup time varies with machine load, and
			// this is exactly what Docker's start_period does.
			int exitCode = 1;
			for (int attempt = 0; attempt < 30 && exitCode != 0; attempt++)
			{
				Thread.Sleep(500);
				exitCode = ApiProcess.Run(
					arguments: [HealthCheckProbe.Argument],
					environment: new Dictionary<string, string> { ["ASPNETCORE_URLS"] = urls },
					timeout: TimeSpan.FromSeconds(30));
			}

			Assert.Equal(0, exitCode);
		}
		finally
		{
			ApiProcess.Kill(api);
		}
	}
}
