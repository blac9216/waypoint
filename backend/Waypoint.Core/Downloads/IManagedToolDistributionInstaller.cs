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

namespace Waypoint.Core.Downloads;

/// <summary>
/// Safely extracts a verified <c>vcf-download-tool</c> distribution archive, validates
/// its layout, smoke-tests the real executable, and atomically activates it (issue #686
/// -- the fix for the <c>Exec format error</c> regression where the archive itself was
/// copied straight into the executable path). Every install source
/// (<see cref="ManagedToolInstallSources.LocalRepository"/>, <see cref="ManagedToolInstallSources.Upload"/>,
/// <see cref="ManagedToolInstallSources.Depot"/>) shares this one path once its
/// source-specific checksum/signature/catalog verification has already passed.
/// </summary>
public interface IManagedToolDistributionInstaller
{
	/// <summary>
	/// Extracts <paramref name="archivePath"/> into same-volume staging, validates every
	/// entry and the resulting layout, runs a bounded noninteractive smoke test of the
	/// real executable, and -- only if every step passes -- atomically activates it,
	/// preserving the prior-good installation on any failure. Staging is always cleaned
	/// up, on every path (success, rejection, or failure).
	/// </summary>
	Task<ManagedToolDistributionInstallResult> InstallAsync(string archivePath, CancellationToken cancellationToken);
}

/// <summary>Why a candidate distribution was rejected before activation, or failed during activation -- ledger-actionable, never a raw exception message alone.</summary>
public enum ManagedToolDistributionRejectionKind
{
	/// <summary>Not rejected -- <see cref="ManagedToolDistributionInstallResult.Succeeded"/> is true.</summary>
	None,

	/// <summary>An archive entry used an absolute path, a <c>..</c> traversal segment, or otherwise resolved outside the staging root.</summary>
	UnsafePath,

	/// <summary>A symlink or hardlink entry targets a path outside the staging root (an escape), or the archive uses a link type this installer does not evaluate as safe.</summary>
	UnsafeLink,

	/// <summary>An entry is a device, FIFO, socket, or other non-regular/non-directory/non-safe-link special file.</summary>
	SpecialFile,

	/// <summary>The archive exceeds <see cref="ManagedToolOptions.MaxArchiveEntries"/> or <see cref="ManagedToolOptions.MaxExtractedTotalBytes"/>.</summary>
	ExpansionLimitExceeded,

	/// <summary>Extraction succeeded but <see cref="ManagedToolOptions.ExecutableRelativePath"/> was not found, or the required <see cref="ManagedToolOptions.LibraryRelativePath"/> directory is missing.</summary>
	MissingLayout,

	/// <summary>The bounded noninteractive smoke-test execution of the real executable did not exit successfully within <see cref="ManagedToolOptions.SmokeTestTimeout"/>.</summary>
	SmokeTestFailed,

	/// <summary>The archive itself could not be read as a valid gzip/tar stream.</summary>
	MalformedArchive,

	/// <summary>Activation (the atomic staging-to-active swap) failed after every validation step passed -- an I/O-class failure, not a rejection of the candidate's content.</summary>
	ActivationFailed,
}

/// <summary>Outcome of <see cref="IManagedToolDistributionInstaller.InstallAsync"/>.</summary>
public sealed record ManagedToolDistributionInstallResult(
	bool Succeeded,
	ManagedToolDistributionRejectionKind RejectionKind,
	string? FailureReason)
{
	public static ManagedToolDistributionInstallResult Ok() =>
		new(true, ManagedToolDistributionRejectionKind.None, null);

	public static ManagedToolDistributionInstallResult Reject(ManagedToolDistributionRejectionKind kind, string reason)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		return new(false, kind, reason);
	}
}
