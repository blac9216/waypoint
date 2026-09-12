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

using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.Downloads;
using Waypoint.Core.Errors;
using Waypoint.Core.Pagination;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The retention engine's HTTP surface (issue #1453, epic #1182, split from design
/// record #1047; approved design #16 sections 2/8): grace/pending-prune state, pin/
/// unpin, purge-now, the manual-download dial, and the review list. Thin over the
/// already-built domain services from #1406 (<see cref="IRetainedContentStateRepository"/>),
/// #1436 (<see cref="IRetentionSweepService"/>), and #1440
/// (<see cref="IReviewListService"/>, <see cref="IReviewListDeletionService"/>) --
/// this controller adds no new domain logic. RBAC per design #16 section 8: Admin
/// manages subscriptions/retention dials/deletes/purges; Operator and Viewer both get
/// read-only access to state and the review list (Operator is not distinguished from
/// Viewer here -- neither may mutate retention state, matching #1048's own RBAC
/// requirement this controller's contract must satisfy).
///
/// Route is <c>/api/v1/download-retention</c>, deliberately distinct from the existing
/// <c>/api/v1/retention-policy</c> (<see cref="RetentionPolicyController"/>), which is
/// unrelated appliance-wide *compliance evidence* retention (issue #1062, migration
/// 0078) -- same English word, two different domains and migrations.
/// </summary>
[ApiController]
[Route("api/v1/download-retention")]
public sealed class RetentionController : ControllerBase
{
	private readonly IRetainedContentStateRepository _states;
	private readonly IRetentionPolicyRepository _policies;
	private readonly IRetentionSweepService _sweep;
	private readonly IReviewListService _reviewList;
	private readonly IReviewListDeletionService _reviewListDeletion;

	private static readonly string[] ListableStates =
	[
		RetainedContentStates.Grace,
		RetainedContentStates.PendingPurge,
		RetainedContentStates.Pinned
	];

	public RetentionController(
		IRetainedContentStateRepository states,
		IRetentionPolicyRepository policies,
		IRetentionSweepService sweep,
		IReviewListService reviewList,
		IReviewListDeletionService reviewListDeletion)
	{
		ArgumentNullException.ThrowIfNull(states);
		ArgumentNullException.ThrowIfNull(policies);
		ArgumentNullException.ThrowIfNull(sweep);
		ArgumentNullException.ThrowIfNull(reviewList);
		ArgumentNullException.ThrowIfNull(reviewListDeletion);

		_states = states;
		_policies = policies;
		_sweep = sweep;
		_reviewList = reviewList;
		_reviewListDeletion = reviewListDeletion;
	}

	/// <summary>
	/// The three ADR-0034 states an operator must be able to tell apart --
	/// <c>grace</c> (approaching the grace period), <c>pending-purge</c> (past it),
	/// and <c>pinned</c> (exempt, sweep will skip) -- per <c>docs/reference/api-contract.md</c>'s
	/// row for this endpoint. Paged the same way as <c>CatalogController.ListArtifacts</c>
	/// on the wire (<see cref="PageRequest"/>, <c>X-Total-Count</c> header) but not
	/// underneath it: <see cref="IRetainedContentStateRepository.ListByStateAsync"/>
	/// has no <see cref="PageRequest"/> overload, so this fetches each matching state
	/// in full and pages/counts in memory here -- fine at today's row counts, not a
	/// claim of repository-level paging parity. Narrowable to a single state via the
	/// optional <paramref name="state"/> query parameter (an unrecognized value is
	/// 400); omitted, it lists all three. "Per subscription scope" (this issue's own
	/// Proposed Changes) awaits a real Subscription entity (#1421 is still open, the
	/// same deferred-discovery seam <see cref="IRetentionSweepService"/>'s own doc
	/// comment documents) -- until then this lists the appliance-wide set.
	/// </summary>
	[HttpGet("state")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(RetainedContentStateResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<RetainedContentStateResponse>>> ListState([FromQuery] PageRequest page, [FromQuery] string? state, CancellationToken cancellationToken)
	{
		string[] states;
		if (string.IsNullOrWhiteSpace(state))
		{
			// Issue #1786: derives the default listing from the SAME closed set
			// ListableStates already declares for the ?state= validator below,
			// instead of a second, independently-maintained literal array -- a
			// mutation mistakenly deleting one entry from ListableStates alone
			// used to leave this branch silently unaffected.
			states = ListableStates;
		}
		else if (Array.IndexOf(ListableStates, state) >= 0)
		{
			states = [state];
		}
		else
		{
			throw ApiException.Validation($"'state' must be one of: {string.Join(", ", ListableStates)}.");
		}

		List<RetainedContentState> all = [];
		foreach (string candidate in states)
		{
			all.AddRange(await _states.ListByStateAsync(candidate, cancellationToken).ConfigureAwait(false));
		}

		// Issue #1787: Id is a deterministic secondary sort key, breaking a
		// CreatedAt tie -- List<T>.Sort is an unstable introsort, so without this
		// two rows sharing one CreatedAt (rare but possible: created_at defaults to
		// the per-transaction now(), and EnsureTrackedAsync opens its own
		// connection per call) could swap order between two successive page reads,
		// letting an in-memory-paged client see one twice or miss it entirely.
		all.Sort((a, b) =>
		{
			int byCreatedAt = a.CreatedAt.CompareTo(b.CreatedAt);
			return byCreatedAt != 0 ? byCreatedAt : a.Id.CompareTo(b.Id);
		});

		Response.Headers["X-Total-Count"] = all.Count.ToString(CultureInfo.InvariantCulture);
		IReadOnlyList<RetainedContentState> pageItems = all.Skip(page.Offset).Take(page.Limit).ToArray();

		List<RetainedContentStateResponse> responses = new(pageItems.Count);
		foreach (RetainedContentState item in pageItems)
		{
			responses.Add(await ToResponseAsync(item, cancellationToken).ConfigureAwait(false));
		}
		return Ok(responses);
	}

	/// <summary>
	/// Pins content, protecting it from grace/auto-prune/purge-now. 409
	/// <c>illegal_transition</c> (not an unmapped 500) when
	/// <see cref="RetainedContentStateTransitions.CanPin"/> rejects the row's current
	/// state (already <c>pending-purge</c> or <c>purged</c>).
	/// </summary>
	[HttpPost("{id:guid}/pin")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(RetainedContentStateResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<ActionResult<RetainedContentStateResponse>> Pin(Guid id, [FromBody] PinRetainedContentRequest? request, CancellationToken cancellationToken)
	{
		RetainedContentState? current = await _states.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (current is null)
		{
			throw ApiException.NotFound($"No retained-content-state row with id '{id}'.");
		}

		string actor = User.GetRequiredUsername();
		try
		{
			await _states.PinAsync(id, actor, request?.Note, cancellationToken).ConfigureAwait(false);
		}
		catch (InvalidOperationException exception)
		{
			throw new ApiException(HttpStatusCode.Conflict, "illegal_transition", exception.Message);
		}

		RetainedContentState updated = (await _states.GetAsync(id, cancellationToken).ConfigureAwait(false))!;
		return Ok(await ToResponseAsync(updated, cancellationToken).ConfigureAwait(false));
	}

	/// <summary>
	/// Unpins content, moving it back to <c>tracked</c> so it re-enters the normal
	/// grace/auto-prune lifecycle. Same 409 mapping as <see cref="Pin"/> for an illegal
	/// transition (e.g. content that was never pinned). Clears
	/// <c>pinned_by</c>/<c>pinned_at</c>/<c>pin_note</c> on this transition (issue
	/// #1624) -- <see cref="IRetainedContentStateRepository.TransitionAsync(Guid,string,CancellationToken)"/>
	/// clears those three columns whenever the row leaves <c>pinned</c>, matching
	/// migration 0107's own column comment ("NULL when not pinned").
	/// </summary>
	[HttpPost("{id:guid}/unpin")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(RetainedContentStateResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<ActionResult<RetainedContentStateResponse>> Unpin(Guid id, CancellationToken cancellationToken)
	{
		RetainedContentState? current = await _states.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (current is null)
		{
			throw ApiException.NotFound($"No retained-content-state row with id '{id}'.");
		}

		try
		{
			await _states.TransitionAsync(id, RetainedContentStates.Tracked, cancellationToken).ConfigureAwait(false);
		}
		catch (InvalidOperationException exception)
		{
			throw new ApiException(HttpStatusCode.Conflict, "illegal_transition", exception.Message);
		}

		RetainedContentState updated = (await _states.GetAsync(id, cancellationToken).ConfigureAwait(false))!;
		return Ok(await ToResponseAsync(updated, cancellationToken).ConfigureAwait(false));
	}

	/// <summary>
	/// Purges immediately, without waiting for the grace window -- delegates to
	/// <see cref="IRetentionSweepService.PurgeImmediatelyAsync"/> for the transition
	/// walk, path-confined delete, and per-file logged deletion trail. Already-pinned
	/// content is refused with 409 <c>content_pinned</c> (unpin first); already-purged
	/// content comes back 200 with <see cref="PurgeNowResponse.Purged"/> <c>false</c>
	/// and <see cref="PurgeNowResponse.Error"/> set to the service's "already purged"
	/// message -- this endpoint never turns that into an error response itself. #1662
	/// (open, not this issue's job) tracks a *different* consumer of the same
	/// service call, the retention-sweep job handler, folding that same non-null
	/// <c>Error</c> into its own failure count on rerun; nothing here claims that gap
	/// is closed.
	/// </summary>
	[HttpPost("{id:guid}/purge-now")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(PurgeNowResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<ActionResult<PurgeNowResponse>> PurgeNow(Guid id, [FromBody] PurgeNowRequest? request, CancellationToken cancellationToken)
	{
		RetainedContentState? current = await _states.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (current is null)
		{
			throw ApiException.NotFound($"No retained-content-state row with id '{id}'.");
		}

		if (string.Equals(current.State, RetainedContentStates.Pinned, StringComparison.Ordinal))
		{
			throw new ApiException(HttpStatusCode.Conflict, "content_pinned", "Content is pinned; unpin before purging.");
		}

		string actor = User.GetRequiredUsername();
		RetentionPurgeOutcome outcome = await _sweep.PurgeImmediatelyAsync(id, actor, request?.Reason, cancellationToken).ConfigureAwait(false);
		return Ok(new PurgeNowResponse(outcome.RetainedContentStateId, outcome.Purged, outcome.Error));
	}

	/// <summary>The effective manual-download dial for <paramref name="scopeKey"/> (defaults to <see cref="RetentionPolicyScopes.Default"/>).</summary>
	[HttpGet("dial")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(RetentionDialResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<RetentionDialResponse>> GetDial([FromQuery(Name = "scope_key")] string? scopeKey, CancellationToken cancellationToken)
	{
		string effectiveScopeKey = string.IsNullOrWhiteSpace(scopeKey) ? RetentionPolicyScopes.Default : scopeKey;
		RetentionPolicy policy = await ResolvePolicyAsync(effectiveScopeKey, cancellationToken).ConfigureAwait(false);
		return Ok(new RetentionDialResponse(policy.ScopeKey, policy.ManualDownloadDialDefault));
	}

	/// <summary>
	/// Sets the manual-download dial for a scope (upserts the whole policy row --
	/// <see cref="IRetentionPolicyRepository.UpsertAsync"/> has no partial-update form
	/// -- so this reads the scope's current grace settings first, or the
	/// <see cref="RetentionPolicyScopes.Default"/> scope's when the target scope has
	/// no policy of its own yet, and carries them forward unchanged).
	/// </summary>
	[HttpPut("dial")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(RetentionDialResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<RetentionDialResponse>> SetDial([FromBody] SetRetentionDialRequest? request, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(request?.Dial))
		{
			throw ApiException.Validation("'dial' is required.");
		}

		string dial;
		try
		{
			dial = ManualDownloadRetentionDialResolver.ToWireValue(ManualDownloadRetentionDialResolver.Parse(request.Dial));
		}
		catch (ArgumentException exception)
		{
			throw ApiException.Validation(exception.Message);
		}

		string scopeKey = string.IsNullOrWhiteSpace(request.ScopeKey) ? RetentionPolicyScopes.Default : request.ScopeKey;
		RetentionPolicy basis = await ResolvePolicyAsync(scopeKey, cancellationToken).ConfigureAwait(false);

		Guid id = await _policies.UpsertAsync(scopeKey, basis.GracePeriodDays, basis.GraceMaxRefreshes, dial, cancellationToken).ConfigureAwait(false);
		RetentionPolicy updated = (await _policies.GetAsync(id, cancellationToken).ConfigureAwait(false))!;
		return Ok(new RetentionDialResponse(updated.ScopeKey, updated.ManualDownloadDialDefault));
	}

	/// <summary>
	/// Issue #1962: projects <paramref name="state"/> to its response shape with
	/// <c>grace_ends_at</c> resolved from the SAME authoritative grace-period source
	/// the retention purge scheduler itself reads (<see cref="ResolveGracePeriodDaysAsync"/>),
	/// never a client-side guess or a hardcoded default.
	/// </summary>
	private async Task<RetainedContentStateResponse> ToResponseAsync(RetainedContentState state, CancellationToken cancellationToken)
	{
		int? gracePeriodDays = await ResolveGracePeriodDaysAsync(state, cancellationToken).ConfigureAwait(false);
		return RetainedContentStateResponse.FromDomain(state, gracePeriodDays);
	}

	/// <summary>
	/// Issue #1962: mirrors <c>RetentionSweepService.RunSweepAsync</c>'s own auto-prune-
	/// pass policy resolution for a grace-state row -- an explicit <c>policy_id</c> when
	/// the row carries one, else the <see cref="RetentionPolicyScopes.Default"/> scope's
	/// policy -- so <c>grace_ends_at</c> can never disagree with the window the scheduler
	/// actually purges against. Short-circuits to null for a row that is not currently
	/// in <c>grace</c> (no grace period is "ending" at all) or one with no
	/// <see cref="RetainedContentState.GraceStartedAt"/> yet, without an extra policy
	/// lookup neither case needs.
	/// </summary>
	private async Task<int?> ResolveGracePeriodDaysAsync(RetainedContentState state, CancellationToken cancellationToken)
	{
		if (!string.Equals(state.State, RetainedContentStates.Grace, StringComparison.Ordinal) || state.GraceStartedAt is null)
		{
			return null;
		}

		RetentionPolicy? policy = state.PolicyId is { } policyId
			? await _policies.GetAsync(policyId, cancellationToken).ConfigureAwait(false)
			: await _policies.GetByScopeKeyAsync(RetentionPolicyScopes.Default, cancellationToken).ConfigureAwait(false);

		return policy?.GracePeriodDays;
	}

	/// <summary>Resolves <paramref name="scopeKey"/>'s policy, falling back to <see cref="RetentionPolicyScopes.Default"/> -- migration 0107 seeds that row unconditionally, so an unresolvable Default is a server-side integrity problem.</summary>
	private async Task<RetentionPolicy> ResolvePolicyAsync(string scopeKey, CancellationToken cancellationToken)
	{
		RetentionPolicy? policy = await _policies.GetByScopeKeyAsync(scopeKey, cancellationToken).ConfigureAwait(false);
		if (policy is not null)
		{
			return policy;
		}

		RetentionPolicy? fallback = await _policies.GetByScopeKeyAsync(RetentionPolicyScopes.Default, cancellationToken).ConfigureAwait(false);
		return fallback ?? throw new ApiException(HttpStatusCode.InternalServerError, "retention_policy_missing", "The seeded default retention policy is missing.");
	}

	/// <summary>The union of orphans and out-of-scope content (issue #1440); never removed except via <see cref="DeleteReviewListEntry"/> below.</summary>
	[HttpGet("review-list")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ReviewListEntryResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<ReviewListEntryResponse>>> GetReviewList(CancellationToken cancellationToken)
	{
		IReadOnlyList<ReviewListEntry> entries = await _reviewList.ListAsync(cancellationToken).ConfigureAwait(false);
		return Ok(entries.Select(ReviewListEntryResponse.FromDomain).ToArray());
	}

	/// <summary>
	/// The sole path by which orphan/out-of-scope content can ever be removed (this
	/// issue's own Proposed Changes) -- Admin-only. 404 when no matching entry is
	/// currently on the review list (rather than silently no-opping a delete of
	/// something never reported).
	/// </summary>
	[HttpDelete("review-list")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(DeleteReviewListEntryResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<DeleteReviewListEntryResponse>> DeleteReviewListEntry([FromBody] DeleteReviewListEntryRequest? request, CancellationToken cancellationToken)
	{
		if (!Enum.TryParse(request?.Kind, ignoreCase: true, out ReviewListEntryKind kind))
		{
			throw ApiException.Validation($"'kind' must be one of: {string.Join(", ", Enum.GetNames<ReviewListEntryKind>())}.");
		}

		IReadOnlyList<ReviewListEntry> entries = await _reviewList.ListAsync(cancellationToken).ConfigureAwait(false);
		bool onReviewList = kind == ReviewListEntryKind.OutOfScope
			? entries.Any(e => e.Kind == kind && e.DepotArtifactId == request!.DepotArtifactId)
			: entries.Any(e => e.Kind == kind && e.RelativePath == request!.RelativePath);
		if (!onReviewList)
		{
			throw ApiException.NotFound("No matching review-list entry exists.");
		}

		string actor = User.GetRequiredUsername();
		ReviewListDeletionOutcome outcome = await _reviewListDeletion
			.DeleteAsync(kind, request!.DepotArtifactId, request.RelativePath, actor, request.Reason, cancellationToken)
			.ConfigureAwait(false);
		return Ok(new DeleteReviewListEntryResponse(outcome.Deleted, outcome.Error));
	}
}
