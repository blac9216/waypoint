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

namespace Waypoint.Core.Secrets;

/// <summary>
/// The closed set of credential types (docs/domain-model.md "Credential", plus
/// <see cref="DepotToken"/>). Migration 0022 (issue #252) adds a DB CHECK mirroring
/// this set -- same split <c>Waypoint.Core.Sites.TargetKinds</c>/<c>targets.kind</c>
/// uses: API-layer validation here in <c>CredentialsController</c>, backed by a DB
/// CHECK for defense-in-depth. <see cref="DepotToken"/> is not one of
/// domain-model.md's four user-facing connection types -- it is the single
/// well-known row <c>CatalogIndexJobHandler</c> resolves via
/// <c>FindByTypeAsync(CatalogOptions.DepotTokenCredentialType)</c> to authenticate
/// catalog-index runs to the Broadcom depot (docs/roadmap.md), so it belongs in the
/// closed set even though it isn't created through the same connection-credential UI
/// flow as the other four.
/// </summary>
public static class CredentialTypes
{
	public const string VCenter = "vcenter";
	public const string Nsx = "nsx";
	public const string Ssh = "ssh";
	public const string Token = "token";
	public const string DepotToken = "depot-token";

	public static readonly IReadOnlyCollection<string> All = [VCenter, Nsx, Ssh, Token, DepotToken];

	public static bool IsValid(string? credentialType) => credentialType is not null && All.Contains(credentialType);
}

/// <summary>
/// The closed set of credential ownership values. ADR-0011: SHARED ONLY in v1 -- no
/// personal/per-user credential rows. A single value today, but kept as a named set
/// (rather than a bare string literal) so the deferred passphrase-wrapped personal
/// tier ADR-0011 describes is an addition here, not a hunt through call sites.
/// </summary>
public static class CredentialOwners
{
	public const string Shared = "shared";

	public static readonly IReadOnlyCollection<string> All = [Shared];
}

/// <summary>
/// The closed set of credential health values (migration 0001's
/// <c>credentials_health_check</c>). <c>Unknown</c> is the initial state before any
/// halt or test has spoken; <c>Valid</c> and <c>AuthFailing</c> are both "proven"
/// states -- the auth-failure halt sets <c>AuthFailing</c>, and only a successful
/// <c>/credentials/{id}/test</c> call sets <c>Valid</c> back (see
/// <c>CredentialRepository.MarkTestOutcomeAsync</c>'s doc comment for why a bare
/// queue-halt unblock does not).
/// </summary>
public static class CredentialHealthStates
{
	public const string Unknown = "unknown";
	public const string Valid = "valid";
	public const string AuthFailing = "auth_failing";
}

/// <summary>
/// The ONLY credential shape that ever leaves the API (ADR-0005 / security.md
/// control 3: write-only, enforced at the serialization layer). There is no secret
/// field to mask because none exists -- <c>CredentialResponseHasNoSecretFieldTests</c>
/// walks this type and fails the build's test run if one ever appears.
/// </summary>
/// <param name="LastTestedAt">
/// Issue #560 (migration 0035): stamped by every <c>credential-test</c> job outcome,
/// success or failure, any credential_type -- null until the first test ever runs.
/// </param>
/// <param name="ExpiresAt">
/// Issue #560 (migration 0035): null means "unknown", never "no expiry" -- this field
/// is only ever set from a real upstream-supplied date (CLAUDE.md: never invent one).
/// Nothing in this slice writes it yet; it exists so the Depot &amp; Tokens screen can
/// render "expiry unknown" rather than treating an unpopulated Broadcom response the
/// same as "known, and far away".
/// </param>
public sealed record CredentialResponse(
	Guid Id,
	string Name,
	string CredentialType,
	string Owner,
	string Health,
	bool SudoEnabled,
	bool HasSecret,
	long UsedByJobCount,
	DateTimeOffset? RotatedAt,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt,
	string? Username = null,
	DateTimeOffset? LastTestedAt = null,
	DateTimeOffset? ExpiresAt = null);

/// <summary>Create request: metadata plus optional initial secret material (in only; UTF-8). <see cref="SudoEnabled"/> is only meaningful for <see cref="CredentialTypes.Ssh"/> (validated at the controller). <see cref="Username"/> is not secret material (migration 0012) -- it is the protocol-level login a connection-type (vcenter/nsx/ssh) credential's job handler presents, distinct from <see cref="Name"/>'s human-facing label.</summary>
public sealed record CredentialCreateRequest(string? Name, string? CredentialType, string? Owner, bool? SudoEnabled, string? Secret, string? Username = null);

/// <summary>Update request: rename, flip <see cref="SudoEnabled"/>, change <see cref="Username"/>, and/or rotate. A non-null <paramref name="Secret"/> replaces the stored blob and stamps <c>rotated_at</c>.</summary>
public sealed record CredentialUpdateRequest(string? Name, bool? SudoEnabled, string? Secret, string? Username = null);

/// <summary>
/// Response for <c>POST /credentials/{id}/test</c> (issue #245): 202, the queued
/// <c>credential-test</c> job's run/job ids, mirroring <c>DiscoverQueuedResponse</c>'s
/// shape. Replaces the old synchronous 200 <c>CredentialTestResponse</c> (issue #20's
/// decrypt-liveness-only check) -- the real per-type connectivity probe now runs as a
/// PowerShell job (<c>Waypoint.Infrastructure.Credentials.CredentialTestJobHandler</c>)
/// through the #6/#194 job-handler pattern, and its terminal outcome (not the
/// controller) flips <c>credentials.health</c>.
/// </summary>
public sealed record CredentialTestQueuedResponse(Guid RunId, Guid JobId);
