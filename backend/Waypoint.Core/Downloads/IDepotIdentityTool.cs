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
/// Invokes the installed <c>vcf-download-tool</c> noninteractively (issue #691) for the
/// two assisted-enrollment operations Broadcom's guidance documents: generating/reading
/// the Software Depot ID (<c>configuration get --software-depot-id</c>) and validating a
/// stored Activation Code. Both calls are bounded (<see cref="ManagedToolOptions.EnrollmentCommandTimeout"/>),
/// never prompt, and run with the tool's <c>HOME</c>/<c>XDG_DATA_HOME</c> pointed at
/// <see cref="ManagedToolOptions.IdentityStatePath"/> so the resulting identity is
/// stable across container rebuilds without touching a container-global root home. The
/// Activation Code value is passed to the tool via a job-scoped temporary file path
/// (matching the sibling reference's <c>--depot-download-activation-code-file</c>
/// convention) rather than argv or an environment variable, and that file is always
/// deleted in the caller's <c>finally</c> -- never left behind, never logged.
///
/// Issue #790: <see cref="SeedMachineIdentityAsync"/> and
/// <see cref="ValidateActivationCodeAsync"/> both take an explicit, caller-owned
/// <c>identityHome</c> -- mirroring the job-scoped-identity-home contract issue #1482's
/// <see cref="IBinariesDownloadTool"/> already established -- rather than deriving a
/// single shared path from <see cref="ManagedToolOptions.IdentityStatePath"/>
/// internally. Two concurrent depot jobs seeding DIFFERENT asset_ids into the same
/// shared home could otherwise race: one job's tool invocation could authenticate
/// under a <c>machine_id</c> another job just seeded. Every caller MUST pass its own
/// job-scoped directory (e.g. <c>&lt;ToolStatePath&gt;/.../job-&lt;job id&gt;</c>); it is
/// created if absent and never shared across concurrently running jobs.
/// <see cref="GetDepotIdAsync"/> is unaffected -- it never seeds <c>machine_id</c>, so
/// it keeps using the single persistent identity home under
/// <see cref="ManagedToolOptions.IdentityStatePath"/>.
/// </summary>
public interface IDepotIdentityTool
{
	/// <summary>Runs the bounded noninteractive Depot ID query. Fails with an actionable reason if the tool is not installed, the call times out, or the tool exits non-zero.</summary>
	Task<DepotIdentityResult> GetDepotIdAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Seeds <paramref name="identityHome"/>'s <c>machine_id</c> from the decoded
	/// <paramref name="assetId"/> of the Activation Code a run is about to use (issue #787).
	/// <c>machine_id</c> is DERIVED state, not a durable managed identity: every run that
	/// uses the code re-derives it from that code and OVERWRITES whatever is there, so
	/// swapping in a different working code just works with no reset ceremony (owner
	/// decision 2026-08-25 -- "identity follows the code"). Atomic and restrictive
	/// (write-temp-then-rename, 0600). The code value itself is never touched -- only its
	/// non-secret <paramref name="assetId"/> is written. <paramref name="identityHome"/>
	/// MUST be a job-scoped directory (issue #790) -- never a home shared with another
	/// concurrently running job.
	/// </summary>
	Task SeedMachineIdentityAsync(string assetId, string identityHome, CancellationToken cancellationToken);

	/// <summary>
	/// Runs the bounded noninteractive validation of <paramref name="activationCodePath"/>
	/// (a job-scoped temp file containing the decrypted code, never the code value itself)
	/// against <paramref name="identityHome"/>'s current <c>machine_id</c>. Validation means
	/// only "the tool accepts this code"; the caller seeds <c>machine_id</c> from the code's
	/// own decoded asset_id via <see cref="SeedMachineIdentityAsync"/> against the SAME
	/// <paramref name="identityHome"/> immediately before this call, so any structurally
	/// valid code is asked as-is. A non-auth-failure error (tool missing, timeout) is
	/// distinguished from a real portal/auth rejection so callers never misclassify a
	/// runner problem as "the code is bad." <paramref name="identityHome"/> MUST be the
	/// same job-scoped directory (issue #790) the matching
	/// <see cref="SeedMachineIdentityAsync"/> call used.
	/// </summary>
	Task<DepotValidationResult> ValidateActivationCodeAsync(string activationCodePath, string identityHome, CancellationToken cancellationToken);
}

/// <summary>Outcome of <see cref="IDepotIdentityTool.GetDepotIdAsync"/>. <see cref="DepotId"/> is non-secret and safe to display/copy/log.</summary>
public sealed record DepotIdentityResult(bool Succeeded, string? DepotId, string? FailureReason)
{
	public static DepotIdentityResult Ok(string depotId) => new(true, depotId, null);

	public static DepotIdentityResult Failed(string reason)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		return new(false, null, reason);
	}
}

/// <summary>Outcome of <see cref="IDepotIdentityTool.ValidateActivationCodeAsync"/>.</summary>
public sealed record DepotValidationResult(bool Succeeded, bool IsAuthFailure, string? FailureReason)
{
	public static DepotValidationResult Ok() => new(true, false, null);

	/// <summary>The tool ran and explicitly rejected the code (bad/expired/revoked, or a portal-role problem) -- an enrollment-state fact, not a runner failure.</summary>
	public static DepotValidationResult AuthFailed(string reason)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		return new(false, true, reason);
	}

	/// <summary>The call itself could not be completed (tool missing, timeout, unexpected exit) -- never conflated with an actual code rejection.</summary>
	public static DepotValidationResult Failed(string reason)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		return new(false, false, reason);
	}
}
