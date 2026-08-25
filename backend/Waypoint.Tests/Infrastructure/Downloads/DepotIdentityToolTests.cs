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
/// Issue #772: <c>GetDepotIdAsync</c> against a stub <c>vcf-download-tool</c> shell
/// script (invented fixture -- no vendor prose beyond the minimal matched shape the
/// live-validation evidence in issue #772 described: a <c>*Welcome...*</c> banner
/// line, a <c>Version:</c> line, a <c>Log file:</c> line, and the tool's
/// "No Software depot ID generated" phrase). Proves: a fresh identity is generated
/// (get -> banner -> generate -> parsed ID), an existing identity is read without
/// regenerating, banner/garbage/empty stdout is rejected rather than stored, and a
/// nonzero exit or timeout is a typed failure.
/// </summary>
public sealed class DepotIdentityToolTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("wp-depot-identity-").FullName;

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
	/// Writes an executable <c>sh</c> script standing in for <c>vcf-download-tool</c>.
	/// The script dispatches on whether argv contains "generate" or "get" and prints
	/// the corresponding canned block; a per-call log lets tests assert call order and
	/// count without inspecting process internals.
	/// </summary>
	private DepotIdentityTool CreateTool(string script, out string callLogPath)
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
			EnrollmentCommandTimeout = TimeSpan.FromSeconds(5),
			ActivationCodeValidationTimeout = TimeSpan.FromSeconds(10),
			ActivationCodeValidationScratchDirectoryName = "validate-scratch",
		};
		return new DepotIdentityTool(Options.Create(options), new AlwaysPresent());
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

	private const string BannerPreamble =
		"""
		*Welcome to VCF Download Tool*
		Version: 9.1.0.0400

		""";

	private static string NotGeneratedBlock() =>
		BannerPreamble +
		"No Software depot ID generated. Please use 'vcf-download-tool configuration generate --software-depot-id'\n" +
		"Log file: /var/lib/waypoint/managed-tool/identity/log/vdt.log\n";

	private static string GeneratedBlock(string id) =>
		BannerPreamble +
		$"Software Depot ID: {id}\n" +
		"Log file: /var/lib/waypoint/managed-tool/identity/log/vdt.log\n";

	private static string Script(string body) => "#!/bin/sh\n" + body;

	/// <summary>
	/// A stub <c>vcf-download-tool</c> that models the REAL 9.1.0.0400 command contract
	/// (issue #791, AC4 -- the fixture-vs-real-contract class-killer). It parses argv the
	/// way the real tool documents and REJECTS undocumented flag combinations with usage +
	/// <c>exit 2</c>, exactly as the live tool does, so an invalid command line (e.g. the old
	/// <c>configuration get --software-depot-id --depot-download-activation-code-file</c>)
	/// can never silently pass a test again. The only authenticated operation it accepts is
	/// <c>metadata download --depot-store=&lt;dir&gt; --depot-download-activation-code-file=&lt;file&gt;</c>
	/// (with an optional <c>--ceip=</c>); on a well-formed invocation it exits
	/// <paramref name="metadataDownloadExit"/> after emitting <paramref name="metadataDownloadStdout"/>.
	/// Every call is appended to calls.log for order/shape assertions.
	/// </summary>
	private string RealContractStub(int metadataDownloadExit = 0, string metadataDownloadStdout = "")
	{
		string logAppend = $"echo \"$*\" >> \"{Path.Combine(_root, "calls.log")}\"";
		string usage =
			"Usage: vcf-download-tool metadata download [--ceip=<ceip>] -d=<depotStore> --depot-download-activation-code-file=<file>";

		// POSIX sh: walk argv, recognise ONLY the documented tokens per subcommand. Anything
		// else -> usage on stderr + exit 2 (the real tool's rejection shape).
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

	[Fact]
	public async Task FreshIdentity_GetReportsNotGenerated_GenerateProducesAndParsesId()
	{
		string script = Script(
			$"""
			echo "$*" >> "{Path.Combine(_root, "calls.log")}"
			case "$*" in
			  *generate*)
			    cat <<'EOF'
			{GeneratedBlock("WPT-0001-DEPOT-ID")}
			EOF
			    ;;
			  *)
			    cat <<'EOF'
			{NotGeneratedBlock()}
			EOF
			    ;;
			esac
			exit 0
			""");
		DepotIdentityTool tool = CreateTool(script, out string callLogPath);

		DepotIdentityResult result = await tool.GetDepotIdAsync(CancellationToken.None);

		Assert.True(result.Succeeded);
		Assert.Equal("WPT-0001-DEPOT-ID", result.DepotId);

		string[] calls = File.ReadAllLines(callLogPath);
		Assert.Equal(2, calls.Length);
		Assert.Contains("get", calls[0]);
		Assert.Contains("generate", calls[1]);
	}

	[Fact]
	public async Task ExistingIdentity_GetAlreadyReturnsId_GenerateIsNeverInvoked()
	{
		string script = Script(
			$"""
			echo "$*" >> "{Path.Combine(_root, "calls.log")}"
			cat <<'EOF'
			{GeneratedBlock("WPT-EXISTING-ID")}
			EOF
			exit 0
			""");
		DepotIdentityTool tool = CreateTool(script, out string callLogPath);

		DepotIdentityResult result = await tool.GetDepotIdAsync(CancellationToken.None);

		Assert.True(result.Succeeded);
		Assert.Equal("WPT-EXISTING-ID", result.DepotId);

		string[] calls = File.ReadAllLines(callLogPath);
		Assert.Single(calls);
		Assert.DoesNotContain("generate", calls[0]);
	}

	[Fact]
	public async Task BannerNeverGenerated_EvenAfterGenerateCallFails_IsRejectedNotStored()
	{
		// Both calls return the "not generated" banner (a broken/misbehaving tool) --
		// the regression fixture for issue #772's core defect: the banner must never
		// be returned as a successful DepotId.
		string script = Script(
			$"""
			cat <<'EOF'
			{NotGeneratedBlock()}
			EOF
			exit 0
			""");
		DepotIdentityTool tool = CreateTool(script, out _);

		DepotIdentityResult result = await tool.GetDepotIdAsync(CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Null(result.DepotId);
		Assert.Contains("No Software depot ID generated", result.FailureReason);
	}

	[Fact]
	public async Task GarbageOutput_IsRejected()
	{
		string script = Script(
			"""
			echo "some unrelated multi word prose with no id shaped token at all"
			exit 0
			""");
		DepotIdentityTool tool = CreateTool(script, out _);

		DepotIdentityResult result = await tool.GetDepotIdAsync(CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Null(result.DepotId);
	}

	[Fact]
	public async Task EmptyStdout_IsRejected()
	{
		string script = Script("exit 0\n");
		DepotIdentityTool tool = CreateTool(script, out _);

		DepotIdentityResult result = await tool.GetDepotIdAsync(CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Contains("no output", result.FailureReason);
	}

	[Fact]
	public async Task NonzeroExit_IsFailedNeverStored()
	{
		string script = Script(
			"""
			echo "unexpected tool error" 1>&2
			exit 3
			""");
		DepotIdentityTool tool = CreateTool(script, out _);

		DepotIdentityResult result = await tool.GetDepotIdAsync(CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Null(result.DepotId);
		Assert.Contains("code 3", result.FailureReason);
	}

	[Fact]
	public async Task Timeout_IsFailedNeverStored()
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
			EnrollmentCommandTimeout = TimeSpan.FromMilliseconds(200),
		};
		DepotIdentityTool tool = new(Options.Create(options), new AlwaysPresent());

		DepotIdentityResult result = await tool.GetDepotIdAsync(CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Null(result.DepotId);
		Assert.Contains("timed out", result.FailureReason);
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
		DepotIdentityTool tool = new(Options.Create(options), new NeverPresent());

		DepotIdentityResult result = await tool.GetDepotIdAsync(CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Contains("not installed", result.FailureReason);
	}

	[Theory]
	[InlineData("WPT-0001-DEPOT-ID", true)]
	[InlineData("purely-alphabetic-no-digits", true)] // hyphen alone satisfies the digit-or-hyphen guard
	[InlineData("PurelyAlphabeticNoPunctuation", false)] // plain word -- no digit/hyphen, rejected like "Software"/"Depot"
	[InlineData("ab", false)] // too short
	public void TryParseDepotId_BareLine_MatchesTokenShapeStrictly(string line, bool expectSucceeded)
	{
		bool parsed = DepotIdentityTool.TryParseDepotId(line, out string? id, out _);
		Assert.Equal(expectSucceeded, parsed);
		if (expectSucceeded)
		{
			Assert.Equal(line, id);
		}
	}

	[Fact]
	public void TryParseDepotId_BannerOnly_IsRejected()
	{
		bool parsed = DepotIdentityTool.TryParseDepotId(BannerPreamble, out string? id, out string? reason);
		Assert.False(parsed);
		Assert.Null(id);
		Assert.NotNull(reason);
	}

	[Fact]
	public void TryParseDepotId_MultipleCandidateTokens_IsRejected()
	{
		string stdout = BannerPreamble + "token-one\ntoken-two\n";
		bool parsed = DepotIdentityTool.TryParseDepotId(stdout, out string? id, out _);
		Assert.False(parsed);
		Assert.Null(id);
	}

	[Fact]
	public void TryParseDepotId_ProseSentenceWithEmbeddedId_ExtractsTheToken()
	{
		string stdout = BannerPreamble + "Software Depot ID: WPT-0001-DEPOT-ID\nLog file: /x/log/vdt.log\n";
		bool parsed = DepotIdentityTool.TryParseDepotId(stdout, out string? id, out _);
		Assert.True(parsed);
		Assert.Equal("WPT-0001-DEPOT-ID", id);
	}

	// Issue #781 (regression found in epic #667 re-validation): the real
	// vcf-download-tool 9.1.0.0400 success line is one prose sentence that carries the
	// generated Software Depot ID as a UUID TWICE -- once as the serviceId= query
	// parameter of a register URL (with a trailing '.') and once after the
	// "Software depot ID:" label -- plus a hyphenated URL path word ("download-manager").
	// The #778 parser saw three embedded tokens on that line, matched none of them
	// (count != 1), and rejected valid output. The UUID and hosts below are INVENTED but
	// mirror the real success line's structure exactly.
	private const string RealShapeUuid = "a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d";

	private static string RealShapeSuccessLine(string uuid) =>
		"Use this link to register https://depot.example.invalid/vcf/clm/download-manager/register?serviceId="
		+ uuid + ". Alternatively login at https://portal.example.invalid, select Software depot Registration "
		+ "and use this Software depot ID: " + uuid;

	[Fact]
	public void TryParseDepotId_RealToolProseWithDuplicateUuidAndHyphenatedUrlWord_ExtractsTheUuid()
	{
		string stdout = BannerPreamble + RealShapeSuccessLine(RealShapeUuid) + "\nLog file: /x/log/vdt.log\n";
		bool parsed = DepotIdentityTool.TryParseDepotId(stdout, out string? id, out _);
		Assert.True(parsed);
		Assert.Equal(RealShapeUuid, id);
	}

	[Fact]
	public void TryParseDepotId_MultipleDistinctUuids_IsRejectedAsAmbiguous()
	{
		string stdout = BannerPreamble
			+ "Software depot ID: " + RealShapeUuid + "\n"
			+ "Software depot ID: f6e5d4c3-b2a1-4098-8765-4321fedcba09\n";
		bool parsed = DepotIdentityTool.TryParseDepotId(stdout, out string? id, out string? reason);
		Assert.False(parsed);
		Assert.Null(id);
		Assert.NotNull(reason);
	}

	// ---- Issue #787: machine_id seeding from a code's decoded asset_id ----

	private string MachineIdPath() =>
		Path.Combine(_root, "identity", ".local", "share", "vmware", "vdt", "machine_id");

	private const string InventedAssetId = "wpt-787-asset-0001"; // invented pairing/asset id fixture

	[Fact]
	public async Task SeedMachineIdentity_WritesAssetIdAtomically_WithRestrictivePermissions()
	{
		// A trivial always-zero stub is enough -- SeedMachineIdentityAsync never invokes
		// the tool, it only writes the identity file the tool will later check.
		DepotIdentityTool tool = CreateTool(Script("exit 0\n"), out _);

		await tool.SeedMachineIdentityAsync(InventedAssetId, CancellationToken.None);

		string path = MachineIdPath();
		Assert.True(File.Exists(path));
		// Byte-for-byte the asset_id, no trailing newline -- matches the sibling contract.
		Assert.Equal(InventedAssetId, File.ReadAllText(path));
		if (!OperatingSystem.IsWindows())
		{
			UnixFileMode mode = File.GetUnixFileMode(path);
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
		}

		// No stray temp files were left behind in the vdt directory.
		string vdtDir = Path.GetDirectoryName(path)!;
		Assert.Empty(Directory.GetFiles(vdtDir, ".machine_id.*"));
	}

	[Fact]
	public async Task SeedMachineIdentity_WhenMachineIdAlreadyExists_OverwritesItWithTheNewAssetId()
	{
		DepotIdentityTool tool = CreateTool(Script("exit 0\n"), out _);
		string path = MachineIdPath();
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, "stale-identity-from-a-previous-code");

		// Owner decision 2026-08-25: machine_id is DERIVED state -- identity follows the
		// code, so seeding overwrites whatever is there with the current code's asset_id
		// (swapping in a different working code just works, no reset ceremony).
		await tool.SeedMachineIdentityAsync(InventedAssetId, CancellationToken.None);

		Assert.Equal(InventedAssetId, File.ReadAllText(path));
		if (!OperatingSystem.IsWindows())
		{
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
		}

		// No stray temp files were left behind by the overwrite.
		Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, ".machine_id.*"));
	}

	[Fact]
	public async Task ValidateActivationCode_DoesNotSeedMachineId_TheCallerSeedsIt()
	{
		// Owner decision 2026-08-25: validation means only "the tool accepts this code."
		// The caller seeds machine_id from the code's own asset_id BEFORE this call, so
		// ValidateActivationCodeAsync no longer touches the identity file itself. The stub
		// models the REAL contract (issue #791), so a green result here also proves the
		// validate command line is the accepted metadata-download shape.
		DepotIdentityTool tool = CreateTool(RealContractStub(metadataDownloadExit: 0), out _);
		string codeFile = Path.Combine(_root, "code.txt");
		File.WriteAllText(codeFile, "irrelevant-code-body");

		DepotValidationResult result = await tool.ValidateActivationCodeAsync(codeFile, CancellationToken.None);

		Assert.True(result.Succeeded);
		Assert.False(File.Exists(MachineIdPath()));
	}

	// ---- Issue #791: validation-by-use against the real metadata-download contract ----

	[Fact]
	public async Task ValidateActivationCode_RunsMetadataDownloadWithDepotStoreAndCodeFile_AgainstRealContractStub()
	{
		// AC4 class-killer: the stub REJECTS undocumented flags (exit 2 + usage). If
		// ValidateActivationCodeAsync ever regressed to the old
		// `configuration get ... --depot-download-activation-code-file` shape (or any other
		// invalid command line), this stub would exit 2 and the result would NOT be Ok.
		DepotIdentityTool tool = CreateTool(RealContractStub(metadataDownloadExit: 0), out string callLogPath);
		string codeFile = Path.Combine(_root, "code.txt");
		File.WriteAllText(codeFile, "a-code");

		DepotValidationResult result = await tool.ValidateActivationCodeAsync(codeFile, CancellationToken.None);

		Assert.True(result.Succeeded);
		string invocation = File.ReadAllText(callLogPath);
		Assert.Contains("metadata download", invocation, StringComparison.Ordinal);
		Assert.Contains("--depot-store=", invocation, StringComparison.Ordinal);
		Assert.Contains("--depot-download-activation-code-file=", invocation, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ValidateActivationCode_ScratchDepotStore_IsCreatedFreshAndRemovedOnEveryPath()
	{
		// The scratch depot-store lives under ToolStatePath/<scratch dir name> and must be
		// gone once validation returns, on both success and failure.
		string scratchRoot = Path.Combine(_root, "validate-scratch");

		DepotIdentityTool okTool = CreateTool(RealContractStub(metadataDownloadExit: 0), out _);
		string codeFile = Path.Combine(_root, "code.txt");
		File.WriteAllText(codeFile, "a-code");
		Assert.True((await okTool.ValidateActivationCodeAsync(codeFile, CancellationToken.None)).Succeeded);
		Assert.Empty(Directory.Exists(scratchRoot) ? Directory.GetDirectories(scratchRoot) : []);

		DepotIdentityTool failTool = CreateTool(
			RealContractStub(metadataDownloadExit: 4, metadataDownloadStdout: "Activation Code rejected: expired."), out _);
		Assert.False((await failTool.ValidateActivationCodeAsync(codeFile, CancellationToken.None)).Succeeded);
		Assert.Empty(Directory.Exists(scratchRoot) ? Directory.GetDirectories(scratchRoot) : []);
	}

	[Fact]
	public async Task ValidateActivationCode_ToolRejectsCode_IsAuthFailure()
	{
		DepotIdentityTool tool = CreateTool(
			RealContractStub(metadataDownloadExit: 3, metadataDownloadStdout: "Authentication failed: activation code is expired or revoked."),
			out _);
		string codeFile = Path.Combine(_root, "code.txt");
		File.WriteAllText(codeFile, "a-code");

		DepotValidationResult result = await tool.ValidateActivationCodeAsync(codeFile, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.True(result.IsAuthFailure);
	}

	[Fact]
	public async Task ValidateActivationCode_NetworkUnreachable_IsNotAuthFailure()
	{
		// AC1: a network-unreachable environment must produce network-classified guidance,
		// never auth_failing -- even though the invocation completed with a nonzero exit.
		DepotIdentityTool tool = CreateTool(
			RealContractStub(metadataDownloadExit: 5, metadataDownloadStdout: "Could not resolve host: depot.example.invalid: connection timed out."),
			out _);
		string codeFile = Path.Combine(_root, "code.txt");
		File.WriteAllText(codeFile, "a-code");

		DepotValidationResult result = await tool.ValidateActivationCodeAsync(codeFile, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.False(result.IsAuthFailure);
		Assert.Contains("network", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ValidateActivationCode_AmbiguousNonzero_IsConservativeNonAuthFailure()
	{
		DepotIdentityTool tool = CreateTool(
			RealContractStub(metadataDownloadExit: 9, metadataDownloadStdout: "internal error: something unexpected went wrong."),
			out _);
		string codeFile = Path.Combine(_root, "code.txt");
		File.WriteAllText(codeFile, "a-code");

		DepotValidationResult result = await tool.ValidateActivationCodeAsync(codeFile, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.False(result.IsAuthFailure);
	}

	[Fact]
	public void RealContractStub_RejectsTheOldInvalidValidateCommand_WithUsageAndExit2()
	{
		// Regression (issue #791, AC4): prove the class-killer stub actually rejects the old
		// invalid command line the way the real tool does. Invoking the stub directly with
		// the pre-fix `configuration get --software-depot-id --depot-download-activation-code-file`
		// combination must exit 2 with usage -- so if the production code ever emits it again,
		// the validate tests above go red.
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
		System.Diagnostics.ProcessStartInfo startInfo = new(executablePath)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		foreach (string arg in args)
		{
			startInfo.ArgumentList.Add(arg);
		}

		using System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo)!;
		string stderr = process.StandardError.ReadToEnd();
		process.StandardOutput.ReadToEnd();
		process.WaitForExit();
		return (process.ExitCode, stderr);
	}

	[Fact]
	public async Task RealToolProseSuccessLine_EndToEnd_GeneratesAndParsesUuid()
	{
		string script = Script(
			$"""
			echo "$*" >> "{Path.Combine(_root, "calls.log")}"
			case "$*" in
			  *generate*)
			    cat <<'EOF'
			*Welcome to VCF Download Tool*
			Version: 9.1.0.0400

			{RealShapeSuccessLine(RealShapeUuid)}
			Log file: /var/lib/waypoint/managed-tool/identity/log/vdt.log
			EOF
			    ;;
			  *)
			    cat <<'EOF'
			{NotGeneratedBlock()}
			EOF
			    ;;
			esac
			exit 0
			""");
		DepotIdentityTool tool = CreateTool(script, out _);

		DepotIdentityResult result = await tool.GetDepotIdAsync(CancellationToken.None);

		Assert.True(result.Succeeded);
		Assert.Equal(RealShapeUuid, result.DepotId);
	}
}
