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

using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Runner;

/// <summary>
/// Issue #843, the two dedicated runner hosts' half of "a missing, empty, or unreadable
/// password file fails the process closed". The API's equivalent lives in
/// <c>DatabasePasswordFileStartupTests</c>; these hosts need their own coverage because
/// their failure mechanism is different -- neither <c>Waypoint.ComplianceRunner</c> nor
/// <c>Waypoint.DownloadRunner</c> has a top-level <c>catch (Exception)</c> (matching the
/// existing <c>ManagedTool:ToolStatePath</c> startup-validation pattern), so their
/// <c>ResolveAndApply</c> failure surfaces as an unhandled exception: a non-zero exit code
/// plus the resolver's message on stderr, neither of which is observable in process.
///
/// As in the API tests, <c>ConnectionStrings__Waypoint</c> is supplied explicitly rather
/// than discovered from the inherited working directory's <c>appsettings.json</c> -- both
/// so the tests behave identically on CI and locally, and so the resolver's OTHER throwing
/// branch ("no base connection string") cannot satisfy the assertion by accident.
/// </summary>
public sealed class RunnerDatabasePasswordFileStartupTests
{
	private const string BaseConnectionString =
		"Host=db.example.internal;Port=5432;Database=waypoint;Username=waypoint";

	public static TheoryData<string> RunnerEntryAssemblies() => new()
	{
		RunnerProcess.ComplianceEntryAssemblyPath,
		RunnerProcess.DownloadEntryAssemblyPath
	};

	[Theory]
	[MemberData(nameof(RunnerEntryAssemblies))]
	public void Startup_WithMissingPasswordFile_ExitsNonZero(string entryAssemblyPath)
	{
		string missingPath = Path.Combine(Path.GetTempPath(), $"waypoint-843-runner-missing-{Guid.NewGuid():N}.txt");

		AssertFailsClosed(entryAssemblyPath, missingPath, "does not exist");
	}

	[Theory]
	[MemberData(nameof(RunnerEntryAssemblies))]
	public void Startup_WithEmptyPasswordFile_ExitsNonZero(string entryAssemblyPath)
	{
		string path = Path.Combine(Path.GetTempPath(), $"waypoint-843-runner-empty-{Guid.NewGuid():N}.txt");
		File.WriteAllText(path, string.Empty);

		try
		{
			AssertFailsClosed(entryAssemblyPath, path, "is empty");
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Theory]
	[MemberData(nameof(RunnerEntryAssemblies))]
	public void Startup_WithUnreadablePasswordFile_ExitsNonZero(string entryAssemblyPath)
	{
		// A directory at the configured path is unreadable as a password file.
		string directoryPath = Path.Combine(Path.GetTempPath(), $"waypoint-843-runner-dir-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directoryPath);

		try
		{
			AssertFailsClosed(entryAssemblyPath, directoryPath, "could not be read");
		}
		finally
		{
			Directory.Delete(directoryPath);
		}
	}

	private static void AssertFailsClosed(
		string entryAssemblyPath,
		string passwordFilePath,
		string expectedReasonInMessage)
	{
		ChildOutput output = new();
		int exitCode = HostProcess.Run(
			entryAssemblyPath,
			environment: new Dictionary<string, string>
			{
				["DOTNET_ENVIRONMENT"] = "Testing",
				["ConnectionStrings__Waypoint"] = BaseConnectionString,
				["Database__PasswordFile"] = passwordFilePath
			},
			timeout: TimeSpan.FromSeconds(60),
			output: output);

		string childOutput = output.Text;
		Assert.True(
			exitCode != 0,
			$"{Path.GetFileName(entryAssemblyPath)} must fail closed on a bad Database:PasswordFile, " +
				$"not start.{Environment.NewLine}Child output:{Environment.NewLine}{childOutput}");
		Assert.True(
			childOutput.Contains("Database:PasswordFile", StringComparison.Ordinal)
				&& childOutput.Contains(passwordFilePath, StringComparison.Ordinal)
				&& childOutput.Contains(expectedReasonInMessage, StringComparison.Ordinal),
			$"{Path.GetFileName(entryAssemblyPath)} should have died with the password-file error naming " +
				$"'{passwordFilePath}' ({expectedReasonInMessage}), not some other fatal startup error." +
				$"{Environment.NewLine}Child output:{Environment.NewLine}{childOutput}");
	}
}
