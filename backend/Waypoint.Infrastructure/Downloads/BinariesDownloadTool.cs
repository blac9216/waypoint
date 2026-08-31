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
using Microsoft.Extensions.Options;
using Waypoint.Core.Downloads;

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

	public BinariesDownloadTool(IOptions<ManagedToolOptions> options, IManagedToolPresenceChecker presenceChecker)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(presenceChecker);
		_options = options;
		_presenceChecker = presenceChecker;
	}

	public async Task<BinariesDownloadResult> DownloadAsync(
		string externalId, string depotStorePath, string activationCodePath, string identityHome, string assetId,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
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
		string arguments = $"binaries download --id=\"{externalId}\" --depot-store=\"{depotStorePath}\" --ceip=DISABLE";

		(bool succeeded, int exitCode, string stdout, string stderr) = await RunAsync(
			ExecutablePath(options), arguments, identityHome, options, cancellationToken).ConfigureAwait(false);

		if (!succeeded)
		{
			return BinariesDownloadResult.Failed($"binaries download could not be started or timed out: {stderr}", string.Empty);
		}

		if (exitCode == 0)
		{
			return BinariesDownloadResult.Ok(stdout);
		}

		// A completed nonzero exit is classified honestly (issue #1482 AC: "Auth vs
		// network vs disk vs vendor-throttle failures are classified distinctly, never
		// collapsed into a generic failure") -- never blanket auth-failed or generic
		// failed on evidence the tool did not give.
		string toolMessage = stdout.Length > 0 ? stdout : stderr;
		string summary = Truncate(string.IsNullOrWhiteSpace(toolMessage) ? "the tool exited nonzero with no output." : toolMessage);
		return DownloadToolFailureClassifier.Classify(toolMessage) switch
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
