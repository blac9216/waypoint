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

using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Waypoint.Core.Downloads;

namespace Waypoint.Infrastructure.Downloads;

/// <inheritdoc cref="IDepotIdentityTool"/>
/// <remarks>
/// Mirrors <c>ManagedToolDistributionInstaller.SmokeTestAsync</c>'s bounded
/// noninteractive process pattern: stdin closed immediately, a linked
/// timeout/cancellation token, and a hard kill on timeout.
///
/// Issue #772: live validation against the real tool proved <c>configuration get
/// --software-depot-id</c> exits 0 even when no identity has been generated yet --
/// it prints a human banner telling the operator to run <c>configuration generate</c>
/// instead, and exit code 0 alone is NOT proof an ID was produced. So this reads
/// first (<c>configuration get</c>, cheap and idempotent -- never regenerates/rotates
/// an existing ID), and only runs <c>configuration generate --software-depot-id</c>
/// when the read comes back as the "nothing generated yet" banner rather than a real
/// ID. Either call's stdout is strictly parsed for a single plausible ID token
/// (<see cref="TryParseDepotId"/>) rather than trusted verbatim -- a banner, an error
/// blob, or multi-line prose is rejected as a typed failure, never stored as an
/// identity.
/// </remarks>
public sealed class DepotIdentityTool : IDepotIdentityTool
{
	private const string GetArgument = "configuration get --software-depot-id";
	private const string GenerateArgument = "configuration generate --software-depot-id";

	/// <summary>
	/// Path, relative to the isolated identity home's <c>XDG_DATA_HOME</c>
	/// (<c>&lt;identity&gt;/.local/share</c>), of the file the real tool checks an
	/// Activation Code against: it accepts a code only when the local <c>machine_id</c>
	/// equals the <c>asset_id</c> the code was issued against. The sibling reference
	/// (<c>../vcf-docker-download/Dockerfile</c>) writes exactly
	/// <c>~/.local/share/vmware/vdt/machine_id</c>; Waypoint mirrors that layout under
	/// the managed volume's identity home rather than a container-global root home.
	/// </summary>
	private static readonly string[] MachineIdRelativeSegments = ["vmware", "vdt", "machine_id"];

	/// <summary>Distinguishing phrase from the real tool's "nothing generated yet" banner (issue #772) -- never treated as a parsed ID.</summary>
	private const string NotGeneratedPhrase = "No Software depot ID generated";

	/// <summary>
	/// A plausible Software Depot ID token: printable, no internal whitespace, no
	/// quotes, bounded length, and carrying at least one digit or hyphen so a plain
	/// English word out of surrounding prose ("Software", "Depot") never qualifies --
	/// deliberately loose on the rest since Broadcom does not publish a fixed format,
	/// but tight enough to reject banner/prose/log-path lines.
	/// </summary>
	private static readonly Regex DepotIdTokenPattern =
		new(@"^(?=[A-Za-z0-9._:-]*[0-9-])[A-Za-z0-9][A-Za-z0-9._:-]{2,127}$", RegexOptions.Compiled);

	/// <summary>Same shape as <see cref="DepotIdTokenPattern"/>, un-anchored, for locating a single candidate token embedded inside a prose line.</summary>
	private static readonly Regex EmbeddedTokenPattern =
		new(@"(?=[A-Za-z0-9._:-]*[0-9-])[A-Za-z0-9][A-Za-z0-9._:-]{5,127}", RegexOptions.Compiled);

	/// <summary>
	/// A canonical 8-4-4-4-12 UUID, matched with word-boundary anchoring so a trailing
	/// '.' or '/' from surrounding URL/prose punctuation is NOT captured. The real
	/// vcf-download-tool 9.1.0.0400 success line (issue #781) is one prose sentence that
	/// carries the generated Software Depot ID as a UUID twice -- once as the
	/// <c>serviceId=</c> query parameter of a register URL and once after the
	/// <c>Software depot ID:</c> label -- plus a hyphenated URL path word
	/// (<c>download-manager</c>). A UUID shape collapses that to the single genuine
	/// identity: the two UUID occurrences are byte-identical (deduped to one) and the
	/// hyphenated dictionary word is not a UUID so it is ignored.
	/// </summary>
	private static readonly Regex UuidTokenPattern =
		new(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled);

	private readonly IOptions<ManagedToolOptions> _options;
	private readonly IManagedToolPresenceChecker _presenceChecker;

	public DepotIdentityTool(IOptions<ManagedToolOptions> options, IManagedToolPresenceChecker presenceChecker)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(presenceChecker);
		_options = options;
		_presenceChecker = presenceChecker;
	}

	public async Task<DepotIdentityResult> GetDepotIdAsync(CancellationToken cancellationToken)
	{
		if (!_presenceChecker.IsPresent())
		{
			return DepotIdentityResult.Failed(
				$"vcf-download-tool is not installed (expected at '{_presenceChecker.DescribeExpectedLocation()}'). Install the managed tool before generating a Software Depot ID.");
		}

		ManagedToolOptions options = _options.Value;
		string identityHome = PrepareIdentityHome(options);

		DepotIdentityResult read = await InvokeAndParseAsync(GetArgument, identityHome, options, "Depot ID query", cancellationToken)
			.ConfigureAwait(false);
		if (read.Succeeded)
		{
			// An ID already exists -- idempotent read, never regenerate/rotate silently.
			return read;
		}

		// The read failed. If it failed specifically because no identity has been
		// generated yet, generate one now; any other failure (tool missing mid-call,
		// timeout, unparseable garbage) is returned as-is rather than papered over by
		// a generate attempt that would only fail the same way.
		if (!read.FailureReason?.Contains(NotGeneratedPhrase, StringComparison.OrdinalIgnoreCase) ?? true)
		{
			return read;
		}

		return await InvokeAndParseAsync(GenerateArgument, identityHome, options, "Depot ID generation", cancellationToken)
			.ConfigureAwait(false);
	}

	private static async Task<DepotIdentityResult> InvokeAndParseAsync(
		string argument, string identityHome, ManagedToolOptions options, string operationLabel, CancellationToken cancellationToken)
	{
		(bool succeeded, int exitCode, string stdout, string stderr) = await RunAsync(
			ExecutablePath(options), argument, identityHome, options, options.EnrollmentCommandTimeout, cancellationToken).ConfigureAwait(false);

		if (!succeeded)
		{
			return DepotIdentityResult.Failed($"{operationLabel} could not be started or timed out: {stderr}");
		}

		// Exit code 0 is NOT success proof by itself (issue #772) -- it is necessary
		// but the parsed stdout is what actually decides the outcome.
		if (exitCode != 0)
		{
			return DepotIdentityResult.Failed($"{operationLabel} exited with code {exitCode}: {Truncate(stderr)}");
		}

		return TryParseDepotId(stdout, out string? depotId, out string? rejectionReason)
			? DepotIdentityResult.Ok(depotId!)
			: DepotIdentityResult.Failed($"{operationLabel} succeeded but produced no usable Depot ID: {rejectionReason}");
	}

	/// <summary>
	/// Extracts a Depot ID from the tool's stdout, rejecting anything that is not a
	/// single plausible ID token. The real tool's output is a banner (a
	/// <c>*Welcome...*</c> line), a <c>Version:</c> line, a <c>Log file:</c> line, and
	/// -- on success -- either a bare ID line or a prose sentence containing one; on
	/// the "nothing generated yet" path, a sentence containing
	/// <see cref="NotGeneratedPhrase"/> instead. Banner/version/log-file lines are
	/// always stripped before a candidate is considered, so neither can be mistaken
	/// for an ID no matter which call produced them.
	/// </summary>
	internal static bool TryParseDepotId(string stdout, out string? depotId, out string? rejectionReason)
	{
		depotId = null;
		rejectionReason = null;

		if (string.IsNullOrWhiteSpace(stdout))
		{
			rejectionReason = "no output was produced.";
			return false;
		}

		string[] lines = stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
		List<string> candidates = [];
		foreach (string rawLine in lines)
		{
			string line = rawLine.Trim();
			if (line.Length == 0 || IsBannerLine(line))
			{
				continue;
			}

			if (line.Contains(NotGeneratedPhrase, StringComparison.OrdinalIgnoreCase))
			{
				rejectionReason = $"the tool reported '{NotGeneratedPhrase}' -- no identity exists yet.";
				return false;
			}

			candidates.Add(line);
		}

		if (candidates.Count == 0)
		{
			rejectionReason = "no non-banner output line was found.";
			return false;
		}

		// The real tool (issue #781) emits the ID as a UUID inside one prose sentence
		// that also contains the same UUID a second time and a hyphenated URL word, so a
		// naive "exactly one embedded token" rule rejects it. Prefer UUID-shaped tokens:
		// scan every candidate line, collect all UUID matches, and DEDUPE identical
		// values. A UUID regex naturally excludes non-ID prose (dictionary words, URL
		// path segments), and the two byte-identical UUID occurrences collapse to one.
		// Two DISTINCT UUIDs remains genuine ambiguity and is still fatal.
		HashSet<string> distinctUuids = new(StringComparer.OrdinalIgnoreCase);
		foreach (string candidate in candidates)
		{
			foreach (Match match in UuidTokenPattern.Matches(candidate))
			{
				distinctUuids.Add(match.Value);
			}
		}

		if (distinctUuids.Count == 1)
		{
			depotId = distinctUuids.First();
			return true;
		}

		if (distinctUuids.Count > 1)
		{
			rejectionReason = $"output contained multiple distinct Depot ID candidates: {Truncate(stdout)}";
			return false;
		}

		// No UUID present -- fall back to the general token rule for tools that emit a
		// non-UUID ID. A bare ID line (the common case) matches the token pattern
		// outright; a prose line is searched for exactly one embedded token instead of
		// trusting the whole sentence. If the line carries zero or more than one
		// plausible token it is rejected rather than guessed at.
		List<string> tokens = [];
		foreach (string candidate in candidates)
		{
			if (DepotIdTokenPattern.IsMatch(candidate))
			{
				tokens.Add(candidate);
				continue;
			}

			MatchCollection embedded = EmbeddedTokenPattern.Matches(candidate);
			if (embedded.Count == 1)
			{
				tokens.Add(embedded[0].Value);
			}
		}

		if (tokens.Count != 1)
		{
			rejectionReason = tokens.Count == 0
				? $"output did not contain a recognisable ID token: {Truncate(stdout)}"
				: $"output contained multiple candidate ID tokens: {Truncate(stdout)}";
			return false;
		}

		depotId = tokens[0];
		return true;
	}

	private static bool IsBannerLine(string line) =>
		line.StartsWith('*') // "*Welcome to VCF Download Tool*"
		|| line.StartsWith("Version:", StringComparison.OrdinalIgnoreCase)
		|| line.StartsWith("Log file:", StringComparison.OrdinalIgnoreCase);

	public Task SeedMachineIdentityAsync(string assetId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
		string identityHome = PrepareIdentityHome(_options.Value);
		SeedMachineId(identityHome, assetId);
		return Task.CompletedTask;
	}

	public async Task<DepotValidationResult> ValidateActivationCodeAsync(string activationCodePath, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(activationCodePath);

		if (!_presenceChecker.IsPresent())
		{
			return DepotValidationResult.Failed(
				$"vcf-download-tool is not installed (expected at '{_presenceChecker.DescribeExpectedLocation()}').");
		}

		ManagedToolOptions options = _options.Value;
		string identityHome = PrepareIdentityHome(options);

		// Issue #791: the real 9.1.0.0400 tool has no lightweight "check code" subcommand --
		// `configuration get` does NOT accept --depot-download-activation-code-file and exits
		// 2 with usage when handed one, so the prior validate command failed EVERY code
		// regardless of validity. The only authenticated operation the tool documents is
		// `metadata download --depot-store=<dir> --depot-download-activation-code-file=<file>`
		// (verified against the live `metadata download --help`), so validation is
		// validation-by-use: run a bounded metadata download against a throwaway scratch
		// depot-store. Exit 0 means Broadcom accepted the code; the scratch is discarded.
		//
		// Issue #787 (owner decision 2026-08-25): identity follows the code -- the caller has
		// already seeded machine_id from THIS code's own decoded asset_id via
		// SeedMachineIdentityAsync immediately before this call, so the tool is simply asked
		// whether it accepts the code.
		string scratchDepotStore = Path.Combine(
			options.ToolStatePath, options.ActivationCodeValidationScratchDirectoryName, Guid.NewGuid().ToString("N"));

		try
		{
			// Fresh, job-scoped, empty depot-store -- created here, removed in finally on
			// every path so a failed/partial validation never leaves scratch bytes behind.
			Directory.CreateDirectory(scratchDepotStore);

			string argument =
				$"metadata download --depot-store=\"{scratchDepotStore}\" \"--depot-download-activation-code-file={activationCodePath}\" --ceip=DISABLE";

			(bool succeeded, int exitCode, string stdout, string stderr) = await RunAsync(
				ExecutablePath(options), argument, identityHome, options, options.ActivationCodeValidationTimeout, cancellationToken)
				.ConfigureAwait(false);

			if (!succeeded)
			{
				// The invocation itself never completed (missing binary, timeout/kill) -- a
				// runner/network problem, NEVER a code rejection. A WAN metadata download that
				// times out is unreachable-Broadcom, not a bad code.
				return DepotValidationResult.Failed($"Activation Code validation could not be completed: {stderr}");
			}

			if (exitCode == 0)
			{
				return DepotValidationResult.Ok();
			}

			// The tool ran and exited nonzero. Classify honestly (issue #791): only signals
			// that genuinely indicate the credential was rejected are auth_failing; anything
			// pointing at unreachable/unresolvable/refused connectivity is a network problem
			// that must produce network-classified guidance, never auth_failing. When the tool
			// does not clearly differentiate, we classify conservatively as a (non-auth)
			// validation failure with the tool's own message surfaced.
			string toolMessage = stdout.Length > 0 ? stdout : stderr;
			return ClassifyNonZeroExit(toolMessage);
		}
		finally
		{
			TryDeleteDirectory(scratchDepotStore);
		}
	}

	/// <summary>
	/// Distinguished failure classification for a completed-but-nonzero validation-by-use
	/// (issue #791) via the shared <see cref="DownloadToolFailureClassifier"/>. Network
	/// signals map to a non-auth <see cref="DepotValidationResult.Failed"/> so the operator
	/// gets connectivity guidance, not "your code is bad." Explicit credential-rejection
	/// signals map to <see cref="DepotValidationResult.AuthFailed"/>. An ambiguous exit is
	/// conservative: a non-auth failure carrying the tool's own message, never a claimed
	/// rejection.
	/// </summary>
	internal static DepotValidationResult ClassifyNonZeroExit(string toolMessage)
	{
		string summary = Truncate(string.IsNullOrWhiteSpace(toolMessage)
			? "the tool exited nonzero with no output."
			: toolMessage);

		return DownloadToolFailureClassifier.Classify(toolMessage) switch
		{
			DownloadToolFailureClassifier.FailureClass.Network => DepotValidationResult.Failed(
				$"Activation Code validation could not reach Broadcom (network/connectivity): {summary}"),
			DownloadToolFailureClassifier.FailureClass.Auth => DepotValidationResult.AuthFailed(summary),
			_ => DepotValidationResult.Failed($"Activation Code validation failed: {summary}"),
		};
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch (IOException)
		{
			// Best-effort scratch cleanup: a stray empty validate-scratch subdir on the
			// managed volume is not a correctness issue once the outcome is recorded.
		}
		catch (UnauthorizedAccessException)
		{
			// Same -- never let cleanup failure mask the validation result.
		}
	}

	/// <summary>
	/// Creates (if absent) and returns the isolated, persistent identity directory the
	/// tool's <c>HOME</c>/<c>XDG_DATA_HOME</c> is pointed at -- same-volume as the
	/// managed tool itself (<see cref="ManagedToolOptions.ToolStatePath"/>), so the
	/// Depot ID the tool derives/persists there is stable across container rebuilds
	/// without ever being a container-global root home.
	/// </summary>
	private static string PrepareIdentityHome(ManagedToolOptions options)
	{
		string identityHome = Path.Combine(options.ToolStatePath, options.IdentityStatePath);
		Directory.CreateDirectory(identityHome);
		return identityHome;
	}

	/// <summary>
	/// Atomically seeds <c>&lt;identity&gt;/.local/share/vmware/vdt/machine_id</c> with a
	/// code's decoded <c>asset_id</c> (issue #787). <c>machine_id</c> is DERIVED state, not
	/// a durable identity: this OVERWRITES whatever is currently there so identity always
	/// follows the code the current run is using (owner decision 2026-08-25 -- swapping in
	/// a different working code just works, no reset ceremony). Uses the #760 atomic
	/// pattern: write a same-directory temp file with restrictive (0600) permissions, then
	/// atomically rename it over any existing file, so a concurrent reader never observes a
	/// partially written identity. Content is exactly the asset_id, no trailing newline
	/// (byte-for-byte what the sibling Dockerfile's WriteAllText produces).
	/// </summary>
	private static void SeedMachineId(string identityHome, string assetId)
	{
		string vdtDirectory = Path.Combine(
			new[] { identityHome, ".local", "share" }.Concat(MachineIdRelativeSegments[..^1]).ToArray());
		string machineIdPath = Path.Combine(vdtDirectory, MachineIdRelativeSegments[^1]);

		Directory.CreateDirectory(vdtDirectory);
		string tempPath = Path.Combine(vdtDirectory, $".machine_id.{Guid.NewGuid():N}.tmp");
		try
		{
			// FileMode.CreateNew + restrictive mode: the plaintext identity is written
			// through a 0600 handle from the outset, never a default-umask file later
			// tightened.
			using (FileStream stream = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				if (!OperatingSystem.IsWindows())
				{
					File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
				}

				byte[] bytes = System.Text.Encoding.UTF8.GetBytes(assetId);
				stream.Write(bytes, 0, bytes.Length);
				stream.Flush(flushToDisk: true);
			}

			// Atomic rename on the managed volume (same directory), overwriting any prior
			// identity -- machine_id follows the code, so the current run's asset_id wins.
			File.Move(tempPath, machineIdPath, overwrite: true);
		}
		finally
		{
			if (File.Exists(tempPath))
			{
				File.Delete(tempPath);
			}
		}
	}

	private static string ExecutablePath(ManagedToolOptions options) =>
		Path.Combine(options.ToolStatePath, options.ActiveDirectoryName, options.ExecutableRelativePath);

	private static async Task<(bool Succeeded, int ExitCode, string Stdout, string Stderr)> RunAsync(
		string executablePath, string arguments, string identityHome, ManagedToolOptions options, TimeSpan timeout, CancellationToken cancellationToken)
	{
		string libraryPath = Path.Combine(options.ToolStatePath, options.ActiveDirectoryName, options.LibraryRelativePath);

		ProcessStartInfo startInfo = new(executablePath, arguments)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		string existingLibraryPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty;
		startInfo.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existingLibraryPath)
			? libraryPath
			: libraryPath + Path.PathSeparator + existingLibraryPath;

		// Isolated persistent app-state home (issue #691 AC), never the process's own
		// container-global HOME -- matches the sibling reference's
		// ~/.local/share/vmware/vdt/machine_id convention, but rooted at the managed
		// volume instead of /root.
		startInfo.Environment["HOME"] = identityHome;
		startInfo.Environment["XDG_DATA_HOME"] = Path.Combine(identityHome, ".local", "share");

		using CancellationTokenSource timeoutSource = new(timeout);
		using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

		Process process;
		try
		{
			process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null.");
		}
		catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
		{
			return (false, -1, string.Empty, exception.Message);
		}

		using (process)
		{
			process.StandardInput.Close();

			try
			{
				await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				TryKill(process);
				bool timedOut = timeoutSource.IsCancellationRequested;
				return (false, -1, string.Empty,
					timedOut ? $"did not complete within {timeout}" : "cancelled");
			}

			string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
			string stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
			return (true, process.ExitCode, stdout, stderr);
		}
	}

	private static void TryKill(Process process)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch (InvalidOperationException)
		{
			// Already exited between the check and the kill -- not a failure.
		}
	}

	private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "...";
}
