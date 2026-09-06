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

using System.Text.Json.Serialization;
using Waypoint.Core.Downloads;

namespace Waypoint.Api.Contracts;

/// <summary>
/// Wire contracts for <c>RetentionController</c> (<c>/api/v1/download-retention</c>,
/// issue #1453, epic #1182). Every domain type from <c>Waypoint.Core.Downloads</c>
/// (<see cref="RetainedContentState"/>, <see cref="RetentionPolicy"/>,
/// <see cref="ReviewListEntry"/>) is projected into its own response record here --
/// no domain type is ever returned directly.
/// </summary>
public sealed record RetainedContentStateResponse(
	Guid Id,
	[property: JsonPropertyName("depot_artifact_id")] Guid DepotArtifactId,
	string State,
	[property: JsonPropertyName("grace_started_at")] DateTimeOffset? GraceStartedAt,
	[property: JsonPropertyName("pinned_by")] string? PinnedBy,
	[property: JsonPropertyName("pinned_at")] DateTimeOffset? PinnedAt,
	[property: JsonPropertyName("pin_note")] string? PinNote,
	[property: JsonPropertyName("purged_at")] DateTimeOffset? PurgedAt,
	[property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
	[property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt)
{
	public static RetainedContentStateResponse FromDomain(RetainedContentState state) => new(
		state.Id, state.DepotArtifactId, state.State, state.GraceStartedAt,
		state.PinnedBy, state.PinnedAt, state.PinNote, state.PurgedAt, state.CreatedAt, state.UpdatedAt);
}

/// <summary>Body for <c>POST /api/v1/download-retention/{id}/pin</c>. <see cref="Note"/> is optional operator context, never secret material.</summary>
public sealed record PinRetainedContentRequest(string? Note);

/// <summary>Body for <c>POST /api/v1/download-retention/{id}/purge-now</c>.</summary>
public sealed record PurgeNowRequest(string? Reason);

/// <summary>Response for <c>POST /api/v1/download-retention/{id}/purge-now</c>.</summary>
public sealed record PurgeNowResponse(
	[property: JsonPropertyName("retained_content_state_id")] Guid RetainedContentStateId,
	bool Purged,
	string? Error);

/// <summary>Response for <c>GET /api/v1/download-retention/dial</c>.</summary>
public sealed record RetentionDialResponse(
	[property: JsonPropertyName("scope_key")] string ScopeKey,
	string Dial);

/// <summary>
/// Body for <c>PUT /api/v1/download-retention/dial</c>. <see cref="ScopeKey"/> defaults
/// to <see cref="RetentionPolicyScopes.Default"/> when omitted -- setting the
/// appliance-wide fallback is the common case until #1421 gives per-subscription
/// scope keys a real identity.
/// </summary>
public sealed record SetRetentionDialRequest(
	[property: JsonPropertyName("scope_key")] string? ScopeKey,
	string Dial);

/// <summary>One row of <c>GET /api/v1/download-retention/review-list</c>.</summary>
public sealed record ReviewListEntryResponse(
	string Kind,
	[property: JsonPropertyName("depot_artifact_id")] Guid? DepotArtifactId,
	[property: JsonPropertyName("relative_path")] string RelativePath,
	[property: JsonPropertyName("size_bytes")] long? SizeBytes,
	string? Reason,
	[property: JsonPropertyName("first_seen_at")] DateTimeOffset FirstSeenAt,
	[property: JsonPropertyName("last_seen_at")] DateTimeOffset LastSeenAt)
{
	public static ReviewListEntryResponse FromDomain(ReviewListEntry entry) => new(
		entry.Kind.ToString(), entry.DepotArtifactId, entry.RelativePath, entry.SizeBytes,
		entry.Reason, entry.FirstSeenAt, entry.LastSeenAt);
}

/// <summary>
/// Body for <c>DELETE /api/v1/download-retention/review-list</c> -- the sole path by
/// which orphan/out-of-scope content can ever be removed (issue #1453). Exactly one of
/// <see cref="DepotArtifactId"/> (for an <c>OutOfScope</c> entry) or
/// <see cref="RelativePath"/> (for an <c>Orphan</c> entry) is required, matching
/// <see cref="Kind"/>.
/// </summary>
public sealed record DeleteReviewListEntryRequest(
	string Kind,
	[property: JsonPropertyName("depot_artifact_id")] Guid? DepotArtifactId,
	[property: JsonPropertyName("relative_path")] string? RelativePath,
	string? Reason);

/// <summary>Response for <c>DELETE /api/v1/download-retention/review-list</c>.</summary>
public sealed record DeleteReviewListEntryResponse(bool Deleted, string? Error);
