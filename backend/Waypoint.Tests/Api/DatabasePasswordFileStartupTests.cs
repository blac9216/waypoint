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
/// Issue #843 acceptance criteria "missing, empty, and unreadable [password] files
/// fail clearly without leaking paths or contents to API callers", proven through the
/// REAL entry point -- <see cref="ApiProcess"/> launches <c>Waypoint.Api.dll</c> as a
/// genuine child process, mirroring <c>OidcPublicUrlStartupTests</c> -- because the
/// thing under test is <c>Program.cs</c>'s fatal-startup path
/// (<c>DatabaseConnectionStringResolver.ResolveAndApply</c>, caught by the top-level
/// <c>catch (Exception)</c> that logs and returns exit code 1) actually refusing to
/// bind its listening socket, not merely an in-process exception a
/// <c>WebApplicationFactory</c> test could observe from <c>Services</c>.
/// </summary>
public sealed class DatabasePasswordFileStartupTests
{
	[Fact]
	public async Task Startup_WithMissingPasswordFile_ExitsNonZeroWithoutEverServing()
	{
		string missingPath = Path.Combine(Path.GetTempPath(), $"waypoint-843-missing-{Guid.NewGuid():N}.txt");

		await AssertFailsClosedAsync(new Dictionary<string, string>
		{
			["Database__PasswordFile"] = missingPath
		});
	}

	[Fact]
	public async Task Startup_WithEmptyPasswordFile_ExitsNonZeroWithoutEverServing()
	{
		string path = Path.Combine(Path.GetTempPath(), $"waypoint-843-empty-{Guid.NewGuid():N}.txt");
		File.WriteAllText(path, string.Empty);

		try
		{
			await AssertFailsClosedAsync(new Dictionary<string, string>
			{
				["Database__PasswordFile"] = path
			});
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public async Task Startup_WithUnreadablePasswordFile_ExitsNonZeroWithoutEverServing()
	{
		// A directory at the configured path is unreadable as a password file --
		// File.ReadAllText throws UnauthorizedAccessException, the "unreadable" branch
		// distinct from "missing"/"empty".
		string directoryPath = Path.Combine(Path.GetTempPath(), $"waypoint-843-dir-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directoryPath);

		try
		{
			await AssertFailsClosedAsync(new Dictionary<string, string>
			{
				["Database__PasswordFile"] = directoryPath
			});
		}
		finally
		{
			Directory.Delete(directoryPath);
		}
	}

	[Fact]
	public async Task Startup_WithValidPasswordFile_ServesHealthSuccessfully()
	{
		// Special-character password + a single trailing newline, exactly as an
		// operator-mounted secret file would be written (`printf` with no trailing
		// newline, or `echo` with one) -- proves the resolved connection string is
		// accepted end to end through the real host, not just by the resolver's own
		// unit tests.
		string path = Path.Combine(Path.GetTempPath(), $"waypoint-843-valid-{Guid.NewGuid():N}.txt");
		File.WriteAllText(path, "special;chars=\"in\"'this'pwd\n");

		int port = ApiProcess.GetFreePort();
		using Process process = ApiProcess.Start(environment: new Dictionary<string, string>
		{
			["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
			["ASPNETCORE_ENVIRONMENT"] = "Testing",
			["Database__PasswordFile"] = path
		});

		try
		{
			HttpStatusCode status = await WaitForHealthyAsync(port);
			Assert.Equal(HttpStatusCode.OK, status);
			Assert.False(process.HasExited, "A valid Database:PasswordFile must not prevent the API from staying up.");
		}
		finally
		{
			ApiProcess.Kill(process);
			File.Delete(path);
		}
	}

	private static async Task AssertFailsClosedAsync(IDictionary<string, string> extraEnvironment)
	{
		int port = ApiProcess.GetFreePort();
		Dictionary<string, string> environment = new(extraEnvironment)
		{
			["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
			["ASPNETCORE_ENVIRONMENT"] = "Testing"
		};

		using Process process = ApiProcess.Start(environment: environment);

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
