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
using Waypoint.Core.Jobs;
using Waypoint.Core.Subscriptions;

namespace Waypoint.Infrastructure.Subscriptions;

/// <summary>
/// The one-run/one-job-per-item fan-out step of the subscription-evaluation job
/// (issue #1472 AC3), split out of <c>SubscriptionEvaluationJobHandler</c> for the
/// process-boundary reason the new migration's header states: ADR-0013 keeps run
/// creation/fan-out (<see cref="IJobControlRepository.CreateRunAsync"/>/
/// <see cref="IJobControlRepository.FanOutJobsAsync"/>) on the ASP.NET control plane,
/// and no runner role has ever been granted INSERT on <c>runs</c> -- so the
/// download-runner-executed evaluation job PERSISTS its fetch set
/// (<see cref="ISubscriptionEvaluationStateRepository.SetFetchSetAsync"/>) and this
/// service, run under the API's owner-privileged connection, reads it back and does
/// the actual fan-out -- exactly the same shape as <c>QueueBinariesDownload</c>'s own
/// one-run-then-<see cref="IJobControlRepository.FanOutJobsAsync"/> call, reusing the
/// existing <c>binaries-download</c> job type rather than inventing a new one (issue
/// #1472's Proposed Changes: "fan out one per-item sync job per stale line via the
/// existing run-&gt;job fan-out primitive"). Wiring an automatic caller (a schedule
/// dispatcher tick, or a catalog-pull completion hook) is out of this issue's scope --
/// see the migration's header and this issue's own "NOT added to
/// ScheduleJobTypes.All in this child" note; <c>FanOutAsync</c> is the primitive a
/// future caller invokes.
/// </summary>
public sealed class SubscriptionEvaluationFanOutService
{
	private const short EvaluationFanOutPriority = 6;

	private readonly ISubscriptionEvaluationStateRepository _state;
	private readonly IJobControlRepository _jobs;

	public SubscriptionEvaluationFanOutService(ISubscriptionEvaluationStateRepository state, IJobControlRepository jobs)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(jobs);
		_state = state;
		_jobs = jobs;
	}

	/// <summary>
	/// Fans out the subscription's currently-pending fetch set onto one new
	/// <see cref="RunTypes.BinariesDownload"/> run, one job per item, and marks the
	/// fetch set consumed. Returns <c>null</c> (a no-op) when there is nothing pending
	/// -- no evaluation has run, the last evaluation's fetch set was empty, or it was
	/// already fanned out. A fetch-set item missing a catalog <c>bundle_id</c> (issue
	/// #1783: nothing usable to pass as the real tool's <c>--id</c>) is skipped rather
	/// than failing the whole batch; the run still contains every other item.
	/// </summary>
	public async Task<Guid?> FanOutAsync(Guid subscriptionId, string actor, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);

		IReadOnlyList<SubscriptionFetchItem>? fetchSet = await _state.GetPendingFetchSetAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
		if (fetchSet is null)
		{
			return null;
		}

		List<JobSpec> specs = [];
		foreach (SubscriptionFetchItem item in fetchSet)
		{
			if (string.IsNullOrWhiteSpace(item.BundleId))
			{
				continue;
			}

			string payload = JsonSerializer.Serialize(new
			{
				depot_artifact_id = item.DepotArtifactId,
				external_id = item.ExternalId,
				bundle_id = item.BundleId,
			});
			specs.Add(new JobSpec(RunTypes.BinariesDownload, EvaluationFanOutPriority, TargetId: item.DepotArtifactId, TargetName: item.ExternalId, Payload: payload));
		}

		if (specs.Count == 0)
		{
			await _state.MarkFannedOutAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
			return null;
		}

		Guid runId = await _jobs.CreateRunAsync(RunTypes.BinariesDownload, "{}", credentialId: null, actor, cancellationToken).ConfigureAwait(false);
		await _jobs.FanOutJobsAsync(runId, specs, actor, cancellationToken).ConfigureAwait(false);
		await _state.MarkFannedOutAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
		return runId;
	}
}
