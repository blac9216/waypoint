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

namespace Waypoint.Core.Trust;

/// <summary>Migration 0059's <c>trust_bundles.status</c> vocabulary.</summary>
public static class TrustBundleStatuses
{
	public const string Active = "active";
	public const string Superseded = "superseded";
}

/// <summary>Migration 0059's <c>trust_policies.mode</c> vocabulary.</summary>
public static class TrustPolicyModes
{
	public const string Bundle = "bundle";
	public const string Bypass = "bypass";
}

/// <summary>Migration 0059's <c>trust_policies.status</c> vocabulary.</summary>
public static class TrustPolicyStatuses
{
	public const string Current = "current";
	public const string Superseded = "superseded";
}

/// <summary>
/// Migration 0059's <c>trust_policies.scope_type</c> vocabulary -- deliberately the
/// narrow set THIS slice's controller accepts (docs/testing.md derive-the-axis
/// idiom applied to a closed enum rather than a free string): a top-level target, or
/// this repo's two already-shipped STIG Manager connection shapes
/// (<c>StigManagerController</c>'s global/per-site connections). Widening this list is
/// an additive CHECK constraint change, never a column-shape change (migration 0059's
/// own header comment).
/// </summary>
public static class TrustScopeTypes
{
	public const string Target = "target";
	public const string StigManagerGlobal = "stigman-global";
	public const string StigManagerSite = "stigman-site";

	public static readonly IReadOnlyList<string> All = [Target, StigManagerGlobal, StigManagerSite];
}

/// <summary>
/// One immutable, Admin-uploaded CA certificate/chain (migration 0059's
/// <c>trust_bundles</c>). Public material, not a secret (docs/security.md) -- stored
/// and returned as plain PEM text, never envelope-encrypted.
/// </summary>
public sealed record TrustBundle(
	Guid Id,
	string Label,
	string PemChain,
	string Subject,
	string Issuer,
	string FingerprintSha256,
	DateTimeOffset NotBefore,
	DateTimeOffset NotAfter,
	string Status,
	Guid? SupersededById,
	DateTimeOffset? SupersededAt,
	string UploadedBy,
	DateTimeOffset CreatedAt)
{
	public bool IsExpired(DateTimeOffset asOf) => asOf >= NotAfter;
}

/// <summary>
/// One scoped trust-policy binding (migration 0059's <c>trust_policies</c>): either a
/// reference to a <see cref="TrustBundle"/> (<see cref="TrustPolicyModes.Bundle"/>) or
/// an explicit, reasoned, audited skip-verification decision
/// (<see cref="TrustPolicyModes.Bypass"/>). Never process-global -- always scoped to
/// exactly one (<see cref="ScopeType"/>, <see cref="ScopeId"/>) pair (ADR-0025).
/// </summary>
public sealed record TrustPolicy(
	Guid Id,
	string ScopeType,
	string ScopeId,
	string Mode,
	Guid? TrustBundleId,
	string? BypassReason,
	string Status,
	DateTimeOffset? SupersededAt,
	string Actor,
	DateTimeOffset CreatedAt);

/// <summary>Non-secret validation-failure classification for a rejected upload (issue #753 AC "fail safely with actionable errors").</summary>
public enum TrustBundleValidationOutcome
{
	Valid,
	Empty,
	OversizedInput,
	Malformed,
	ContainsPrivateKey,
	Expired,
	DuplicateFingerprint,
}

/// <summary>
/// The parsed, validated result of one upload attempt -- <see cref="Outcome"/> other
/// than <see cref="TrustBundleValidationOutcome.Valid"/> means every other field is
/// meaningless and the caller must never persist or log <c>PemChain</c> from a failed
/// attempt (issue #753 AC "a private key in an upload must be REJECTED and never
/// persisted or logged").
/// </summary>
public sealed record TrustBundleValidationResult(
	TrustBundleValidationOutcome Outcome,
	string? Label,
	string? PemChain,
	string? Subject,
	string? Issuer,
	string? FingerprintSha256,
	DateTimeOffset? NotBefore,
	DateTimeOffset? NotAfter,
	string? SafeErrorMessage)
{
	public bool IsValid => Outcome == TrustBundleValidationOutcome.Valid;
}

/// <summary>Outcome of a delete attempt against a referenced or already-superseded row.</summary>
public enum TrustBundleDeleteOutcome
{
	Deleted,
	NotFound,
	Referenced,
}

/// <summary>Outcome of setting/superseding a scoped trust policy.</summary>
public enum TrustPolicyWriteOutcome
{
	Written,
	TrustBundleNotFound,
	TrustBundleSuperseded,
}
