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
using System.Formats.Tar;
using System.IO.Compression;
using Microsoft.Extensions.Options;
using Waypoint.Core.Downloads;

namespace Waypoint.Infrastructure.Downloads;

/// <inheritdoc cref="IManagedToolDistributionInstaller"/>
/// <remarks>
/// Issue #686: a verified <c>vcf-download-tool</c> <c>.tar.gz</c> is a vendor
/// distribution archive (matching the sibling <c>../vcf-docker-download/Dockerfile</c>
/// layout: <c>bin/vcf-download-tool</c> plus a <c>lib</c> tree), never the executable
/// itself. This type extracts it entry-by-entry into same-volume staging under
/// <see cref="ManagedToolOptions.ToolStatePath"/>/<see cref="ManagedToolOptions.StagingDirectoryName"/>,
/// rejecting anything that could escape the staging root or exhaust disk before any
/// byte is written for that entry, then requires the expected executable/library layout
/// and a bounded noninteractive smoke-test execution of the real binary before an
/// atomic directory rename activates it. The prior-good <see cref="ManagedToolOptions.ActiveDirectoryName"/>
/// directory is left completely untouched until that rename, and staging is removed on
/// every exit path (success, rejection, or failure).
/// </remarks>
public sealed class ManagedToolDistributionInstaller : IManagedToolDistributionInstaller
{
	private readonly IOptions<ManagedToolOptions> _options;

	public ManagedToolDistributionInstaller(IOptions<ManagedToolOptions> options)
	{
		ArgumentNullException.ThrowIfNull(options);
		_options = options;
	}

	public async Task<ManagedToolDistributionInstallResult> InstallAsync(string archivePath, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

		ManagedToolOptions options = _options.Value;
		Directory.CreateDirectory(options.ToolStatePath);

		string stagingRoot = Path.Combine(options.ToolStatePath, options.StagingDirectoryName);
		string extractRoot = Path.Combine(stagingRoot, "extract-" + Guid.NewGuid().ToString("N"));

		try
		{
			Directory.CreateDirectory(extractRoot);

			ManagedToolDistributionInstallResult extractResult = await ExtractAsync(archivePath, extractRoot, options, cancellationToken)
				.ConfigureAwait(false);
			if (!extractResult.Succeeded)
			{
				return extractResult;
			}

			string executablePath = Path.Combine(extractRoot, NormalizeRelative(options.ExecutableRelativePath));
			string libraryPath = Path.Combine(extractRoot, NormalizeRelative(options.LibraryRelativePath));

			if (!File.Exists(executablePath))
			{
				return ManagedToolDistributionInstallResult.Reject(
					ManagedToolDistributionRejectionKind.MissingLayout,
					$"Extracted distribution does not contain the expected executable at '{options.ExecutableRelativePath}'.");
			}

			if (!Directory.Exists(libraryPath))
			{
				return ManagedToolDistributionInstallResult.Reject(
					ManagedToolDistributionRejectionKind.MissingLayout,
					$"Extracted distribution does not contain the expected library directory at '{options.LibraryRelativePath}'.");
			}

			TrySetExecutable(executablePath);

			ManagedToolDistributionInstallResult smokeResult = await SmokeTestAsync(executablePath, libraryPath, options, cancellationToken)
				.ConfigureAwait(false);
			if (!smokeResult.Succeeded)
			{
				return smokeResult;
			}

			return Activate(extractRoot, options);
		}
		finally
		{
			// Staging cleanup on every path -- success (the directory was already moved
			// out from under extractRoot by Activate, so this is a no-op), rejection, or
			// an unexpected exception during extraction/smoke-test.
			TryDeleteDirectory(extractRoot);
		}
	}

	private static async Task<ManagedToolDistributionInstallResult> ExtractAsync(
		string archivePath, string extractRoot, ManagedToolOptions options, CancellationToken cancellationToken)
	{
		string normalizedRoot = NormalizedRootWithSeparator(extractRoot);
		long totalBytes = 0;
		int entryCount = 0;

		try
		{
			await using FileStream archiveStream = File.OpenRead(archivePath);
			await using GZipStream gzipStream = new(archiveStream, CompressionMode.Decompress);
			await using TarReader reader = new(gzipStream, leaveOpen: false);

			while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
			{
				cancellationToken.ThrowIfCancellationRequested();

				entryCount++;
				if (entryCount > options.MaxArchiveEntries)
				{
					return ManagedToolDistributionInstallResult.Reject(
						ManagedToolDistributionRejectionKind.ExpansionLimitExceeded,
						$"Archive contains more than {options.MaxArchiveEntries} entries.");
				}

				ManagedToolDistributionInstallResult? pathResult = ValidateEntryPath(entry, extractRoot, normalizedRoot);
				if (pathResult is not null)
				{
					return pathResult;
				}

				switch (entry.EntryType)
				{
					case TarEntryType.Directory:
					case TarEntryType.DirectoryList:
						Directory.CreateDirectory(GetDestination(entry, extractRoot));
						break;

					case TarEntryType.RegularFile:
					case TarEntryType.V7RegularFile:
					case TarEntryType.ContiguousFile:
						totalBytes += Math.Max(0, entry.Length);
						if (totalBytes > options.MaxExtractedTotalBytes)
						{
							return ManagedToolDistributionInstallResult.Reject(
								ManagedToolDistributionRejectionKind.ExpansionLimitExceeded,
								$"Archive expands beyond the {options.MaxExtractedTotalBytes} byte limit.");
						}

						string destination = GetDestination(entry, extractRoot);
						Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
						await entry.ExtractToFileAsync(destination, overwrite: false, cancellationToken).ConfigureAwait(false);
						break;

					case TarEntryType.SymbolicLink:
						{
							ManagedToolDistributionInstallResult? linkResult = ValidateSymlinkTarget(entry, extractRoot, normalizedRoot);
							if (linkResult is not null)
							{
								return linkResult;
							}

							break;
						}

					case TarEntryType.HardLink:
						{
							ManagedToolDistributionInstallResult? linkResult = ValidateHardLinkTarget(entry, extractRoot, normalizedRoot);
							if (linkResult is not null)
							{
								return linkResult;
							}

							break;
						}

					case TarEntryType.CharacterDevice:
					case TarEntryType.BlockDevice:
					case TarEntryType.Fifo:
						return ManagedToolDistributionInstallResult.Reject(
							ManagedToolDistributionRejectionKind.SpecialFile,
							$"Archive entry '{entry.Name}' is a special file ({entry.EntryType}), which is never extracted.");

					default:
						// Extended attribute / long-name / global metadata entries and other
						// format bookkeeping the archive format may legitimately contain --
						// TarReader already folds their effect into the entries that follow,
						// so there is nothing further to extract here.
						break;
				}
			}
		}
		catch (InvalidDataException exception)
		{
			return ManagedToolDistributionInstallResult.Reject(
				ManagedToolDistributionRejectionKind.MalformedArchive,
				$"Archive could not be read as a valid gzip/tar distribution: {exception.Message}");
		}

		return ManagedToolDistributionInstallResult.Ok();
	}

	/// <summary>
	/// Rejects an absolute entry name or any entry whose resolved path escapes
	/// <paramref name="extractRoot"/> (a <c>..</c> traversal segment, on any platform's
	/// separator convention) -- checked BEFORE any file is created for that entry.
	/// </summary>
	private static ManagedToolDistributionInstallResult? ValidateEntryPath(TarEntry entry, string extractRoot, string normalizedRoot)
	{
		string name = entry.Name;
		if (string.IsNullOrWhiteSpace(name))
		{
			return ManagedToolDistributionInstallResult.Reject(
				ManagedToolDistributionRejectionKind.UnsafePath, "Archive contains an entry with an empty name.");
		}

		if (name.StartsWith('/') || name.StartsWith('\\') || IsWindowsRooted(name))
		{
			return ManagedToolDistributionInstallResult.Reject(
				ManagedToolDistributionRejectionKind.UnsafePath, $"Archive entry '{name}' uses an absolute path.");
		}

		string candidate;
		try
		{
			candidate = Path.GetFullPath(Path.Combine(extractRoot, name));
		}
		catch (ArgumentException)
		{
			return ManagedToolDistributionInstallResult.Reject(
				ManagedToolDistributionRejectionKind.UnsafePath, $"Archive entry '{name}' has an invalid path.");
		}

		if (!IsWithinRoot(candidate, normalizedRoot))
		{
			return ManagedToolDistributionInstallResult.Reject(
				ManagedToolDistributionRejectionKind.UnsafePath, $"Archive entry '{name}' resolves outside the staging directory.");
		}

		return null;
	}

	private static ManagedToolDistributionInstallResult? ValidateSymlinkTarget(TarEntry entry, string extractRoot, string normalizedRoot)
	{
		string linkName = entry.LinkName;
		if (string.IsNullOrWhiteSpace(linkName))
		{
			return ManagedToolDistributionInstallResult.Reject(
				ManagedToolDistributionRejectionKind.UnsafeLink, $"Symlink entry '{entry.Name}' has no target.");
		}

		if (Path.IsPathRooted(linkName) || IsWindowsRooted(linkName))
		{
			return ManagedToolDistributionInstallResult.Reject(
				ManagedToolDistributionRejectionKind.UnsafeLink, $"Symlink entry '{entry.Name}' targets an absolute path ('{linkName}').");
		}

		// A symlink target is resolved relative to its OWN containing directory, not the
		// staging root -- e.g. "lib/libfoo.so.1" -> "libfoo.so.1.0" resolves inside
		// "lib/", not inside the root directly.
		string entryDirectory = Path.GetDirectoryName(Path.Combine(extractRoot, NormalizeRelative(entry.Name))) ?? extractRoot;
		string resolved;
		try
		{
			resolved = Path.GetFullPath(Path.Combine(entryDirectory, linkName));
		}
		catch (ArgumentException)
		{
			return ManagedToolDistributionInstallResult.Reject(
				ManagedToolDistributionRejectionKind.UnsafeLink, $"Symlink entry '{entry.Name}' has an invalid target.");
		}

		if (!IsWithinRoot(resolved, normalizedRoot))
		{
			return ManagedToolDistributionInstallResult.Reject(
				ManagedToolDistributionRejectionKind.UnsafeLink,
				$"Symlink entry '{entry.Name}' targets '{linkName}', which escapes the staging directory.");
		}

		// The target need not exist yet (it may appear later in the same archive); we
		// only need to know the intended path never escapes the root. Recreate a
		// same-semantics relative symlink so the executed tool sees the layout the
		// vendor archive intended.
		string linkPath = Path.Combine(extractRoot, NormalizeRelative(entry.Name));
		Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
		try
		{
			File.CreateSymbolicLink(linkPath, linkName);
		}
		catch (IOException)
		{
			// Best-effort: some hosts/filesystems disallow unprivileged symlink
			// creation. A distribution that genuinely requires a working symlink will
			// then fail the layout or smoke-test check downstream instead of silently
			// activating with a broken link -- fail-safe, not a rejection in itself.
		}

		return null;
	}

	private static ManagedToolDistributionInstallResult? ValidateHardLinkTarget(TarEntry entry, string extractRoot, string normalizedRoot)
	{
		string linkName = entry.LinkName;
		if (string.IsNullOrWhiteSpace(linkName) || Path.IsPathRooted(linkName) || IsWindowsRooted(linkName))
		{
			return ManagedToolDistributionInstallResult.Reject(
				ManagedToolDistributionRejectionKind.UnsafeLink, $"Hard link entry '{entry.Name}' has an unsafe or absolute target ('{linkName}').");
		}

		string targetCandidate;
		try
		{
			targetCandidate = Path.GetFullPath(Path.Combine(extractRoot, NormalizeRelative(linkName)));
		}
		catch (ArgumentException)
		{
			return ManagedToolDistributionInstallResult.Reject(
				ManagedToolDistributionRejectionKind.UnsafeLink, $"Hard link entry '{entry.Name}' has an invalid target.");
		}

		if (!IsWithinRoot(targetCandidate, normalizedRoot) || !File.Exists(targetCandidate))
		{
			return ManagedToolDistributionInstallResult.Reject(
				ManagedToolDistributionRejectionKind.UnsafeLink,
				$"Hard link entry '{entry.Name}' targets '{linkName}', which does not resolve to an already-extracted file inside the staging directory.");
		}

		string linkPath = Path.Combine(extractRoot, NormalizeRelative(entry.Name));
		Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
		File.Copy(targetCandidate, linkPath, overwrite: false);
		return null;
	}

	private static bool IsWindowsRooted(string name) =>
		name.Length >= 2 && name[1] == ':' && char.IsAsciiLetter(name[0]);

	private static bool IsWithinRoot(string candidate, string normalizedRoot) =>
		(candidate + Path.DirectorySeparatorChar).StartsWith(normalizedRoot, StringComparison.Ordinal)
		|| string.Equals(candidate + Path.DirectorySeparatorChar, normalizedRoot, StringComparison.Ordinal);

	private static string NormalizedRootWithSeparator(string root) =>
		Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

	private static string GetDestination(TarEntry entry, string extractRoot) =>
		Path.Combine(extractRoot, NormalizeRelative(entry.Name));

	private static string NormalizeRelative(string relativePath) =>
		relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

	/// <summary>
	/// Runs the extracted candidate executable once, noninteractively, with its managed
	/// library directory on the environment before any activation happens -- the direct
	/// fix for issue #686's <c>Exec format error</c> regression: an archive can pass
	/// every path/layout check and still not be a real, runnable executable for this
	/// platform/architecture. stdin is redirected from an empty stream so a tool that
	/// unexpectedly waits on input cannot hang the job past <see cref="ManagedToolOptions.SmokeTestTimeout"/>.
	/// </summary>
	private static async Task<ManagedToolDistributionInstallResult> SmokeTestAsync(
		string executablePath, string libraryPath, ManagedToolOptions options, CancellationToken cancellationToken)
	{
		ProcessStartInfo startInfo = new(executablePath, options.SmokeTestArgument)
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

		using CancellationTokenSource timeoutSource = new(options.SmokeTestTimeout);
		using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

		Process process;
		try
		{
			process = Process.Start(startInfo)
				?? throw new InvalidOperationException("Process.Start returned null.");
		}
		catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
		{
			return ManagedToolDistributionInstallResult.Reject(
				ManagedToolDistributionRejectionKind.SmokeTestFailed,
				$"Extracted executable could not be started (likely not runnable on this platform/architecture): {exception.Message}");
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
				if (timedOut)
				{
					return ManagedToolDistributionInstallResult.Reject(
						ManagedToolDistributionRejectionKind.SmokeTestFailed,
						$"Smoke-test execution did not complete within {options.SmokeTestTimeout}.");
				}

				throw;
			}

			if (process.ExitCode != 0)
			{
				string stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
				return ManagedToolDistributionInstallResult.Reject(
					ManagedToolDistributionRejectionKind.SmokeTestFailed,
					$"Smoke-test execution exited with code {process.ExitCode}: {Truncate(stderr)}");
			}
		}

		return ManagedToolDistributionInstallResult.Ok();
	}

	private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "...";

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

	/// <summary>
	/// Atomically activates a fully validated staging directory: renames the current
	/// <see cref="ManagedToolOptions.ActiveDirectoryName"/> aside, renames staging into
	/// its place, then removes the old one -- same-filesystem directory renames are
	/// atomic on Linux, so a download job's presence check can never observe a
	/// half-activated installation. If the second rename fails, the original active
	/// directory is renamed back so the prior-good installation is preserved.
	/// </summary>
	private static ManagedToolDistributionInstallResult Activate(string extractRoot, ManagedToolOptions options)
	{
		string activePath = Path.Combine(options.ToolStatePath, options.ActiveDirectoryName);
		string previousPath = activePath + ".previous-" + Guid.NewGuid().ToString("N");
		bool previousExisted = Directory.Exists(activePath);

		try
		{
			if (previousExisted)
			{
				Directory.Move(activePath, previousPath);
			}

			try
			{
				Directory.Move(extractRoot, activePath);
			}
			catch (IOException)
			{
				if (previousExisted)
				{
					Directory.Move(previousPath, activePath);
				}

				throw;
			}
		}
		catch (IOException exception)
		{
			return ManagedToolDistributionInstallResult.Reject(
				ManagedToolDistributionRejectionKind.ActivationFailed,
				$"Verified distribution could not be activated: {exception.Message}");
		}
		finally
		{
			if (previousExisted)
			{
				TryDeleteDirectory(previousPath);
			}
		}

		return ManagedToolDistributionInstallResult.Ok();
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
			// Best-effort staging/previous-active cleanup only -- a stray directory
			// under staging is not a correctness issue for a job that has already
			// recorded its ledger outcome, and the next install attempt uses a fresh
			// GUID-named extraction directory regardless.
		}
	}

	private static void TrySetExecutable(string path)
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		File.SetUnixFileMode(path,
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
			UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
			UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
	}
}
