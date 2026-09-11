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
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Waypoint.Core.Downloads;
using Waypoint.Core.Logging;

namespace Waypoint.Infrastructure.Downloads;

/// <inheritdoc cref="IBinariesDownloadTool"/>
/// <remarks>
/// Mirrors <see cref="ManagedToolMetadataPuller"/>/<see cref="DepotIdentityTool"/>'s
/// bounded noninteractive process pattern (stdin closed immediately, linked
/// timeout/cancellation, hard kill on timeout), but deliberately does NOT share their
/// <c>PrepareIdentityHome</c> helper: those two point at the single shared
/// <see cref="ManagedToolOptions.IdentityStatePath"/> home, which issue #790 documents as
/// unserialized across concurrent depot jobs. The 2026-08-28 grill decision (R2-8) makes
/// unbounded concurrency this handler's design, not an optional optimization, so every
/// call here seeds <c>machine_id</c> into the CALLER-SUPPLIED, job-scoped
/// <paramref name="identityHome"/> instead -- the caller (<c>BinariesDownloadJobHandler</c>)
/// is responsible for making that path unique per job.
/// </remarks>
public sealed class BinariesDownloadTool : IBinariesDownloadTool
{
	/// <summary>Same layout the real tool checks an Activation Code against -- see <see cref="DepotIdentityTool"/>'s identical constant for the sibling reference citation.</summary>
	private static readonly string[] MachineIdRelativeSegments = ["vmware", "vdt", "machine_id"];

	private readonly IOptions<ManagedToolOptions> _options;
	private readonly IManagedToolPresenceChecker _presenceChecker;
	private readonly ISecretRedactor _redactor;

	public BinariesDownloadTool(IOptions<ManagedToolOptions> options, IManagedToolPresenceChecker presenceChecker, ISecretRedactor redactor)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(presenceChecker);
		ArgumentNullException.ThrowIfNull(redactor);
		_options = options;
		_presenceChecker = presenceChecker;
		_redactor = redactor;
	}

	public async Task<BinariesDownloadResult> DownloadAsync(
		string id, string depotStorePath, string activationCodePath, string identityHome, string assetId,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		ArgumentException.ThrowIfNullOrWhiteSpace(depotStorePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(activationCodePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(identityHome);
		ArgumentException.ThrowIfNullOrWhiteSpace(assetId);

		if (!_presenceChecker.IsPresent())
		{
			return BinariesDownloadResult.Failed(
				$"vcf-download-tool is not installed (expected at '{_presenceChecker.DescribeExpectedLocation()}'). Install the managed tool before binaries-download jobs can run.",
				string.Empty);
		}

		ManagedToolOptions options = _options.Value;
		Directory.CreateDirectory(depotStorePath);
		SeedMachineId(identityHome, assetId);

		// Issue #1482's documented contract (this issue's own Proposed Changes section):
		// `binaries download --id <bundle-id> --depot-store=<depot> --ceip=DISABLE`.
		// Rendered here with the `--id=` / `--depot-store=` long-flag-equals spelling
		// that every other invocation in this codebase uses (`metadata download
		// --depot-store=...`, `configuration ... --depot-download-activation-code-
		// file=...`), so every call this codebase makes reads identically. Unlike #791's
		// live-audited `metadata download --help` contract, the real tool's `binaries
		// download --help` has not been separately live-audited for this issue; this
		// shape is exactly what the issue specifies and is flagged pending-live like the
		// rest of this issue's tool-invocation surface.
		//
		// The Activation Code MUST reach the tool the same way #791's live-audited
		// `metadata download` contract does -- `--depot-download-activation-code-file=
		// <path>` (see DepotIdentityTool.cs's identical flag, verified against the live
		// `metadata download --help`) -- otherwise the tool has no credential and every
		// call fails auth regardless of a validated enrollment.
		string arguments =
			$"binaries download --id=\"{id}\" --depot-store=\"{depotStorePath}\" " +
			$"\"--depot-download-activation-code-file={activationCodePath}\" --ceip=DISABLE";

		(bool succeeded, int exitCode, string stdout, string stderr) = await RunAsync(
			ExecutablePath(options), arguments, identityHome, options, cancellationToken).ConfigureAwait(false);

		if (!succeeded)
		{
			return BinariesDownloadResult.Failed($"binaries download could not be started or timed out: {stderr}", string.Empty);
		}

		if (exitCode == 0)
		{
			// Issue #1783 (Option B, required regardless of the --id fix): the real tool
			// exits 0 even when its own "Binaries to be downloaded" table selects
			// nothing -- a bare "0 elements" line with no other error text. Treating that
			// as success silently no-ops the job and only fails one layer later, at
			// verification, with a misleading "file not found" message. Checked BEFORE
			// returning Ok so an empty selection is always reported honestly, by name,
			// rather than surfacing downstream as something it is not.
			string? emptySelectionReason = TryDetectEmptySelectionFailure(stdout, id);
			if (emptySelectionReason is not null)
			{
				return BinariesDownloadResult.Failed(emptySelectionReason, stdout);
			}

			return BinariesDownloadResult.Ok(stdout);
		}

		// Issue #1785: the tool's stdout on a real failure is only a banner + a
		// "Log file: <path>" line (mirrors DepotIdentityTool's identical banner
		// parsing) -- the actual diagnostics live in that log file, never on stdout.
		// Best-effort read it (never lets a missing/unreadable log mask the underlying
		// failure) and fold its meaningful tail into BOTH the classification input and
		// the reported failure reason, so a misleading tool banner (e.g. "Depot
		// connection failure") is never the only text an operator or the classifier
		// ever sees.
		//
		// Round-1 review finding: the identity home this tail is read from (job-scoped,
		// seeded with this job's own machine_id/asset_id and pointed at by --depot-
		// download-activation-code-file's Activation Code) can hold the download token
		// in its own log lines -- redacted here through the SAME ISecretRedactor the
		// rest of the pipeline uses (docs/explanation/security.md control 1: "the logging pipeline
		// ... redacts every occurrence before any line reaches a sink"), mirroring
		// DepotEnrollmentJobHandler.ValidateCodeAsync's identical "jobs.note is a sink
		// too" redaction -- BEFORE the tail enters either the classifier input or the
		// persisted failure reason, never after.
		string? logTail = TryReadToolLogTail(identityHome) is { } rawTail ? _redactor.Redact(rawTail) : null;

		// A completed nonzero exit is classified honestly (issue #1482 AC: "Auth vs
		// network vs disk vs vendor-throttle failures are classified distinctly, never
		// collapsed into a generic failure") -- never blanket auth-failed or generic
		// failed on evidence the tool did not give.
		string toolMessage = stdout.Length > 0 ? stdout : stderr;
		string classificationInput = logTail is null ? toolMessage : $"{toolMessage}\n{logTail}";
		string bannerSummary = Truncate(string.IsNullOrWhiteSpace(toolMessage) ? "the tool exited nonzero with no output." : toolMessage);
		string summary = logTail is null ? bannerSummary : $"{bannerSummary} (tool log: {Truncate(logTail)})";
		return DownloadToolFailureClassifier.Classify(classificationInput) switch
		{
			DownloadToolFailureClassifier.FailureClass.Network => BinariesDownloadResult.Failed(
				$"binaries download could not reach Broadcom (network/connectivity): {summary}", stdout),
			DownloadToolFailureClassifier.FailureClass.Disk => BinariesDownloadResult.DiskFailed(
				$"binaries download failed writing to the depot store (disk): {summary}", stdout),
			DownloadToolFailureClassifier.FailureClass.Throttle => BinariesDownloadResult.Throttled(
				$"binaries download was rate-limited by Broadcom: {summary}", stdout),
			DownloadToolFailureClassifier.FailureClass.Auth => BinariesDownloadResult.AuthFailed(summary, stdout),
			_ => BinariesDownloadResult.Failed($"binaries download failed: {summary}", stdout),
		};
	}

	/// <summary>Matches a bare "&lt;N&gt; elements" line -- the real tool's "Binaries to be downloaded" table footer (issue #1783).</summary>
	private static readonly Regex ElementsCountPattern = new(@"^(\d+)\s+elements?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	/// <summary>
	/// Issue #1783: detects the real tool's "0 elements" empty-selection table on an
	/// otherwise-successful (exit 0) invocation and turns it into an honest, actionable
	/// failure naming the id that was given -- never a silent no-op success. A nonzero
	/// element count is left alone (returns null, meaning "not empty").
	/// </summary>
	private static string? TryDetectEmptySelectionFailure(string stdout, string id)
	{
		foreach (string rawLine in stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
		{
			Match match = ElementsCountPattern.Match(rawLine.Trim());
			if (match.Success && match.Groups[1].Value == "0")
			{
				return $"binaries download selected 0 elements for --id=\"{id}\" -- this id does not match a bundle " +
					"in the current depot catalog. Verify the artifact's bundle id and re-pull the catalog if it is stale.";
			}
		}

		return null;
	}

	/// <summary>
	/// Bound on how much of <c>vdt.log</c>'s tail is ever read into memory (round-1
	/// review finding: the file is vendor-written, unbounded, and this handler never
	/// truncates it -- a multi-GB log the tool leaves behind must never be pulled into
	/// process memory whole by <see cref="TryReadToolLogTail"/> just to extract a
	/// handful of meaningful lines from its end).
	/// </summary>
	private const int LogTailReadBytes = 64 * 1024;

	/// <summary>
	/// Issue #1785: reads and extracts a meaningful tail from the real tool's own log
	/// file at <c>&lt;identityHome&gt;/log/vdt.log</c> -- the same relative shape
	/// <c>DepotIdentityToolTests</c>' fixtures assert for the shared enrollment identity
	/// home ("Log file: &lt;identity&gt;/log/vdt.log"), which this job-scoped identity
	/// home follows identically since both point <c>HOME</c> at their own root. Best
	/// effort: a missing or unreadable log file must never mask the underlying failure,
	/// so any read failure here returns null and the caller falls back to stdout/stderr
	/// alone, exactly as before this issue. Only the last <see cref="LogTailReadBytes"/>
	/// bytes are ever read (round-1 review finding), never the whole file -- the
	/// meaningful-line extraction below only ever needs the end of the file anyway.
	/// </summary>
	private static string? TryReadToolLogTail(string identityHome)
	{
		string logPath = Path.Combine(identityHome, "log", "vdt.log");
		try
		{
			if (!File.Exists(logPath))
			{
				return null;
			}

			using FileStream stream = new(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			long start = Math.Max(0, stream.Length - LogTailReadBytes);
			stream.Seek(start, SeekOrigin.Begin);

			byte[] buffer = new byte[stream.Length - start];
			int totalRead = 0;
			while (totalRead < buffer.Length)
			{
				int read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
				if (read == 0)
				{
					break;
				}

				totalRead += read;
			}

			return ExtractMeaningfulTail(Encoding.UTF8.GetString(buffer, 0, totalRead));
		}
		catch (IOException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>
	/// Prefers lines that look like an actual error (<c>ERROR</c>, <c>Exception</c>,
	/// <c>Caused by</c>, <c>Permission denied</c>) -- the real vdt.log example issue
	/// #1785 captured is otherwise mostly INFO-level progress noise -- and falls back
	/// to the file's last few lines when nothing matches, so a log in an unanticipated
	/// shape still contributes SOMETHING rather than nothing.
	/// </summary>
	internal static string? ExtractMeaningfulTail(string content)
	{
		if (string.IsNullOrWhiteSpace(content))
		{
			return null;
		}

		string[] lines = content.Replace("\r\n", "\n", StringComparison.Ordinal)
			.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		if (lines.Length == 0)
		{
			return null;
		}

		List<string> meaningful = [.. lines.Where(LooksLikeErrorLine)];
		IEnumerable<string> chosen = meaningful.Count > 0 ? meaningful.TakeLast(5) : lines.TakeLast(5);
		string tail = string.Join(" | ", chosen.Select(line => line.Trim()));
		return string.IsNullOrWhiteSpace(tail) ? null : tail;
	}

	private static bool LooksLikeErrorLine(string line) =>
		line.Contains("ERROR", StringComparison.Ordinal)
		|| line.Contains("Exception", StringComparison.Ordinal)
		|| line.Contains("Caused by", StringComparison.Ordinal)
		|| line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Atomically seeds <c>&lt;identityHome&gt;/.local/share/vmware/vdt/machine_id</c> --
	/// same write-temp-then-rename pattern as <see cref="DepotIdentityTool"/>'s private
	/// helper of the same purpose, duplicated here (not shared) because that home is job-
	/// scoped and ephemeral for this caller, never the shared enrollment identity home.
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
		string executablePath, string arguments, string identityHome, ManagedToolOptions options, CancellationToken cancellationToken)
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

		startInfo.Environment["HOME"] = identityHome;
		startInfo.Environment["XDG_DATA_HOME"] = Path.Combine(identityHome, ".local", "share");

		using CancellationTokenSource timeoutSource = new(options.BinariesDownloadTimeout);
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

			// Both pipes are read concurrently, started BEFORE the wait -- a child that
			// writes more than the OS pipe buffer (~64 KiB) to either stream would
			// otherwise block forever on a full pipe while nothing is draining it, and
			// WaitForExitAsync would never observe the exit: a deadlock, not merely a
			// slow read, that the BinariesDownloadTimeout can't rescue since the process
			// itself is stuck, not just uncooperative. Reading concurrently with (not
			// after) the wait is the standard fix.
			Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
			Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

			try
			{
				await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				TryKill(process);
				bool timedOut = timeoutSource.IsCancellationRequested;
				return (false, -1, string.Empty,
					timedOut ? $"did not complete within {options.BinariesDownloadTimeout}" : "cancelled");
			}

			string stdout = await stdoutTask.ConfigureAwait(false);
			string stderr = await stderrTask.ConfigureAwait(false);
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
