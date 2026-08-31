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
using Microsoft.Extensions.Logging;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;

namespace Waypoint.Infrastructure.Downloads;

/// <summary>
/// The <c>retention-sweep</c> <see cref="JobShape.Simple"/> job handler (issue #1436,
/// epic #1182), <c>waypoint_download_runner</c>-claimed (migration 0127). Two payload
/// shapes, mutually exclusive within one job:
///
/// <b>Scheduled sweep</b> (candidate entry pass + timed auto-prune, delegated to
/// <see cref="IRetentionSweepService.RunSweepAsync"/>):
/// <c>{"candidate_depot_artifact_ids": ["&lt;guid&gt;", ...], "listing_verified": true, "scope_key": "default"}</c>.
/// <c>listing_verified</c> is required and has no default -- an omitted or malformed
/// value fails the job rather than silently defaulting to either polarity, since
/// defaulting to <c>true</c> would defeat the partial-listing safety contract and
/// defaulting to <c>false</c> would silently no-op every unlabelled caller.
///
/// <b>Immediate purge</b> (skips the grace window for named rows, delegated to
/// <see cref="IRetentionSweepService.PurgeImmediatelyAsync"/> -- the trigger #1453's
/// purge-now endpoint enqueues): <c>{"purge_now_ids": ["&lt;retained-content-state-id&gt;", ...], "actor": "...", "reason": "..."}</c>.
/// A non-empty <c>purge_now_ids</c> takes this branch and every sweep field is
/// ignored -- one job execution does one thing.
/// </summary>
public sealed partial class RetentionSweepJobHandler : IJobHandler
{
	// snake_case, matching every other job payload in this codebase (e.g.
	// PurgeJobHandler's job_ids) -- the Web default (camelCase) would silently leave
	// these null.
	private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
	};

	private readonly IRetentionSweepService _sweep;
	private readonly ILogger<RetentionSweepJobHandler> _logger;

	public RetentionSweepJobHandler(IRetentionSweepService sweep, ILogger<RetentionSweepJobHandler> logger)
	{
		ArgumentNullException.ThrowIfNull(sweep);
		ArgumentNullException.ThrowIfNull(logger);

		_sweep = sweep;
		_logger = logger;
	}

	public string JobType => "retention-sweep";

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		RetentionSweepPayload? payload;
		try
		{
			payload = JsonSerializer.Deserialize<RetentionSweepPayload>(context.Job.Payload, PayloadOptions);
		}
		catch (JsonException exception)
		{
			return JobExecutionOutcome.Failed($"Malformed retention-sweep payload: {exception.Message}");
		}

		if (payload is null)
		{
			return JobExecutionOutcome.Failed("retention-sweep payload is required.");
		}

		if (payload.PurgeNowIds is { Count: > 0 })
		{
			return await ExecutePurgeNowAsync(payload, cancellationToken).ConfigureAwait(false);
		}

		return await ExecuteSweepAsync(context.Job.Id, payload, cancellationToken).ConfigureAwait(false);
	}

	private async Task<JobExecutionOutcome> ExecutePurgeNowAsync(RetentionSweepPayload payload, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(payload.Actor))
		{
			return JobExecutionOutcome.Failed("retention-sweep purge_now payload requires a non-empty 'actor'.");
		}

		int purged = 0;
		List<string> errors = [];

		foreach (string rawId in payload.PurgeNowIds!)
		{
			if (!Guid.TryParse(rawId, out Guid id))
			{
				errors.Add($"'{rawId}' is not a valid retained-content-state id.");
				continue;
			}

			RetentionPurgeOutcome outcome = await _sweep
				.PurgeImmediatelyAsync(id, payload.Actor, payload.Reason, cancellationToken).ConfigureAwait(false);
			if (outcome.Purged)
			{
				purged++;
			}
			else if (outcome.Error is not null)
			{
				errors.Add($"{id}: {outcome.Error}");
			}
		}

		if (purged == 0 && errors.Count > 0)
		{
			return JobExecutionOutcome.Failed(
				$"immediate purge failed for all {payload.PurgeNowIds!.Count} item(s): {string.Join("; ", errors)}");
		}

		return JobExecutionOutcome.Succeeded(
			errors.Count == 0
				? $"Purged {purged} item(s) immediately."
				: $"Purged {purged} of {payload.PurgeNowIds!.Count} item(s) immediately; errors: {string.Join("; ", errors)}");
	}

	private async Task<JobExecutionOutcome> ExecuteSweepAsync(Guid jobId, RetentionSweepPayload payload, CancellationToken cancellationToken)
	{
		if (payload.ListingVerified is null)
		{
			return JobExecutionOutcome.Failed("retention-sweep payload requires an explicit 'listing_verified' boolean.");
		}

		List<Guid> candidates = [];
		List<string> parseErrors = [];
		foreach (string rawId in payload.CandidateDepotArtifactIds ?? [])
		{
			if (Guid.TryParse(rawId, out Guid parsed))
			{
				candidates.Add(parsed);
			}
			else
			{
				parseErrors.Add($"'{rawId}' is not a valid depot artifact id.");
			}
		}

		RetentionSweepReport report = await _sweep.RunSweepAsync(
			new RetentionSweepRequest(candidates, payload.ListingVerified.Value, payload.ScopeKey), cancellationToken).ConfigureAwait(false);

		if (report.Skipped)
		{
			LogSkipped(_logger, jobId, report.SkippedReason ?? "unknown");
			return JobExecutionOutcome.Succeeded(report.SkippedReason);
		}

		List<string> errors = [.. parseErrors, .. report.Errors];
		string summary =
			$"retention sweep entered {report.EnteredGrace} into grace, auto-pruned {report.AutoPruned}" +
			(report.UntrackedCandidatesSkipped > 0 ? $", skipped {report.UntrackedCandidatesSkipped} untracked candidate(s)" : string.Empty) +
			".";

		if (errors.Count > 0)
		{
			return JobExecutionOutcome.Failed($"{summary} {errors.Count} error(s): {string.Join("; ", errors)}");
		}

		return JobExecutionOutcome.Succeeded(summary);
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "retention-sweep job {JobId} skipped: {Reason}")]
	private static partial void LogSkipped(ILogger logger, Guid jobId, string reason);

	private sealed record RetentionSweepPayload(
		List<string>? CandidateDepotArtifactIds,
		bool? ListingVerified,
		string? ScopeKey,
		List<string>? PurgeNowIds,
		string? Actor,
		string? Reason);
}
