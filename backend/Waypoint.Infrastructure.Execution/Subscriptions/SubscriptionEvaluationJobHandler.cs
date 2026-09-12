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

using System.Text.Json;
using Waypoint.Core.Catalog;
using Waypoint.Core.Jobs;
using Waypoint.Core.Pagination;
using Waypoint.Core.Subscriptions;

namespace Waypoint.Infrastructure.Execution.Subscriptions;

/// <summary>
/// The <c>subscription-evaluate</c> <see cref="JobShape.Simple"/> job handler (issue
/// #1472, epic #1182, download-runner domain): resolves the subscription, diffs it
/// against the indexed catalog via <see cref="SubscriptionEvaluationService"/> (fetch
/// set + projected bytes, lib.json version-counter pre-check), and persists the
/// result. It does NOT itself call <see cref="IJobControlRepository.CreateRunAsync"/>/
/// <see cref="IJobControlRepository.FanOutJobsAsync"/> -- see
/// <c>Waypoint.Infrastructure.Subscriptions.SubscriptionEvaluationFanOutService</c>'s
/// doc comment for why that step runs under the owner-privileged API connection
/// instead. Payload: <c>{"subscription_id": "&lt;guid&gt;"}</c>.
/// </summary>
public sealed class SubscriptionEvaluationJobHandler : IJobHandler
{
	private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
	};

	private readonly ISubscriptionRepository _subscriptions;
	private readonly IDepotArtifactRepository _artifacts;
	private readonly ISubscriptionEvaluationStateRepository _state;
	private readonly SubscriptionEvaluationService _evaluation;

	public SubscriptionEvaluationJobHandler(
		ISubscriptionRepository subscriptions,
		IDepotArtifactRepository artifacts,
		ISubscriptionEvaluationStateRepository state,
		SubscriptionEvaluationService evaluation)
	{
		ArgumentNullException.ThrowIfNull(subscriptions);
		ArgumentNullException.ThrowIfNull(artifacts);
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(evaluation);
		_subscriptions = subscriptions;
		_artifacts = artifacts;
		_state = state;
		_evaluation = evaluation;
	}

	public string JobType => "subscription-evaluate";

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		Payload? payload;
		try
		{
			payload = JsonSerializer.Deserialize<Payload>(context.Job.Payload, PayloadOptions);
		}
		catch (JsonException exception)
		{
			return JobExecutionOutcome.Failed($"Malformed subscription-evaluate payload: {exception.Message}");
		}

		if (payload?.SubscriptionId is null)
		{
			return JobExecutionOutcome.Failed("subscription-evaluate payload requires a 'subscription_id'.");
		}

		Subscription? subscription = await _subscriptions.GetAsync(payload.SubscriptionId.Value, cancellationToken).ConfigureAwait(false);
		if (subscription is null)
		{
			return JobExecutionOutcome.Failed($"Subscription '{payload.SubscriptionId}' does not exist.");
		}

		if (!subscription.IsEnabled)
		{
			return JobExecutionOutcome.Succeeded("Subscription is disabled; nothing evaluated.");
		}

		SubscriptionEvaluationState? priorState = await _state.GetAsync(subscription.Id, cancellationToken).ConfigureAwait(false);

		PageRequest page = new() { Limit = 200 };
		List<DepotArtifact> candidateArtifacts = [];
		long total;
		do
		{
			(IReadOnlyList<DepotArtifact> items, total) = await _artifacts.ListAsync(
				new DepotArtifactFilter(subscription.Product, null, null), page, cancellationToken).ConfigureAwait(false);
			candidateArtifacts.AddRange(items);
			page.Offset += page.Limit;
		}
		while (candidateArtifacts.Count < total);

		SubscriptionEvaluationOutcome outcome = await _evaluation
			.EvaluateAsync(subscription, candidateArtifacts, priorState, cancellationToken).ConfigureAwait(false);

		await _state.UpsertAsync(
			new SubscriptionEvaluationState(subscription.Id, outcome.VersionCounter, DateTimeOffset.UtcNow, outcome.FetchSet.Count, outcome.ProjectedBytes),
			cancellationToken).ConfigureAwait(false);

		if (outcome.Skipped)
		{
			return JobExecutionOutcome.Succeeded(outcome.SkipReason ?? "Evaluation skipped: lib.json version counter unchanged.");
		}

		await _state.SetFetchSetAsync(subscription.Id, outcome.FetchSet, cancellationToken).ConfigureAwait(false);

		string bytesNote = outcome.ProjectedBytes is long bytes ? $", projected {bytes} byte(s)" : ", projected size unknown (one or more items have no catalog size)";
		return JobExecutionOutcome.Succeeded($"Evaluated subscription {subscription.Id}: {outcome.FetchSet.Count} fetch-set item(s){bytesNote}.");
	}

	private sealed record Payload(Guid? SubscriptionId);
}
