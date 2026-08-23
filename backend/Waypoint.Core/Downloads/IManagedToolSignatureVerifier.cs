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

/// <summary>Result of a <see cref="IManagedToolSignatureVerifier"/> check.</summary>
public sealed record ManagedToolSignatureResult(bool Valid, string? FailureReason)
{
	public static ManagedToolSignatureResult Ok() => new(true, null);

	public static ManagedToolSignatureResult Fail(string reason)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		return new(false, reason);
	}
}

/// <summary>
/// Verifies a candidate <c>vcf-download-tool</c> artifact's detached signature against
/// the Broadcom release public key (issue #39, ADR-0015 decision 3) before it may be
/// activated onto the managed-tool volume. Every one of the three install paths (local
/// repository, depot fetch, manual upload) runs a candidate through this check; none of
/// them may skip it. The project never ships the release public key or the gated tool
/// itself -- an operator provisions the key file out of band
/// (<see cref="ManagedToolOptions.ReleasePublicKeyPath"/>).
/// </summary>
public interface IManagedToolSignatureVerifier
{
	/// <summary>
	/// Verifies <paramref name="artifactPath"/> against its detached signature file at
	/// <paramref name="signaturePath"/>. Never throws for an ordinary verification
	/// failure (bad signature, missing signature file, missing/unreadable release key) --
	/// those are reported as <see cref="ManagedToolSignatureResult.Valid"/> == false with
	/// a <see cref="ManagedToolSignatureResult.FailureReason"/> so the caller can record a
	/// <c>rejected</c> ledger row rather than crash the job.
	/// </summary>
	Task<ManagedToolSignatureResult> VerifyAsync(string artifactPath, string signaturePath, CancellationToken cancellationToken);
}
