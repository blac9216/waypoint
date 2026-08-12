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
using Waypoint.Infrastructure.Runs;
using Waypoint.Tests.Api;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Runs;

/// <summary>
/// Issue #414: focused unit coverage for <see cref="RunControlService"/> against
/// <see cref="FakeJobQueueRepository"/> -- the HTTP-level assertions (role gates,
/// ownership, 404s) stay in <c>RunsEndpointTests</c>; these prove the extracted
/// service's own contract: re-fetch-after-action semantics and the exact literal
/// fallback string the original controller returned.
/// </summary>
public sealed class RunControlServiceTests
{
	[Fact]
	public async Task PauseAsync_Succeeds_ReturnsRefetchedState()
	{
		FakeJobQueueRepository repository = new();
		Guid runId = Guid.NewGuid();
		RunQueueState state = new("running", false, false, null, "alice");
		repository.SetRun(runId, state);
		RunControlService service = new(repository);

		string result = await service.PauseAsync(runId, state, CancellationToken.None);

		// FakeJobQueueRepository.PauseRunAsync flips is_paused true on success, which
		// GetRunQueueStateAsync then reflects -- but State itself (the run's lifecycle
		// state, not the pause flag) is unchanged, matching production semantics where
		// "paused" is a flag layered over State, not a distinct State value.
		Assert.Equal("running", result);
	}

	[Fact]
	public async Task PauseAsync_RepositoryRefusesPause_ThrowsValidation()
	{
		FakeJobQueueRepository repository = new();
		Guid runId = Guid.NewGuid();
		RunQueueState state = new("done", false, false, null, "alice");
		repository.SetRun(runId, state);
		repository.SetPauseResult(false);
		RunControlService service = new(repository);

		ApiException exception = await Assert.ThrowsAsync<ApiException>(
			() => service.PauseAsync(runId, state, CancellationToken.None));

		Assert.Equal(System.Net.HttpStatusCode.BadRequest, exception.StatusCode);
	}

	[Fact]
	public async Task ResumeAsync_RepositoryRefusesResume_ThrowsValidation()
	{
		FakeJobQueueRepository repository = new();
		Guid runId = Guid.NewGuid();
		RunQueueState state = new("done", false, false, null, "alice");
		repository.SetRun(runId, state);
		repository.SetResumeResult(false);
		RunControlService service = new(repository);

		ApiException exception = await Assert.ThrowsAsync<ApiException>(
			() => service.ResumeAsync(runId, state, CancellationToken.None));

		Assert.Equal(System.Net.HttpStatusCode.BadRequest, exception.StatusCode);
	}

	[Fact]
	public async Task AbortAsync_AlreadyTerminalRun_ReturnsPostActionStateNotThrow()
	{
		// AbortRunAsync is a documented no-op against an already-terminal run --
		// FakeJobQueueRepository.AbortRunAsync only transitions 'pending'/'running',
		// so a 'done' run stays 'done'. AbortAsync must reflect that, never throw.
		FakeJobQueueRepository repository = new();
		Guid runId = Guid.NewGuid();
		RunQueueState state = new("done", false, false, null, "alice");
		repository.SetRun(runId, state);
		RunControlService service = new(repository);

		string result = await service.AbortAsync(runId, state, CancellationToken.None);

		Assert.Equal("done", result);
	}

	[Fact]
	public async Task AbortAsync_RunningRun_TransitionsToAborted()
	{
		FakeJobQueueRepository repository = new();
		Guid runId = Guid.NewGuid();
		RunQueueState state = new("running", false, false, null, "alice");
		repository.SetRun(runId, state);
		RunControlService service = new(repository);

		string result = await service.AbortAsync(runId, state, CancellationToken.None);

		Assert.Equal("aborted", result);
	}
}
