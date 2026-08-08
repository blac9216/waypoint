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

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Core.Jobs;
using Waypoint.Tests.Support;

namespace Waypoint.Tests.Api;

/// <summary>
/// Controller tests for the /runs endpoints: role guards, happy path, 404s, and
/// state-transition validation. Uses <see cref="RunsTestApiFactory"/> to inject a
/// controllable <see cref="FakeJobQueueRepository"/> through the test host.
/// </summary>
public sealed class RunsEndpointTests : IClassFixture<RunsTestApiFactory>
{
	private readonly RunsTestApiFactory _factory;

	public RunsEndpointTests(RunsTestApiFactory factory)
	{
		_factory = factory;
	}

	// -- role guard tests ---------------------------------------------------

	[Theory]
	[InlineData("Viewer")]
	public async Task CreateRun_WithRoleBelowCyber_Returns403(string role)
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/runs");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		request.Content = new StringContent(
			System.Text.Json.JsonSerializer.Serialize(new { run_type = "scan", scope = "{}" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task CreateRun_WithCyberRole_Returns202()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/runs");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Cyber");
		request.Content = new StringContent(
			System.Text.Json.JsonSerializer.Serialize(new { run_type = "scan", scope = "{}" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
	}

	// -- issue #208: remediation gate + initiator provenance ----------------

	[Theory]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	public async Task CreateRun_RemediateBelowAdmin_Returns403(string role)
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/runs");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { run_type = "remediate", scope = "{}", confirmation = "REMEDIATE" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("remediate")]
	[InlineData("yes")]
	public async Task CreateRun_RemediateWithoutExactConfirmation_Returns400(string? confirmation)
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/runs");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { run_type = "remediate", scope = "{}", confirmation }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task CreateRun_RemediateWithAdminAndConfirmation_Returns202()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/runs");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { run_type = "remediate", scope = "{}", confirmation = "REMEDIATE" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.Equal("remediate", _factory.Repository.LastCreateRun!.Value.RunType);
	}

	[Fact]
	public async Task CreateRun_InitiatedByComesFromIdentity_NotFromBody()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/runs");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Cyber");
		// initiated_by is no longer part of the contract; a caller supplying it
		// anyway must not influence the recorded initiator.
		request.Content = new StringContent(
			JsonSerializer.Serialize(new { run_type = "scan", scope = "{}", initiated_by = "mallory" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.Equal("test-user", _factory.Repository.LastCreateRun!.Value.InitiatedBy);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	[InlineData("Admin")]
	public async Task GetRun_WithAnyAuthenticatedRole_ReturnsOk(string role)
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null));
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{_factory.RunId}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	[InlineData("Admin")]
	public async Task GetJobs_WithAnyAuthenticatedRole_ReturnsOk(string role)
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null));
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{_factory.RunId}/jobs");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	public async Task PauseRun_WithRoleBelowOperator_Returns403(string role)
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/pause");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task PauseRun_WithOperatorRole_ReturnsOk()
	{
		// TestAuthHandler names every principal "test-user"; this run must be owned
		// by the caller for a non-Admin Operator to act on it (issue #209).
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null, InitiatedBy: "test-user"));
		_factory.Repository.SetPauseResult(true);
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/pause");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	public async Task ResumeRun_WithRoleBelowOperator_Returns403(string role)
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/resume");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task ResumeRun_WithOperatorRole_ReturnsOk()
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", true, false, null, InitiatedBy: "test-user"));
		_factory.Repository.SetResumeResult(true);
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/resume");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Theory]
	[InlineData("Viewer")]
	[InlineData("Cyber")]
	public async Task AbortRun_WithRoleBelowOperator_Returns403(string role)
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/abort");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task AbortRun_WithOperatorRole_ReturnsOk()
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null, InitiatedBy: "test-user"));
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/abort");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	// -- issue #209: run-action ownership ------------------------------------
	// docs/api-contract.md: "/runs/{id}/pause · /resume · /abort ... Operator+ (own
	// runs), Admin any." TestAuthHandler names every principal "test-user".

	[Theory]
	[InlineData("pause")]
	[InlineData("resume")]
	[InlineData("abort")]
	public async Task RunAction_OwnerOperator_ReturnsOk(string action)
	{
		// Paused must match the pre-condition each action needs to succeed: resume
		// requires an already-paused run, pause/abort require an unpaused one.
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", action == "resume", false, null, InitiatedBy: "test-user"));
		_factory.Repository.SetPauseResult(true);
		_factory.Repository.SetResumeResult(true);
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/{action}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Theory]
	[InlineData("pause")]
	[InlineData("resume")]
	[InlineData("abort")]
	public async Task RunAction_NonOwnerOperator_Returns403(string action)
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", action == "resume", false, null, InitiatedBy: "another-user"));
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/{action}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "forbidden");
	}

	[Theory]
	[InlineData("pause")]
	[InlineData("resume")]
	[InlineData("abort")]
	public async Task RunAction_Admin_SucceedsOnNonOwnedRun(string action)
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", action == "resume", false, null, InitiatedBy: "another-user"));
		_factory.Repository.SetPauseResult(true);
		_factory.Repository.SetResumeResult(true);
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/{action}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Theory]
	[InlineData("pause")]
	[InlineData("resume")]
	[InlineData("abort")]
	public async Task RunAction_OperatorOnOwnerlessRun_Returns403(string action)
	{
		// InitiatedBy null simulates a system/scheduled run with no recorded initiator.
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", action == "resume", false, null, InitiatedBy: null));
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/{action}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "forbidden");
	}

	[Theory]
	[InlineData("pause")]
	[InlineData("resume")]
	[InlineData("abort")]
	public async Task RunAction_AdminOnOwnerlessRun_ReturnsOk(string action)
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", action == "resume", false, null, InitiatedBy: null));
		_factory.Repository.SetPauseResult(true);
		_factory.Repository.SetResumeResult(true);
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/{action}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	// -- 404 tests ----------------------------------------------------------

	[Fact]
	public async Task GetRun_NonexistentRun_Returns404()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{Guid.NewGuid()}");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_found");
	}

	[Fact]
	public async Task GetJobs_NonexistentRun_Returns404()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/runs/{Guid.NewGuid()}/jobs");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_found");
	}

	[Fact]
	public async Task PauseRun_NonexistentRun_Returns404()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{Guid.NewGuid()}/pause");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_found");
	}

	[Fact]
	public async Task ResumeRun_NonexistentRun_Returns404()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{Guid.NewGuid()}/resume");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_found");
	}

	[Fact]
	public async Task AbortRun_NonexistentRun_Returns404()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{Guid.NewGuid()}/abort");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "not_found");
	}

	// -- state-transition validation ----------------------------------------

	[Fact]
	public async Task PauseRun_AlreadyPaused_Returns400()
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("pending", true, false, null, InitiatedBy: "test-user"));
		_factory.Repository.SetPauseResult(false);
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/pause");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Fact]
	public async Task ResumeRun_NotPaused_Returns400()
	{
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null, InitiatedBy: "test-user"));
		_factory.Repository.SetResumeResult(false);
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/resume");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	// -- response shape tests -----------------------------------------------

	[Fact]
	public async Task PauseRun_ReturnsPostActionState()
	{
		// Pre-action state is "running"/not-paused; after pause the fake updates is_paused.
		_factory.Repository.SetRun(_factory.RunId, new RunQueueState("running", false, false, null, InitiatedBy: "test-user"));
		_factory.Repository.SetPauseResult(true);
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, $"/api/v1/runs/{_factory.RunId}/pause");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Operator");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		// The controller re-fetches state after the action; the fake updates is_paused=true.
		Assert.Equal("running", doc.RootElement.GetProperty("state").GetString());
	}

	[Fact]
	public async Task CreateRun_ReturnsRunId()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/runs");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Cyber");
		request.Content = new StringContent(
			System.Text.Json.JsonSerializer.Serialize(new { run_type = "scan", scope = "{}" }),
			System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		Assert.NotNull(doc.RootElement.GetProperty("run_id").GetString());
	}
}

/// <summary>
/// Test host that injects a <see cref="FakeJobQueueRepository"/> so /runs endpoints
/// can be exercised without Postgres. Inherits from <see cref="WaypointApiFactory"/>
/// and swaps the auth scheme for <see cref="TestAuthHandler"/> so role injection
/// via <c>X-Test-Role</c> header works (same pattern as <see cref="RoleGuardedApiFactory"/>).
/// </summary>
public sealed class RunsTestApiFactory : WaypointApiFactory
{
	public Guid RunId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000001");
	public FakeJobQueueRepository Repository { get; } = new();

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		base.ConfigureWebHost(builder);

		builder.ConfigureTestServices(services =>
		{
			// Swap auth scheme for test role injection (same as RoleGuardedApiFactory).
			services
				.AddAuthentication(TestAuthHandler.SchemeName)
				.AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

			services.PostConfigure<AuthenticationOptions>(options =>
			{
				options.DefaultScheme = TestAuthHandler.SchemeName;
				options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
				options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
				options.DefaultForbidScheme = TestAuthHandler.SchemeName;
			});

			// Replace the default (no-op or Postgres) IJobQueueRepository with our fake.
			var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IJobQueueRepository));
			if (descriptor != null)
			{
				services.Remove(descriptor);
			}
			services.AddSingleton<IJobQueueRepository>(Repository);
		});
	}
}

/// <summary>
/// Minimal fake IJobQueueRepository for RunsController tests. Tracks state per run
/// GUID so that nonexistent run IDs return null (404). Only the methods exercised by
/// the controller are implemented; the rest return no-op defaults.
/// </summary>
public sealed class FakeJobQueueRepository : IJobQueueRepository
{
	private readonly Dictionary<Guid, RunQueueState> _runs = new();
	private bool _pauseResult = true;
	private bool _resumeResult = true;

	public void SetRun(Guid runId, RunQueueState state)
	{
		_runs[runId] = state;
	}

	public void SetPauseResult(bool result) => _pauseResult = result;
	public void SetResumeResult(bool result) => _resumeResult = result;

	public Task<RunQueueState?> GetRunQueueStateAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		return Task.FromResult(_runs.TryGetValue(runId, out var state) ? state : null);
	}

	public Task<RunSummary?> GetRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		if (!_runs.TryGetValue(runId, out var state))
		{
			return Task.FromResult<RunSummary?>(null);
		}
		return Task.FromResult<RunSummary?>(new RunSummary(
			Id: runId, RunType: "scan", State: state.State, Paused: state.Paused,
			Blocked: state.Blocked, BlockedReason: state.BlockedReason,
			ScopeJson: "{}", CredentialId: null, InitiatedBy: "test-user",
			CreatedAt: "2026-01-01T00:00:00Z", StartedAt: null, CompletedAt: null,
			JobCount: 0, JobCountQueued: 0, JobCountRunning: 0,
			JobCountCompleted: 0, JobCountFailed: 0, JobCountBlocked: 0));
	}

	public Task<IReadOnlyList<JobSummary>> GetJobsForRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = (runId, cancellationToken);
		return Task.FromResult<IReadOnlyList<JobSummary>>([]);
	}

	/// <summary>Arguments of the last <see cref="CreateRunAsync"/> call, for asserting
	/// what the controller actually forwarded (issue #208: initiated_by provenance).</summary>
	public (string RunType, string ScopeJson, Guid? CredentialId, string? InitiatedBy)? LastCreateRun { get; private set; }

	public Task<Guid> CreateRunAsync(string runType, string scopeJson, Guid? credentialId, string? initiatedBy, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		LastCreateRun = (runType, scopeJson, credentialId, initiatedBy);
		return Task.FromResult(Guid.NewGuid());
	}

	public Task<IReadOnlyList<Guid>> FanOutJobsAsync(Guid runId, IReadOnlyList<JobSpec> specs, string? createdBy, CancellationToken cancellationToken)
	{
		_ = (runId, specs, createdBy, cancellationToken);
		return Task.FromResult<IReadOnlyList<Guid>>([]);
	}

	public Task<bool> PauseRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		if (_pauseResult && _runs.TryGetValue(runId, out var state))
		{
			// Simulate the state transition: after a successful pause, is_paused becomes true.
			_runs[runId] = new RunQueueState(state.State, true, state.Blocked, state.BlockedReason, state.InitiatedBy);
		}
		return Task.FromResult(_pauseResult);
	}

	public Task<bool> ResumeRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		if (_resumeResult && _runs.TryGetValue(runId, out var state))
		{
			// Simulate the state transition: after a successful resume, is_paused becomes false.
			_runs[runId] = new RunQueueState(state.State, false, state.Blocked, state.BlockedReason, state.InitiatedBy);
		}
		return Task.FromResult(_resumeResult);
	}

	public Task<AbortRunResult> AbortRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = (runId, cancellationToken);
		return Task.FromResult(new AbortRunResult([], []));
	}

	public Task<ClaimedJob?> ClaimJobAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
	{
		_ = (workerId, leaseDuration, cancellationToken);
		return Task.FromResult<ClaimedJob?>(null);
	}

	public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
	{
		_ = (jobId, workerId, leaseDuration, cancellationToken);
		return Task.FromResult(true);
	}

	public Task<bool> AdvanceStateAsync(Guid jobId, string workerId, string expectedFromState, string toState, string? note, bool clearLease, CancellationToken cancellationToken)
	{
		_ = (jobId, workerId, expectedFromState, toState, note, clearLease, cancellationToken);
		return Task.FromResult(true);
	}

	public Task<IReadOnlyList<RecoveredJob>> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken)
	{
		_ = (batchSize, cancellationToken);
		return Task.FromResult<IReadOnlyList<RecoveredJob>>([]);
	}

	public Task<AuthFailureHaltResult> CheckConsecutiveAuthFailuresAsync(Guid credentialId, int threshold, CancellationToken cancellationToken)
	{
		_ = (credentialId, threshold, cancellationToken);
		return Task.FromResult(new AuthFailureHaltResult(HaltTripped: false, [], []));
	}

	public Task<bool> ReleaseClaimAsync(Guid jobId, string workerId, CancellationToken cancellationToken)
	{
		_ = (jobId, workerId, cancellationToken);
		return Task.FromResult(true);
	}

	public Task<CredentialUnblockResult> UnblockCredentialAsync(Guid credentialId, string? reason, CancellationToken cancellationToken)
	{
		_ = (credentialId, reason, cancellationToken);
		return Task.FromResult(new CredentialUnblockResult(WasHalted: false, [], []));
	}
}
