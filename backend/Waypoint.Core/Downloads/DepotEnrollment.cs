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
/// The issue #691 assisted enrollment state machine, in the order an operator
/// actually walks it: no tool means nothing else is even possible; once the tool is
/// installed the appliance can generate a Software Depot ID, then the operator must
/// register that ID at https://vcf.broadcom.com and paste back the issued Activation
/// Code (or paste an existing one directly, validated against the same ID); once
/// stored, the code is either provably good against a bounded noninteractive tool
/// call or provably rejected.
/// </summary>
public static class DepotEnrollmentStates
{
	/// <summary>No managed <c>vcf-download-tool</c> installation is present -- nothing else in this state machine can run.</summary>
	public const string ToolUnavailable = "tool_unavailable";

	/// <summary>The tool is installed, but Waypoint has not yet generated/observed a stable Software Depot ID.</summary>
	public const string DepotIdUnavailable = "depot_id_unavailable";

	/// <summary>The Depot ID is known and displayable; the operator has not yet stored a paired Activation Code.</summary>
	public const string AwaitingPortalRegistration = "awaiting_portal_registration";

	/// <summary>A structurally valid Activation Code has been encrypted and stored, but not yet validated against the tool. No asset_id-vs-Depot-ID match is required (owner decision 2026-08-25: identity follows the code).</summary>
	public const string ActivationCodeStored = "activation_code_stored";

	/// <summary>The last validation succeeded: the tool accepted the stored code (identity was seeded from that code's own asset_id first).</summary>
	public const string Validated = "validated";

	/// <summary>The stored code was validated and REJECTED by the tool (bad/expired/revoked) -- distinct from <see cref="ActivationCodeStored"/> so the UI never claims "pending" once a real auth failure is known.</summary>
	public const string AuthFailing = "auth_failing";

	public static readonly IReadOnlyCollection<string> All =
		[ToolUnavailable, DepotIdUnavailable, AwaitingPortalRegistration, ActivationCodeStored, Validated, AuthFailing];
}

/// <summary>
/// The <c>depot_enrollment</c> singleton row (migration 0048). Never carries the
/// Activation Code itself -- <see cref="DepotId"/> and <see cref="PairedAssetId"/> are
/// both non-secret. The Depot ID is the disposable portal-registration assist for an
/// operator who has no code yet; <see cref="PairedAssetId"/> is now purely informational
/// (the asset_id last stored with a code) and is no longer enforced against the Depot ID
/// (owner decision 2026-08-25: identity follows the code).
/// </summary>
public sealed record DepotEnrollment(
	string State,
	string? DepotId,
	DateTimeOffset? DepotIdGeneratedAt,
	string? PairedAssetId,
	DateTimeOffset? PairedAt,
	string? LastValidationFailure,
	DateTimeOffset? ResetAt,
	DateTimeOffset UpdatedAt);

/// <summary>Storage for the <c>depot_enrollment</c> singleton (issue #691, mirrors <see cref="Waypoint.Core.SystemState.IApplianceStateRepository"/>'s one-implementation convention).</summary>
public interface IDepotEnrollmentRepository
{
	/// <summary>Reads the singleton row. Migration 0048 seeds it unconditionally (<c>id = 1</c>); a null return means the row was deleted out of band.</summary>
	Task<DepotEnrollment?> GetAsync(CancellationToken cancellationToken);

	/// <summary>Records a freshly generated/observed Depot ID and advances state to <see cref="DepotEnrollmentStates.AwaitingPortalRegistration"/> (unless already further along, e.g. re-running generation while already validated is a no-op state-wise). The Depot ID is the disposable portal-registration assist only.</summary>
	Task SetDepotIdAsync(string depotId, CancellationToken cancellationToken);

	/// <summary>Records a stored Activation Code's decoded (informational) <paramref name="assetId"/> and advances state to <see cref="DepotEnrollmentStates.ActivationCodeStored"/>. No asset_id-vs-Depot-ID match is required (owner decision 2026-08-25: identity follows the code) -- any structurally valid code may be stored.</summary>
	Task SetPairedAsync(string assetId, CancellationToken cancellationToken);

	/// <summary>Records the outcome of a bounded noninteractive validation call: <see cref="DepotEnrollmentStates.Validated"/> on success, <see cref="DepotEnrollmentStates.AuthFailing"/> (with a redaction-safe failure note) on rejection.</summary>
	Task SetValidationOutcomeAsync(bool succeeded, string? failureNote, CancellationToken cancellationToken);

	/// <summary>Explicit confirmed identity reset (issue #691 AC): clears the Depot ID/pairing and returns state to <see cref="DepotEnrollmentStates.DepotIdUnavailable"/>, stamping <c>reset_at</c>. Never touches the stored Activation Code credential or any legacy Download Token row -- callers that also want the credential deleted do so separately through <see cref="Waypoint.Core.Secrets.ICredentialSecretStore"/>.</summary>
	Task ResetAsync(CancellationToken cancellationToken);
}
