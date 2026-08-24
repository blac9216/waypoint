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

namespace Waypoint.Core.Errors;

/// <summary>
/// The <c>error</c> object of the documented envelope
/// (<c>docs/api-contract.md</c> Conventions): <c>{ "error": { code, message, detail?,
/// blockers? } }</c>. Serialized snake_case by the API's global JSON options.
/// </summary>
/// <param name="Code">Stable, machine-readable error code (e.g. <c>mode_unavailable</c>).</param>
/// <param name="Message">Human-readable summary, safe to show a user.</param>
/// <param name="Detail">Optional extra context; omitted from the payload when null.</param>
/// <param name="Blockers">
/// Issue #593: an optional machine-readable breakdown for a <c>409</c> whose cause is
/// more than one enumerable category (e.g. <c>credential_in_use</c> naming targets,
/// schedules, AND active jobs at once) -- omitted for every other error, and for a
/// <c>409</c> whose cause is a single indivisible fact (e.g. <c>name_taken</c>) that a
/// category/count breakdown would not clarify. <see cref="Message"/> stays the
/// human-readable summary; this is the structured form a caller can branch on without
/// parsing prose.
/// </param>
/// <param name="BindingGaps">
/// Issue #585 (epic #582): an optional machine-readable enumeration of every
/// per-target/per-purpose credential-binding problem that made a run request invalid --
/// the credential-resolution counterpart of <paramref name="Blockers"/>' category/count
/// shape, carrying the (target, purpose, reason) triple a caller (the #587 wizard)
/// needs to point the operator at the exact gap. Omitted for every error whose cause is
/// not a binding-resolution failure.
/// </param>
public sealed record ErrorDetail(
	string Code,
	string Message,
	string? Detail = null,
	IReadOnlyList<BlockingCategory>? Blockers = null,
	IReadOnlyList<CredentialBindingGap>? BindingGaps = null);

/// <summary>
/// One machine-readable per-target/per-purpose credential-resolution failure (issue
/// #585, ADR-0021 §6): which target, which purpose, and why it could not resolve.
/// <see cref="Reason"/> values are the closed <see cref="CredentialBindingGapReasons"/>
/// set; never free text. <see cref="CredentialId"/> names the offending credential for
/// override-shaped reasons (<c>incompatible_credential_type</c>,
/// <c>credential_not_found</c>), null for <c>missing_binding</c>-shaped ones. Identity
/// only -- never secret material.
/// </summary>
public sealed record CredentialBindingGap(
	Guid TargetId,
	string? TargetName,
	string Purpose,
	string Reason,
	Guid? CredentialId = null);

/// <summary>The closed set of <see cref="CredentialBindingGap.Reason"/> values.</summary>
public static class CredentialBindingGapReasons
{
	/// <summary>A required purpose has no target-assigned binding and no override.</summary>
	public const string MissingBinding = "missing_binding";

	/// <summary>The named credential's type is not in the purpose's compatibility set (ADR-0021 §2).</summary>
	public const string IncompatibleCredentialType = "incompatible_credential_type";

	/// <summary>The named override/run-level credential does not exist.</summary>
	public const string CredentialNotFound = "credential_not_found";

	/// <summary>An override names a target outside the run's resolved scope.</summary>
	public const string TargetNotInScope = "target_not_in_scope";

	/// <summary>An override names a purpose the target's kind never uses (ADR-0021 §3).</summary>
	public const string PurposeNotApplicable = "purpose_not_applicable";

	/// <summary>Two overrides name the same (target, purpose) pair.</summary>
	public const string DuplicateOverride = "duplicate_override";
}

/// <summary>
/// One machine-readable reason a request is blocked, plus how many rows are
/// responsible -- e.g. <c>{ category: "targets", count: 2 }</c>. <see cref="Category"/>
/// values are a closed, per-endpoint set (see the endpoint's own doc comment for its
/// list); never free text.
/// </summary>
/// <param name="Category">Stable, machine-readable category name.</param>
/// <param name="Count">Number of blocking rows in this category (always &gt;= 1).</param>
public sealed record BlockingCategory(string Category, int Count);

/// <summary>The envelope itself — the sole top-level shape for every error response.</summary>
public sealed record ErrorResponse(ErrorDetail Error);
