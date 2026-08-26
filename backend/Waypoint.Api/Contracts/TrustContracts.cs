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

using Waypoint.Core.Trust;

namespace Waypoint.Api.Contracts;

/// <summary>
/// One trust bundle's wire shape (docs/security.md `/trust/bundles`). Never carries
/// <c>pem_chain</c> in the LIST projection (issue #753 AC "see subject, issuer,
/// fingerprint, validity, and usage" -- no requirement to echo the raw PEM text back
/// on every list row); the detail response below adds it back for the one-bundle GET,
/// where an operator legitimately needs to re-export the exact material they uploaded.
/// </summary>
public sealed record TrustBundleSummaryResponse(
	Guid Id,
	string Label,
	string Subject,
	string Issuer,
	string FingerprintSha256,
	DateTimeOffset NotBefore,
	DateTimeOffset NotAfter,
	bool Expired,
	string Status,
	Guid? SupersededById,
	DateTimeOffset? SupersededAt,
	string UploadedBy,
	DateTimeOffset CreatedAt)
{
	public static TrustBundleSummaryResponse FromDomain(TrustBundle bundle, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(bundle);
		return new TrustBundleSummaryResponse(
			bundle.Id, bundle.Label, bundle.Subject, bundle.Issuer, bundle.FingerprintSha256,
			bundle.NotBefore, bundle.NotAfter, bundle.IsExpired(now), bundle.Status,
			bundle.SupersededById, bundle.SupersededAt, bundle.UploadedBy, bundle.CreatedAt);
	}
}

/// <summary>Detail response for one bundle -- adds the exact uploaded PEM text (public material, never a secret) for re-export/verification.</summary>
public sealed record TrustBundleDetailResponse(
	Guid Id,
	string Label,
	string PemChain,
	string Subject,
	string Issuer,
	string FingerprintSha256,
	DateTimeOffset NotBefore,
	DateTimeOffset NotAfter,
	bool Expired,
	string Status,
	Guid? SupersededById,
	DateTimeOffset? SupersededAt,
	string UploadedBy,
	DateTimeOffset CreatedAt)
{
	public static TrustBundleDetailResponse FromDomain(TrustBundle bundle, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(bundle);
		return new TrustBundleDetailResponse(
			bundle.Id, bundle.Label, bundle.PemChain, bundle.Subject, bundle.Issuer, bundle.FingerprintSha256,
			bundle.NotBefore, bundle.NotAfter, bundle.IsExpired(now), bundle.Status,
			bundle.SupersededById, bundle.SupersededAt, bundle.UploadedBy, bundle.CreatedAt);
	}
}

/// <summary>Wire shape for one scoped trust-policy binding (docs/security.md `PUT /connections/{id}/trust-policy`, generalized to this slice's (scope_type, scope_id) pair).</summary>
public sealed record TrustPolicyResponse(
	Guid Id,
	string ScopeType,
	string ScopeId,
	string Mode,
	Guid? TrustBundleId,
	string? BypassReason,
	string Status,
	DateTimeOffset? SupersededAt,
	string Actor,
	DateTimeOffset CreatedAt)
{
	public static TrustPolicyResponse FromDomain(TrustPolicy policy)
	{
		ArgumentNullException.ThrowIfNull(policy);
		return new TrustPolicyResponse(
			policy.Id, policy.ScopeType, policy.ScopeId, policy.Mode, policy.TrustBundleId, policy.BypassReason,
			policy.Status, policy.SupersededAt, policy.Actor, policy.CreatedAt);
	}
}

/// <summary>
/// Request body for <c>PUT /trust/policies/{scope_type}/{scope_id}</c>. Exactly one of
/// (<see cref="TrustBundleId"/>) or (<see cref="BypassReason"/>) is expected depending
/// on <see cref="Mode"/> -- validated by the controller against
/// <c>TrustPolicyModes</c>, mirroring migration 0059's own CHECK constraint so an
/// application-layer bug can never produce a request the database would reject anyway.
/// </summary>
public sealed record TrustPolicyRequest(string? Mode, Guid? TrustBundleId, string? BypassReason);

/// <summary>Request body for <c>POST /trust/bundles</c> when supplied as JSON rather than multipart form (label plus raw PEM text) -- see <c>TrustController.Upload</c> for the accepted content types.</summary>
public sealed record TrustBundleUploadRequest(string? Label, string? PemChain);
