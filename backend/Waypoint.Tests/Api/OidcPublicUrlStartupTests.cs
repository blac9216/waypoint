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
using System.Net;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Api;

/// <summary>
/// Issue #842 acceptance criterion "invalid public URLs fail before serving requests",
/// proven through the REAL entry point -- <see cref="ApiProcess"/> launches
/// <c>Waypoint.Api.dll</c> as a genuine child process, not <c>WebApplicationFactory</c>'s
/// in-process <c>TestServer</c> -- because the thing under test IS the fatal-startup
/// path in <c>Program.cs</c> (<c>AddOptions&lt;OidcAuthOptions&gt;().ValidateOnStart()</c>,
/// caught by the top-level <c>catch (Exception)</c> that logs and returns exit code 1).
/// A WebApplicationFactory-based test can observe the same exception from
/// <c>CreateClient()</c>/<c>Services</c>, but cannot observe the process actually
/// refusing to bind its listening socket and exiting non-zero -- the two behaviours
/// <c>docker compose</c>'s own health/restart gating relies on for "fails before serving
/// requests" to mean anything operationally.
/// </summary>
public sealed class OidcPublicUrlStartupTests
{
	[Theory]
	[InlineData("http://waypoint.example.internal")]
	[InlineData("https://waypoint.example.internal/")]
	[InlineData("https://waypoint.example.internal/realms/waypoint")]
	[InlineData("not-a-url")]
	public async Task Startup_WithInvalidPublicUrl_ExitsNonZeroWithoutEverServing(string invalidPublicUrl)
	{
		int port = ApiProcess.GetFreePort();

		using Process process = ApiProcess.Start(environment: new Dictionary<string, string>
		{
			["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
			["ASPNETCORE_ENVIRONMENT"] = "Testing",
			["Oidc__PublicUrl"] = invalidPublicUrl
		});

		try
		{
			bool exited = process.WaitForExit((int)TimeSpan.FromSeconds(30).TotalMilliseconds);
			Assert.True(exited, "The API process should have exited (fatal startup failure), not kept running.");
			Assert.Equal(1, process.ExitCode);

			// Belt-and-suspenders: confirm it truly never opened the listening socket,
			// not just that it happened to exit for some unrelated reason afterward.
			using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(2) };
			await Assert.ThrowsAnyAsync<HttpRequestException>(
				() => client.GetAsync($"http://127.0.0.1:{port}/api/v1/health"));
		}
		finally
		{
			ApiProcess.Kill(process);
		}
	}

	[Fact]
	public async Task Startup_WithValidPublicUrl_ServesHealthSuccessfully()
	{
		int port = ApiProcess.GetFreePort();

		using Process process = ApiProcess.Start(environment: new Dictionary<string, string>
		{
			["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
			["ASPNETCORE_ENVIRONMENT"] = "Testing",
			["Oidc__PublicUrl"] = "https://waypoint.example.internal"
		});

		try
		{
			HttpStatusCode status = await WaitForHealthyAsync(port);
			Assert.Equal(HttpStatusCode.OK, status);
			Assert.False(process.HasExited, "A valid Oidc:PublicUrl must not prevent the API from staying up.");
		}
		finally
		{
			ApiProcess.Kill(process);
		}
	}

	private static async Task<HttpStatusCode> WaitForHealthyAsync(int port)
	{
		using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
		Exception? lastError = null;

		for (int attempt = 0; attempt < 60; attempt++)
		{
			try
			{
				HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{port}/api/v1/health");
				if (response.IsSuccessStatusCode)
				{
					return response.StatusCode;
				}
			}
			catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
			{
				lastError = exception;
			}

			await Task.Delay(TimeSpan.FromSeconds(1));
		}

		throw new TimeoutException("Waypoint.Api did not become healthy in time.", lastError);
	}
}
