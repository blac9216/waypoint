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
using Microsoft.Extensions.Options;
using Waypoint.Core.Downloads;
using Waypoint.Infrastructure.Downloads;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// Issue #793: <c>ManagedToolMetadataPuller.PullAsync</c> had no direct unit test --
/// its command-line shaping and nonzero-exit classification were only exercised
/// indirectly through a fake <see cref="IManagedToolMetadataPuller"/> in
/// <c>CatalogPullJobHandlerTests</c>/<c>CatalogPullEndToEndTests</c>. This drives the
/// REAL class against a <c>RealContractStub</c>-equivalent <c>sh</c> script (invented
/// content, faithful to the documented <c>metadata download --depot-store=&lt;dir&gt;
/// --depot-download-activation-code-file=&lt;file&gt; --ceip=DISABLE</c> contract audited
/// in issue #791/#792 -- see <c>DepotIdentityToolTests.RealContractStub</c> for the
/// sibling validate-path equivalent), proving argv shape and the shared
/// <see cref="DownloadToolFailureClassifier"/> auth/network/ambiguous branch selection
/// on the PULL path, not just the validate path.
/// </summary>
public sealed class ManagedToolMetadataPullerTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("wp-metadata-puller-").FullName;

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	private sealed class AlwaysPresent : IManagedToolPresenceChecker
	{
		public bool IsPresent() => true;
		public string DescribeExpectedLocation() => "stub";
	}

	private sealed class NeverPresent : IManagedToolPresenceChecker
	{
		public bool IsPresent() => false;
		public string DescribeExpectedLocation() => "expected/stub/path";
	}

	/// <summary>
	/// Same class-killer shape as <c>DepotIdentityToolTests.RealContractStub</c>: parses
	/// argv the way the real 9.1.0.0400 tool documents and REJECTS an undocumented flag
	/// combination with usage + <c>exit 2</c>, so a regression to a stale command shape
	/// (e.g. <c>-d</c> without the value, or a missing required option) cannot silently
	/// pass. Every call is appended to calls.log for order/shape assertions.
	/// </summary>
	private string RealContractStub(int metadataDownloadExit = 0, string metadataDownloadStdout = "")
	{
		string logAppend = $"echo \"$*\" >> \"{Path.Combine(_root, "calls.log")}\"";
		string usage =
			"Usage: vcf-download-tool metadata download [--ceip=<ceip>] -d=<depotStore> --depot-download-activation-code-file=<file>";

		return Script(
			$$"""
			{{logAppend}}
			sub1="$1"; sub2="$2"
			shift 2 2>/dev/null || true
			if [ "$sub1" != "metadata" ] || [ "$sub2" != "download" ]; then
			  echo "{{usage}}" 1>&2
			  exit 2
			fi
			have_depot_store=0
			have_code_file=0
			for arg in "$@"; do
			  case "$arg" in
			    --depot-store=*|-d=*) have_depot_store=1 ;;
			    -d) have_depot_store=1 ;;
			    --depot-download-activation-code-file=*) have_code_file=1 ;;
			    --ceip=ENABLE|--ceip=DISABLE) : ;;
			    *)
			      echo "Unknown option: $arg" 1>&2
			      echo "{{usage}}" 1>&2
			      exit 2
			      ;;
			  esac
			done
			if [ "$have_depot_store" -ne 1 ] || [ "$have_code_file" -ne 1 ]; then
			  echo "Missing required option" 1>&2
			  echo "{{usage}}" 1>&2
			  exit 2
			fi
			cat <<'STDOUT_EOF'
			{{metadataDownloadStdout}}
			STDOUT_EOF
			exit {{metadataDownloadExit}}
			""");
	}

	private static string Script(string body) => "#!/bin/sh\n" + body;

	private ManagedToolMetadataPuller CreatePuller(string script, out string callLogPath, IManagedToolPresenceChecker? presenceChecker = null)
	{
		string binDir = Path.Combine(_root, "active", "bin");
		Directory.CreateDirectory(binDir);
		string executablePath = Path.Combine(binDir, "vcf-download-tool");
		File.WriteAllText(executablePath, script);
		MakeExecutable(executablePath);

		callLogPath = Path.Combine(_root, "calls.log");

		ManagedToolOptions options = new()
		{
			ToolStatePath = _root,
			ActiveDirectoryName = "active",
			ExecutableRelativePath = "bin/vcf-download-tool",
			LibraryRelativePath = "lib",
			IdentityStatePath = "identity",
			CatalogPullTimeout = TimeSpan.FromSeconds(10),
		};
		return new ManagedToolMetadataPuller(Options.Create(options), presenceChecker ?? new AlwaysPresent());
	}

	private static void MakeExecutable(string path)
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		File.SetUnixFileMode(path,
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
			| UnixFileMode.GroupRead | UnixFileMode.GroupExecute
			| UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
	}

	[Fact]
	public async Task WellFormedInvocation_AgainstRealContractStub_ReturnsOk()
	{
		// AC1/AC4 class-killer: the stub REJECTS undocumented flags (exit 2 + usage). If
		// PullAsync ever regressed off the documented `metadata download --depot-store=
		// ... --depot-download-activation-code-file= ... --ceip=DISABLE` shape, this stub
		// would exit 2 and the result would NOT be Ok.
		ManagedToolMetadataPuller puller = CreatePuller(RealContractStub(metadataDownloadExit: 0), out string callLogPath);
		string depotDir = Path.Combine(_root, "depot");
		Directory.CreateDirectory(depotDir);
		string codeFile = Path.Combine(_root, "code.txt");
		File.WriteAllText(codeFile, "a-code");

		CatalogPullResult result = await puller.PullAsync(depotDir, codeFile, CancellationToken.None);

		Assert.True(result.Succeeded);
		Assert.False(result.IsAuthFailure);

		string invocation = File.ReadAllText(callLogPath);
		Assert.Contains("metadata download", invocation, StringComparison.Ordinal);
		Assert.Contains($"--depot-store={depotDir}", invocation, StringComparison.Ordinal);
		Assert.Contains($"--depot-download-activation-code-file={codeFile}", invocation, StringComparison.Ordinal);
		Assert.Contains("--ceip=DISABLE", invocation, StringComparison.Ordinal);
	}

	[Fact]
	public void RealContractStub_RejectsAnUndocumentedCommandShape_WithUsageAndExit2()
	{
		// Regression guard proving the stub itself actually rejects a stale/invalid
		// command line the way the real tool does -- so if production code ever emits
		// the old shape again, the well-formed test above goes red rather than the stub
		// silently accepting anything.
		string binDir = Path.Combine(_root, "active", "bin");
		Directory.CreateDirectory(binDir);
		string executablePath = Path.Combine(binDir, "vcf-download-tool");
		File.WriteAllText(executablePath, RealContractStub());
		MakeExecutable(executablePath);

		(int exitCode, string stderr) = RunStubDirectly(
			executablePath, "configuration", "get", "--software-depot-id", "--depot-download-activation-code-file=/tmp/code.txt");

		Assert.Equal(2, exitCode);
		Assert.Contains("Usage", stderr, StringComparison.OrdinalIgnoreCase);
	}

	private static (int ExitCode, string Stderr) RunStubDirectly(string executablePath, params string[] args)
	{
		ProcessStartInfo startInfo = new(executablePath)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		foreach (string arg in args)
		{
			startInfo.ArgumentList.Add(arg);
		}

		using Process process = Process.Start(startInfo)!;
		string stderr = process.StandardError.ReadToEnd();
		process.StandardOutput.ReadToEnd();
		process.WaitForExit();
		return (process.ExitCode, stderr);
	}

	[Fact]
	public async Task ToolRejectsCode_IsClassifiedAsAuthFailure()
	{
		ManagedToolMetadataPuller puller = CreatePuller(
			RealContractStub(metadataDownloadExit: 3, metadataDownloadStdout: "Authentication failed: activation code is expired or revoked."),
			out _);
		string depotDir = Path.Combine(_root, "depot");
		Directory.CreateDirectory(depotDir);
		string codeFile = Path.Combine(_root, "code.txt");
		File.WriteAllText(codeFile, "a-code");

		CatalogPullResult result = await puller.PullAsync(depotDir, codeFile, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.True(result.IsAuthFailure);
		Assert.Contains("activation code", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task NetworkUnreachable_IsNotAuthFailure()
	{
		// A network-unreachable environment must produce network-classified guidance,
		// never auth_failing -- even though the invocation completed with a nonzero exit.
		ManagedToolMetadataPuller puller = CreatePuller(
			RealContractStub(metadataDownloadExit: 5, metadataDownloadStdout: "Could not resolve host: depot.example.invalid: connection timed out."),
			out _);
		string depotDir = Path.Combine(_root, "depot");
		Directory.CreateDirectory(depotDir);
		string codeFile = Path.Combine(_root, "code.txt");
		File.WriteAllText(codeFile, "a-code");

		CatalogPullResult result = await puller.PullAsync(depotDir, codeFile, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.False(result.IsAuthFailure);
		Assert.Contains("network", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task AmbiguousNonzeroExit_IsConservativeNonAuthFailure()
	{
		ManagedToolMetadataPuller puller = CreatePuller(
			RealContractStub(metadataDownloadExit: 9, metadataDownloadStdout: "internal error: something unexpected went wrong."),
			out _);
		string depotDir = Path.Combine(_root, "depot");
		Directory.CreateDirectory(depotDir);
		string codeFile = Path.Combine(_root, "code.txt");
		File.WriteAllText(codeFile, "a-code");

		CatalogPullResult result = await puller.PullAsync(depotDir, codeFile, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.False(result.IsAuthFailure);
	}

	[Fact]
	public async Task ToolNotInstalled_FailsWithoutInvokingAnything()
	{
		ManagedToolOptions options = new()
		{
			ToolStatePath = _root,
			ActiveDirectoryName = "active",
			ExecutableRelativePath = "bin/vcf-download-tool",
		};
		ManagedToolMetadataPuller puller = new(Options.Create(options), new NeverPresent());

		CatalogPullResult result = await puller.PullAsync(Path.Combine(_root, "depot"), Path.Combine(_root, "code.txt"), CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.False(result.IsAuthFailure);
		Assert.Contains("not installed", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task NonzeroExitWithNoOutput_IsFailedWithNoOutputMessage()
	{
		ManagedToolMetadataPuller puller = CreatePuller(Script("exit 7\n"), out _);
		string depotDir = Path.Combine(_root, "depot");
		Directory.CreateDirectory(depotDir);
		string codeFile = Path.Combine(_root, "code.txt");
		File.WriteAllText(codeFile, "a-code");

		CatalogPullResult result = await puller.PullAsync(depotDir, codeFile, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.False(result.IsAuthFailure);
		Assert.Contains("no output", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Timeout_IsFailedNeverAuth()
	{
		string binDir = Path.Combine(_root, "active", "bin");
		Directory.CreateDirectory(binDir);
		string executablePath = Path.Combine(binDir, "vcf-download-tool");
		File.WriteAllText(executablePath, Script("sleep 30\n"));
		MakeExecutable(executablePath);

		ManagedToolOptions options = new()
		{
			ToolStatePath = _root,
			ActiveDirectoryName = "active",
			ExecutableRelativePath = "bin/vcf-download-tool",
			LibraryRelativePath = "lib",
			IdentityStatePath = "identity",
			CatalogPullTimeout = TimeSpan.FromMilliseconds(200),
		};
		ManagedToolMetadataPuller puller = new(Options.Create(options), new AlwaysPresent());
		string depotDir = Path.Combine(_root, "depot");
		Directory.CreateDirectory(depotDir);
		string codeFile = Path.Combine(_root, "code.txt");
		File.WriteAllText(codeFile, "a-code");

		CatalogPullResult result = await puller.PullAsync(depotDir, codeFile, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.False(result.IsAuthFailure);
		Assert.Contains("timed out", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
	}
}
