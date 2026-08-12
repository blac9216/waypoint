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

using Waypoint.Core.Errors;
using Waypoint.Core.Jobs;

namespace Waypoint.Infrastructure.Runs;

/// <summary>
/// Issue #414: control-plane pause/resume/abort/resume-blocked orchestration for a run,
/// extracted out of <see cref="Waypoint.Api.Controllers.RunsController"/>. Ownership
/// enforcement (<c>EnforceRunOwnership</c>) stays in the controller -- it reads
/// <see cref="System.Security.Claims.ClaimsPrincipal"/>, an HTTP-layer concern, and the
/// controller must resolve <see cref="RunQueueState"/> before calling here regardless
/// (to run that check) -- so each method here accepts the already-resolved
/// <see cref="RunQueueState"/> rather than re-fetching it up front, but still re-fetches
/// AFTER the action to return the true post-action state, matching the controller's
/// original behavior 1:1 (including the exact literal-string fallback the controller
/// returned if the post-action re-fetch somehow came back null). Every operation goes
/// through <see cref="IJobControlRepository"/>'s control surface -- no claim/lease/
/// execution responsibility.
/// </summary>
public sealed class RunControlService
{
	private readonly IJobControlRepository _repository;

	public RunControlService(IJobControlRepository repository)
	{
		ArgumentNullException.ThrowIfNull(repository);
		_repository = repository;
	}

	/// <summary>Pauses dispatch for a run whose ownership the caller has already
	/// verified against <paramref name="state"/>. Throws <see cref="ApiException"/>
	/// (400) if the run's current state does not allow a pause.</summary>
	public async Task<string> PauseAsync(Guid runId, RunQueueState state, CancellationToken cancellationToken)
	{
		bool paused = await _repository.PauseRunAsync(runId, cancellationToken).ConfigureAwait(false);
		if (!paused)
		{
			throw ApiException.Validation("Run cannot be paused.", $"Run '{runId}' is in state '{state.State}'.");
		}

		return await RefetchStateOrFallbackAsync(runId, "paused", cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Resumes dispatch for a paused run whose ownership the caller has
	/// already verified against <paramref name="state"/>. Throws
	/// <see cref="ApiException"/> (400) if the run's current state does not allow a
	/// resume.</summary>
	public async Task<string> ResumeAsync(Guid runId, RunQueueState state, CancellationToken cancellationToken)
	{
		bool resumed = await _repository.ResumeRunAsync(runId, cancellationToken).ConfigureAwait(false);
		if (!resumed)
		{
			throw ApiException.Validation("Run cannot be resumed.", $"Run '{runId}' is in state '{state.State}'.");
		}

		return await RefetchStateOrFallbackAsync(runId, "running", cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Aborts a run whose ownership the caller has already verified against
	/// <paramref name="state"/>. A no-op against an already-terminal run --
	/// the returned state reflects that rather than throwing.</summary>
	public async Task<string> AbortAsync(Guid runId, RunQueueState state, CancellationToken cancellationToken)
	{
		_ = state;
		AbortRunResult result = await _repository.AbortRunAsync(runId, cancellationToken).ConfigureAwait(false);
		_ = result; // AbortRunResult carries cancelled/in-flight job IDs for future enrichment

		return await RefetchStateOrFallbackAsync(runId, "aborted", cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Re-fetches state after the action to return the post-action state (the run's
	/// state string may not have literally changed, e.g. abort against an
	/// already-terminal run) -- falls back to <paramref name="fallback"/> only if the
	/// re-fetch itself comes back null, matching the controller's original
	/// <c>newState?.State ?? "paused"/"running"/"aborted"</c> literal fallback exactly.
	/// </summary>
	private async Task<string> RefetchStateOrFallbackAsync(Guid runId, string fallback, CancellationToken cancellationToken)
	{
		RunQueueState? newState = await _repository.GetRunQueueStateAsync(runId, cancellationToken).ConfigureAwait(false);
		return newState?.State ?? fallback;
	}
}
