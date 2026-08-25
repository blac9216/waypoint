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
	public async Task SeedMachineIdentity_WhenMachineIdAlreadyExists_NeverOverwritesIt()
	{
		DepotIdentityTool tool = CreateTool(Script("exit 0\n"), out _);
		string path = MachineIdPath();
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, "tool-generated-identity");

		// Read-first / no silent rotation (#778): an established identity is left intact.
		await tool.SeedMachineIdentityAsync(InventedAssetId, CancellationToken.None);

		Assert.Equal("tool-generated-identity", File.ReadAllText(path));
	}

	[Fact]
	public async Task ValidateActivationCode_WithExpectedAssetId_SeedsMachineIdBeforeInvokingTool()
	{
		// The stub tool reads machine_id and echoes it, letting the test assert the file
		// the tool checks the code against was seeded to the pairing asset_id first --
		// this is the rebuild-survivability case: identity home starts empty.
		string script = Script(
			$"""
			id="$(cat "$XDG_DATA_HOME/vmware/vdt/machine_id" 2>/dev/null)"
			echo "$id" >> "{Path.Combine(_root, "seen-machine-id.log")}"
			exit 0
			""");
		DepotIdentityTool tool = CreateTool(script, out _);
		string codeFile = Path.Combine(_root, "code.txt");
		File.WriteAllText(codeFile, "irrelevant-code-body");

		DepotValidationResult result = await tool.ValidateActivationCodeAsync(codeFile, InventedAssetId, CancellationToken.None);

		Assert.True(result.Succeeded);
		Assert.Equal(InventedAssetId, File.ReadAllText(MachineIdPath()));
		Assert.Equal(InventedAssetId, File.ReadAllText(Path.Combine(_root, "seen-machine-id.log")).Trim());
	}

	[Fact]
	public async Task ValidateActivationCode_WithNoExpectedAssetId_DoesNotSeedMachineId()
	{
		DepotIdentityTool tool = CreateTool(Script("exit 0\n"), out _);
		string codeFile = Path.Combine(_root, "code.txt");
		File.WriteAllText(codeFile, "irrelevant-code-body");

		DepotValidationResult result = await tool.ValidateActivationCodeAsync(codeFile, expectedAssetId: null, CancellationToken.None);

		Assert.True(result.Succeeded);
		Assert.False(File.Exists(MachineIdPath()));
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
