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
///
/// Every test here supplies <c>ConnectionStrings__Waypoint</c> itself. The child inherits
/// the test runner's working directory -- the <c>Waypoint.Tests</c> output directory --
/// whose <c>appsettings.json</c> is NOT the API's: all three host projects are referenced
/// and each copies its own <c>appsettings.json</c> to that one path, so the file that
/// survives is whichever the build happened to copy last. Locally that was
/// <c>Waypoint.DownloadRunner</c>'s (which carries a <c>ConnectionStrings:Waypoint</c>);
/// on GitHub Actions' clean build it was <c>Waypoint.ComplianceRunner</c>'s (which does
/// not), so with a password file configured the resolver correctly threw "no base
/// connection string", the host exited 1, and the valid-file test's health probe was
/// refused for its full 60 s retry loop. Passing the base connection string explicitly --
/// environment variables outrank JSON files -- makes these tests hermetic in both
/// environments and is what they should have done from the start.
/// </summary>
public sealed class DatabasePasswordFileStartupTests
{
	/// <summary>
	/// A complete base connection string with NO <c>Password=</c> -- the shape #843 exists
	/// to support. Never connected to: <c>ASPNETCORE_ENVIRONMENT=Testing</c> turns
	/// migrations and the job engine off, so the API serves <c>/api/v1/health</c> without
	/// ever opening a database connection (the same reason <c>OidcPublicUrlStartupTests</c>
	/// is green on CI, which has no Postgres).
	/// </summary>
	private const string BaseConnectionString =
		"Host=db.example.internal;Port=5432;Database=waypoint;Username=waypoint";

	[Fact]
	public async Task Startup_WithMissingPasswordFile_ExitsNonZeroWithoutEverServing()
	{
		string missingPath = Path.Combine(Path.GetTempPath(), $"waypoint-843-missing-{Guid.NewGuid():N}.txt");

		await AssertFailsClosedAsync(
			new Dictionary<string, string> { ["Database__PasswordFile"] = missingPath },
			missingPath,
			"does not exist");
	}

	[Fact]
	public async Task Startup_WithEmptyPasswordFile_ExitsNonZeroWithoutEverServing()
	{
		string path = Path.Combine(Path.GetTempPath(), $"waypoint-843-empty-{Guid.NewGuid():N}.txt");
		File.WriteAllText(path, string.Empty);

		try
		{
			await AssertFailsClosedAsync(
				new Dictionary<string, string> { ["Database__PasswordFile"] = path },
				path,
				"is empty");
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
			await AssertFailsClosedAsync(
				new Dictionary<string, string> { ["Database__PasswordFile"] = directoryPath },
				directoryPath,
				"could not be read");
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
		ChildOutput output = new();
		using Process process = ApiProcess.Start(
			environment: new Dictionary<string, string>
			{
				["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
				["ASPNETCORE_ENVIRONMENT"] = "Testing",
				["ConnectionStrings__Waypoint"] = BaseConnectionString,
				["Database__PasswordFile"] = path
			},
			output: output);

		try
		{
			HttpStatusCode status = await WaitForHealthyAsync(port, output);
			Assert.Equal(HttpStatusCode.OK, status);
			Assert.False(
				process.HasExited,
				$"A valid Database:PasswordFile must not prevent the API from staying up.{Environment.NewLine}{output.Text}");
		}
		finally
		{
			ApiProcess.Kill(process);
			File.Delete(path);
		}
	}

	/// <param name="extraEnvironment">The password-file configuration under test.</param>
	/// <param name="expectedPathInMessage">
	/// The configured path the startup failure must name, so the assertion cannot be
	/// satisfied by some unrelated fatal startup error that also exits 1.
	/// </param>
	/// <param name="expectedReasonInMessage">The resolver's reason fragment for this branch.</param>
	private static async Task AssertFailsClosedAsync(
		IDictionary<string, string> extraEnvironment,
		string expectedPathInMessage,
		string expectedReasonInMessage)
	{
		int port = ApiProcess.GetFreePort();
		Dictionary<string, string> environment = new(extraEnvironment)
		{
			["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
			["ASPNETCORE_ENVIRONMENT"] = "Testing",
			// Supplied so the ONLY thing wrong with this host is the password file: without
			// it the resolver's "no base connection string" branch would also exit 1, and
			// these tests would pass for the wrong reason.
			["ConnectionStrings__Waypoint"] = BaseConnectionString
		};

		ChildOutput output = new();
		using Process process = ApiProcess.Start(environment: environment, output: output);

		try
		{
			bool exited = process.WaitForExit((int)TimeSpan.FromSeconds(30).TotalMilliseconds);
			Assert.True(
				exited,
				$"The API process should have exited (fatal startup failure), not kept running.{Environment.NewLine}{output.Text}");

			// Flush the async output handlers before reading what the child said on its way out.
			process.WaitForExit();
			Assert.Equal(1, process.ExitCode);

			string childOutput = output.Text;
			Assert.True(
				childOutput.Contains("Database:PasswordFile", StringComparison.Ordinal)
					&& childOutput.Contains(expectedPathInMessage, StringComparison.Ordinal)
					&& childOutput.Contains(expectedReasonInMessage, StringComparison.Ordinal),
				$"The API should have died with the password-file error naming '{expectedPathInMessage}' " +
					$"({expectedReasonInMessage}), not some other fatal startup error." +
					$"{Environment.NewLine}Child output:{Environment.NewLine}{childOutput}");

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

	private static async Task<HttpStatusCode> WaitForHealthyAsync(int port, ChildOutput output)
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

		// Without the child's own output a CI-only startup failure is undiagnosable -- that
		// is exactly how this test's first CI failure burned a review round.
		throw new TimeoutException(
			$"Waypoint.Api did not become healthy in time.{Environment.NewLine}Child output:{Environment.NewLine}{output.Text}",
			lastError);
	}
}
