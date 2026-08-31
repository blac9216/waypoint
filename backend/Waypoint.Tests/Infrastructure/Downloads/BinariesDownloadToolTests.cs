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

using Microsoft.Extensions.Options;
using Waypoint.Core.Downloads;
using Waypoint.Infrastructure.Downloads;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// Issue #1482: <c>BinariesDownloadTool.DownloadAsync</c> driven against a
/// <c>RealContractStub</c>-equivalent <c>sh</c> script (invented content, faithful to
/// this issue's own documented <c>binaries download --id &lt;id&gt;
/// --depot-store=&lt;depot&gt; --ceip=DISABLE</c> contract -- see
/// <c>ManagedToolMetadataPullerTests.RealContractStub</c>/<c>DepotIdentityToolTests.RealContractStub</c>
/// for the sibling contract-audited equivalents this mirrors), proving argv shape, the
/// shared <see cref="DownloadToolFailureClassifier"/> auth/network/disk/throttle
/// branch selection extended for this issue, and -- the AC that has no fake-able
/// equivalent -- that two invocations given DIFFERENT job-scoped identity homes and
/// asset ids never observe each other's seeded <c>machine_id</c> (issue #1482 AC:
/// "Concurrent jobs each get an isolated identity home -- no cross-job identity
/// collision").
/// </summary>
public sealed class BinariesDownloadToolTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("wp-binaries-download-tool-").FullName;

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
	/// Class-killer shape: parses argv the way issue #1482's documented contract
	/// requires and REJECTS an undocumented flag combination with usage + <c>exit 2</c>,
	/// so a regression to a stale command shape cannot silently pass. Every call is
	/// appended to calls.log for order/shape assertions.
	/// </summary>
	private string RealContractStub(int exitCode = 0, string stdout = "")
	{
		string logAppend = $"echo \"$*\" >> \"{Path.Combine(_root, "calls.log")}\"";
		string usage = "Usage: vcf-download-tool binaries download --id=<id> --depot-store=<depotStore> [--ceip=<ceip>]";

		return Script(
			$$"""
			{{logAppend}}
			sub1="$1"; sub2="$2"
			shift 2 2>/dev/null || true
			if [ "$sub1" != "binaries" ] || [ "$sub2" != "download" ]; then
			  echo "{{usage}}" 1>&2
			  exit 2
			fi
			have_id=0
			have_depot_store=0
			for arg in "$@"; do
			  case "$arg" in
			    --id=*) have_id=1 ;;
			    --depot-store=*) have_depot_store=1 ;;
			    --depot-download-activation-code-file=*) : ;;
			    --ceip=ENABLE|--ceip=DISABLE) : ;;
			    *)
			      echo "Unknown option: $arg" 1>&2
			      echo "{{usage}}" 1>&2
			      exit 2
			      ;;
			  esac
			done
			if [ "$have_id" -ne 1 ] || [ "$have_depot_store" -ne 1 ]; then
			  echo "Missing required option" 1>&2
			  echo "{{usage}}" 1>&2
			  exit 2
			fi
			cat <<'STDOUT_EOF'
			{{stdout}}
			STDOUT_EOF
			exit {{exitCode}}
			""");
	}

	/// <summary>
	/// Mirrors <see cref="RealContractStub"/>'s argv shape but additionally REJECTS
	/// (usage + exit 2) any invocation missing
	/// <c>--depot-download-activation-code-file=&lt;path&gt;</c> or whose value does not
	/// match <paramref name="expectedActivationCodePath"/> exactly -- issue #1482 round-1
	/// review Finding 1's class-killer: removing the flag from
	/// <c>BinariesDownloadTool.DownloadAsync</c>'s constructed argv (the exact regression
	/// under review) makes this stub exit 2 and the result NOT be Ok.
	/// </summary>
	private string ActivationCodeRequiringStub(string expectedActivationCodePath, int exitCode = 0, string stdout = "")
	{
		string logAppend = $"echo \"$*\" >> \"{Path.Combine(_root, "calls.log")}\"";
		string usage = "Usage: vcf-download-tool binaries download --id=<id> --depot-store=<depotStore> --depot-download-activation-code-file=<path> [--ceip=<ceip>]";

		return Script(
			$$"""
			{{logAppend}}
			sub1="$1"; sub2="$2"
			shift 2 2>/dev/null || true
			if [ "$sub1" != "binaries" ] || [ "$sub2" != "download" ]; then
			  echo "{{usage}}" 1>&2
			  exit 2
			fi
			have_id=0
			have_depot_store=0
			have_activation_code=0
			for arg in "$@"; do
			  case "$arg" in
			    --id=*) have_id=1 ;;
			    --depot-store=*) have_depot_store=1 ;;
			    --depot-download-activation-code-file={{expectedActivationCodePath}}) have_activation_code=1 ;;
			    --ceip=ENABLE|--ceip=DISABLE) : ;;
			    *)
			      echo "Unknown option: $arg" 1>&2
			      echo "{{usage}}" 1>&2
			      exit 2
			      ;;
			  esac
			done
			if [ "$have_id" -ne 1 ] || [ "$have_depot_store" -ne 1 ] || [ "$have_activation_code" -ne 1 ]; then
			  echo "Missing required option" 1>&2
			  echo "{{usage}}" 1>&2
			  exit 2
			fi
			cat <<'STDOUT_EOF'
			{{stdout}}
			STDOUT_EOF
			exit {{exitCode}}
			""");
	}

	private static string Script(string body) => "#!/bin/sh\n" + body;

	private BinariesDownloadTool CreateTool(string script, out string callLogPath, IManagedToolPresenceChecker? presenceChecker = null)
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
			BinariesDownloadTimeout = TimeSpan.FromSeconds(10),
		};
		return new BinariesDownloadTool(Options.Create(options), presenceChecker ?? new AlwaysPresent());
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

	private string WriteCodeFile(string name = "code.txt")
	{
		string path = Path.Combine(_root, name);
		File.WriteAllText(path, "a-code");
		return path;
	}

	[Fact]
	public async Task WellFormedInvocation_AgainstRealContractStub_ReturnsOk()
	{
		// AC1 class-killer: the stub REJECTS undocumented flags (exit 2 + usage). If
		// DownloadAsync ever regressed off the documented `binaries download --id=...
		// --depot-store=... --ceip=DISABLE` shape, this stub would exit 2 and the
		// result would NOT be Ok.
		BinariesDownloadTool tool = CreateTool(RealContractStub(exitCode: 0, stdout: "download complete"), out string callLogPath);
		string depotDir = Path.Combine(_root, "depot");
		string identityHome = Path.Combine(_root, "identity", "job-a");
		string codeFile = WriteCodeFile();

		BinariesDownloadResult result = await tool.DownloadAsync(
			"vcf-9.1.0-bundle-01", depotDir, codeFile, identityHome, "asset-aaa", CancellationToken.None);

		Assert.True(result.Succeeded);
		Assert.Contains("download complete", result.Stdout, StringComparison.Ordinal);
		Assert.True(Directory.Exists(depotDir));

		string invocation = File.ReadAllText(callLogPath);
		Assert.Contains("binaries download", invocation, StringComparison.Ordinal);
		Assert.Contains("--id=vcf-9.1.0-bundle-01", invocation, StringComparison.Ordinal);
		Assert.Contains($"--depot-store={depotDir}", invocation, StringComparison.Ordinal);
		Assert.Contains("--ceip=DISABLE", invocation, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ToolMissing_FailsWithoutInvoking()
	{
		BinariesDownloadTool tool = CreateTool(RealContractStub(), out _, new NeverPresent());

		BinariesDownloadResult result = await tool.DownloadAsync(
			"vcf-bundle", Path.Combine(_root, "depot"), WriteCodeFile(), Path.Combine(_root, "identity"), "asset-aaa",
			CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Contains("not installed", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ToolRejectsCode_IsClassifiedAsAuthFailure()
	{
		BinariesDownloadTool tool = CreateTool(
			RealContractStub(exitCode: 3, stdout: "Authentication failed: activation code is expired or revoked."), out _);

		BinariesDownloadResult result = await tool.DownloadAsync(
			"vcf-bundle", Path.Combine(_root, "depot"), WriteCodeFile(), Path.Combine(_root, "identity"), "asset-aaa",
			CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.True(result.IsAuthFailure);
		Assert.False(result.IsThrottled);
		Assert.False(result.IsDiskFailure);
		Assert.Contains("activation code", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task NetworkUnreachable_IsNotAuthFailure()
	{
		BinariesDownloadTool tool = CreateTool(
			RealContractStub(exitCode: 5, stdout: "Could not resolve host: depot.example.invalid: connection timed out."), out _);

		BinariesDownloadResult result = await tool.DownloadAsync(
			"vcf-bundle", Path.Combine(_root, "depot"), WriteCodeFile(), Path.Combine(_root, "identity"), "asset-aaa",
			CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.False(result.IsAuthFailure);
		Assert.False(result.IsThrottled);
		Assert.False(result.IsDiskFailure);
		Assert.Contains("network", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task DiskFull_IsClassifiedAsDiskFailure_NeverAuth()
	{
		BinariesDownloadTool tool = CreateTool(
			RealContractStub(exitCode: 28, stdout: "write failed: /vcf/PROD/bundle.tar: No space left on device"), out _);

		BinariesDownloadResult result = await tool.DownloadAsync(
			"vcf-bundle", Path.Combine(_root, "depot"), WriteCodeFile(), Path.Combine(_root, "identity"), "asset-aaa",
			CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.True(result.IsDiskFailure);
		Assert.False(result.IsAuthFailure);
		Assert.False(result.IsThrottled);
		Assert.Contains("disk", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task VendorThrottled_IsClassifiedAsThrottle_NeverAuth()
	{
		BinariesDownloadTool tool = CreateTool(
			RealContractStub(exitCode: 8, stdout: "429 Too Many Requests: rate limit exceeded for this identity, try again later."), out _);

		BinariesDownloadResult result = await tool.DownloadAsync(
			"vcf-bundle", Path.Combine(_root, "depot"), WriteCodeFile(), Path.Combine(_root, "identity"), "asset-aaa",
			CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.True(result.IsThrottled);
		Assert.False(result.IsAuthFailure);
		Assert.False(result.IsDiskFailure);
		Assert.Contains("rate-limited", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task AmbiguousNonzeroExit_IsConservativeNonAuthFailure()
	{
		BinariesDownloadTool tool = CreateTool(
			RealContractStub(exitCode: 9, stdout: "internal error: something unexpected went wrong."), out _);

		BinariesDownloadResult result = await tool.DownloadAsync(
			"vcf-bundle", Path.Combine(_root, "depot"), WriteCodeFile(), Path.Combine(_root, "identity"), "asset-aaa",
			CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.False(result.IsAuthFailure);
		Assert.False(result.IsThrottled);
		Assert.False(result.IsDiskFailure);
	}

	/// <summary>
	/// The concurrency AC's class-killer: two invocations given DIFFERENT job-scoped
	/// identity homes and different asset ids must each seed and use ONLY their own
	/// <c>machine_id</c> -- neither ever observes the other's, proving job-scoped
	/// identity isolation (issue #1482 AC / grill decision R2-8) independent of
	/// whether issue #790's shared-home fix has landed.
	/// </summary>
	[Fact]
	public async Task ConcurrentJobs_WithDifferentIdentityHomes_NeverCollideOnMachineId()
	{
		string stub = Script(
			$$"""
			echo "$*" >> "{{Path.Combine(_root, "calls.log")}}"
			cat "$HOME/.local/share/vmware/vdt/machine_id" >> "{{Path.Combine(_root, "seen-machine-ids.log")}}"
			echo >> "{{Path.Combine(_root, "seen-machine-ids.log")}}"
			exit 0
			""");
		BinariesDownloadTool tool = CreateTool(stub, out _);

		string identityHomeA = Path.Combine(_root, "identity", "job-a");
		string identityHomeB = Path.Combine(_root, "identity", "job-b");

		Task<BinariesDownloadResult> taskA = tool.DownloadAsync(
			"bundle-a", Path.Combine(_root, "depot-a"), WriteCodeFile("code-a.txt"), identityHomeA, "asset-AAA", CancellationToken.None);
		Task<BinariesDownloadResult> taskB = tool.DownloadAsync(
			"bundle-b", Path.Combine(_root, "depot-b"), WriteCodeFile("code-b.txt"), identityHomeB, "asset-BBB", CancellationToken.None);

		await Task.WhenAll(taskA, taskB);

		Assert.True(taskA.Result.Succeeded);
		Assert.True(taskB.Result.Succeeded);

		string machineIdA = File.ReadAllText(Path.Combine(identityHomeA, ".local", "share", "vmware", "vdt", "machine_id"));
		string machineIdB = File.ReadAllText(Path.Combine(identityHomeB, ".local", "share", "vmware", "vdt", "machine_id"));

		Assert.Equal("asset-AAA", machineIdA);
		Assert.Equal("asset-BBB", machineIdB);
		Assert.NotEqual(machineIdA, machineIdB);
	}

	/// <summary>
	/// Issue #1482 round-1 review Finding 1 (blocker): <c>DownloadAsync</c> validates
	/// <c>activationCodePath</c> but the prior shape never put it into argv, so the tool
	/// ran with no credential at all. This is mutation-grade against exactly that
	/// regression: <see cref="ActivationCodeRequiringStub"/> exits 2 unless argv carries
	/// <c>--depot-download-activation-code-file=&lt;the exact path passed in&gt;</c> --
	/// the same live-audited flag spelling <c>DepotIdentityTool.cs</c> uses for
	/// <c>metadata download</c> (issue #791). Removing the flag from production code (or
	/// mismatching its value) makes this stub reject the call and the result stop being
	/// Ok, so this test cannot pass against the pre-fix code.
	/// </summary>
	[Fact]
	public async Task WellFormedInvocation_PutsTheActivationCodeFlagAndPathInArgv()
	{
		string codeFile = WriteCodeFile();
		BinariesDownloadTool tool = CreateTool(
			ActivationCodeRequiringStub(codeFile, exitCode: 0, stdout: "download complete"), out string callLogPath);
		string depotDir = Path.Combine(_root, "depot");
		string identityHome = Path.Combine(_root, "identity", "job-a");

		BinariesDownloadResult result = await tool.DownloadAsync(
			"vcf-9.1.0-bundle-01", depotDir, codeFile, identityHome, "asset-aaa", CancellationToken.None);

		Assert.True(result.Succeeded);

		string invocation = File.ReadAllText(callLogPath);
		Assert.Contains($"--depot-download-activation-code-file={codeFile}", invocation, StringComparison.Ordinal);
	}

	/// <summary>
	/// Issue #1482 round-1 review Finding 2 (blocker): <c>RunAsync</c> previously awaited
	/// <c>WaitForExitAsync</c> with both pipes redirected and unread -- a child writing
	/// more than the OS pipe buffer (~64 KiB) to either stream would fill it and block
	/// forever, since nothing was draining it and the process could never exit. This
	/// stub writes 200 KiB (well past that threshold) to BOTH stdout and stderr before
	/// exiting; the test bounds itself to a fraction of the tool's own configured
	/// timeout, so a regression back to the deadlock shows up as this test timing out
	/// long before the real 4-hour tool timeout would ever fire, not as a silent hang.
	/// </summary>
	[Fact]
	public async Task LargeStdoutAndStderr_PastPipeBufferSize_NeverDeadlocksAndCapturesVerbatim()
	{
		const int lineCount = 4000; // ~200 KiB per stream at 50 bytes/line, several times the ~64 KiB OS pipe buffer.
		string line = new string('x', 45);
		string genLoop =
			$$"""
			i=0
			while [ "$i" -lt {{lineCount}} ]; do
			  echo "{{line}}"
			  echo "{{line}}" 1>&2
			  i=$((i + 1))
			done
			""";
		string stub = Script(genLoop + "\nexit 0\n");

		BinariesDownloadTool tool = CreateTool(stub, out _);
		string depotDir = Path.Combine(_root, "depot");
		string identityHome = Path.Combine(_root, "identity", "job-large-output");
		string codeFile = WriteCodeFile();

		using CancellationTokenSource boundedTimeout = new(TimeSpan.FromSeconds(20));
		BinariesDownloadResult result = await tool.DownloadAsync(
			"vcf-bundle-large", depotDir, codeFile, identityHome, "asset-aaa", boundedTimeout.Token);

		Assert.True(result.Succeeded);
		int expectedLength = lineCount * (line.Length + 1);
		Assert.Equal(expectedLength, result.Stdout.Length);
		Assert.All(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries), l => Assert.Equal(line, l));
	}
}
