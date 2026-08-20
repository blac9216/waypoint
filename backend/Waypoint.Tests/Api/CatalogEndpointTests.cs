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
using Waypoint.Core.Catalog;
using Waypoint.Core.Jobs;
using Waypoint.Core.Pagination;
using Waypoint.Tests.Support;

namespace Waypoint.Tests.Api;

/// <summary>
/// Controller tests for the /catalog endpoints (issue #193, epic #9 slice 1): role
/// guards, filters/pagination on the list, and that sync creates a run + one
/// catalog-index job through the engine. Exercised against fakes, not Postgres --
/// the real store + idempotent upsert is proven separately against real Postgres in
/// <c>DepotArtifactRepositoryTests</c> and the end-to-end wiring in
/// <c>CatalogApiTests</c>.
/// </summary>
public sealed class CatalogEndpointTests : IClassFixture<CatalogTestApiFactory>
{
	private readonly CatalogTestApiFactory _factory;

	public CatalogEndpointTests(CatalogTestApiFactory factory)
	{
		_factory = factory;
	}

	[Fact]
	public async Task ListArtifacts_WithoutAuth_Returns401()
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await client.GetAsync("/api/v1/catalog/artifacts");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task ListArtifacts_WithViewerRole_Returns200AndTotalCountHeader()
	{
		_factory.Artifacts.Reset(
			new DepotArtifact(Guid.NewGuid(), "artifact-1", "abc123", "indexed", "VCF", "9.0", "{}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
			new DepotArtifact(Guid.NewGuid(), "artifact-2", "def456", "indexed", "VCF", "9.1", "{}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/catalog/artifacts");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.True(response.Headers.TryGetValues("X-Total-Count", out IEnumerable<string>? values));
		Assert.Equal("2", values!.Single());

		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		Assert.Equal(2, doc.RootElement.GetArrayLength());
	}

	[Fact]
	public async Task ListArtifacts_ForwardsProductVersionStatusFilters()
	{
		_factory.Artifacts.Reset();

		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/catalog/artifacts?product=VCF&version=9.0&status=indexed&limit=10&offset=5");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		await client.SendAsync(request);

		Assert.NotNull(_factory.Artifacts.LastFilter);
		Assert.Equal("VCF", _factory.Artifacts.LastFilter!.Product);
		Assert.Equal("9.0", _factory.Artifacts.LastFilter!.Version);
		Assert.Equal("indexed", _factory.Artifacts.LastFilter!.Status);
		Assert.NotNull(_factory.Artifacts.LastPage);
		Assert.Equal(10, _factory.Artifacts.LastPage!.Limit);
		Assert.Equal(5, _factory.Artifacts.LastPage!.Offset);
	}

	[Fact]
	public async Task Sync_WithViewerRole_Returns403()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/catalog/sync");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Viewer");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Theory]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	public async Task Sync_BelowAdmin_Returns403(string role)
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/catalog/sync");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Fact]
	public async Task Sync_WithAdminRole_Returns202AndCreatesOneCatalogIndexJob()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/catalog/sync");
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Admin");

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		Assert.NotNull(doc.RootElement.GetProperty("run_id").GetString());

		Assert.NotNull(_factory.Jobs.LastCreateRun);
		Assert.Equal("catalog-index", _factory.Jobs.LastCreateRun!.Value.RunType);

		Assert.NotNull(_factory.Jobs.LastFanOut);
		Assert.Single(_factory.Jobs.LastFanOut!);
		Assert.Equal("catalog-index", _factory.Jobs.LastFanOut![0].JobType);
	}
}

/// <summary>Test host wiring fakes for both catalog dependencies behind role-header auth.</summary>
public sealed class CatalogTestApiFactory : WaypointApiFactory
{
	public FakeDepotArtifactRepository Artifacts { get; } = new();
	public CatalogFakeJobQueueRepository Jobs { get; } = new();

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		base.ConfigureWebHost(builder);

		builder.ConfigureTestServices(services =>
		{
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

			ReplaceSingleton<IDepotArtifactRepository>(services, Artifacts);
			// Issue #415 split IJobQueueRepository into IJobControlRepository and
			// IJobRunnerRepository -- CatalogController only depends on the former, but
			// both registrations are swapped so any other controller resolved through
			// this host also sees the fake.
			ReplaceSingleton<IJobControlRepository>(services, Jobs);
			ReplaceSingleton<IJobRunnerRepository>(services, Jobs);
		});
	}

	private static void ReplaceSingleton<TService>(IServiceCollection services, TService instance)
		where TService : class
	{
		ServiceDescriptor? descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(TService));
		if (descriptor is not null)
		{
			services.Remove(descriptor);
		}

		services.AddSingleton(instance);
	}
}

/// <summary>Minimal in-memory fake for controller-level tests; captures the filter/page it was called with.</summary>
public sealed class FakeDepotArtifactRepository : IDepotArtifactRepository
{
	private DepotArtifact[] _items = [];

	public DepotArtifactFilter? LastFilter { get; private set; }
	public PageRequest? LastPage { get; private set; }

	public void Reset(params DepotArtifact[] items) => _items = items;

	public Task<Guid> UpsertAsync(DepotArtifactUpsert artifact, CancellationToken cancellationToken)
	{
		_ = (artifact, cancellationToken);
		return Task.FromResult(Guid.NewGuid());
	}

	public Task<(IReadOnlyList<DepotArtifact> Items, long TotalCount)> ListAsync(
		DepotArtifactFilter filter, PageRequest page, CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		LastFilter = filter;
		LastPage = page;
		return Task.FromResult(((IReadOnlyList<DepotArtifact>)_items, (long)_items.Length));
	}
}

/// <summary>
/// Minimal job repository fake for /catalog/sync tests -- only
/// <see cref="CreateRunAsync"/> and <see cref="FanOutJobsAsync"/> are exercised by
/// <c>CatalogController.Sync</c>; every other member is unused scaffolding. Implements
/// both <see cref="IJobControlRepository"/> and <see cref="IJobRunnerRepository"/>
/// (issue #415's split of the former combined <c>IJobQueueRepository</c>) since this
/// test host swaps both registrations.
/// </summary>
public sealed class CatalogFakeJobQueueRepository : IJobControlRepository, IJobRunnerRepository
{
	public (string RunType, string ScopeJson, Guid? CredentialId, string? InitiatedBy)? LastCreateRun { get; private set; }
	public IReadOnlyList<JobSpec>? LastFanOut { get; private set; }

	public Task<Guid> CreateRunAsync(string runType, string scopeJson, Guid? credentialId, string? initiatedBy, CancellationToken cancellationToken, Guid? scheduleId = null)
	{
		_ = (cancellationToken, scheduleId);
		LastCreateRun = (runType, scopeJson, credentialId, initiatedBy);
		return Task.FromResult(Guid.NewGuid());
	}

	public Task<IReadOnlyList<Guid>> FanOutJobsAsync(Guid runId, IReadOnlyList<JobSpec> specs, string? createdBy, CancellationToken cancellationToken)
	{
		_ = (runId, createdBy, cancellationToken);
		LastFanOut = specs;
		return Task.FromResult<IReadOnlyList<Guid>>(specs.Select(_ => Guid.NewGuid()).ToArray());
	}

	public Task<ClaimedJob?> ClaimJobAsync(string workerId, TimeSpan leaseDuration, IReadOnlySet<string> allowedJobTypes, CancellationToken cancellationToken)
	{
		_ = (workerId, leaseDuration, allowedJobTypes, cancellationToken);
		return Task.FromResult<ClaimedJob?>(null);
	}

	public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
	{
		_ = (jobId, workerId, leaseDuration, cancellationToken);
		return Task.FromResult(true);
	}

	public Task<bool> IsCancelRequestedAsync(Guid jobId, CancellationToken cancellationToken)
	{
		_ = (jobId, cancellationToken);
		return Task.FromResult(false);
	}

	public Task<bool> AdvanceStateAsync(Guid jobId, string workerId, string expectedFromState, string toState, string? note, bool clearLease, CancellationToken cancellationToken)
	{
		_ = (jobId, workerId, expectedFromState, toState, note, clearLease, cancellationToken);
		return Task.FromResult(true);
	}

	public Task<bool> RequeueAtStageAsync(Guid jobId, string workerId, string expectedFromState, string stage, string? note, CancellationToken cancellationToken)
	{
		_ = (jobId, workerId, expectedFromState, stage, note, cancellationToken);
		return Task.FromResult(true);
	}

	public Task<IReadOnlyList<RecoveredJob>> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken)
	{
		_ = (batchSize, cancellationToken);
		return Task.FromResult<IReadOnlyList<RecoveredJob>>([]);
	}

	public Task<RunQueueState?> GetRunQueueStateAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = (runId, cancellationToken);
		return Task.FromResult<RunQueueState?>(null);
	}

	public Task<RunSummary?> GetRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = (runId, cancellationToken);
		return Task.FromResult<RunSummary?>(null);
	}

	public Task<IReadOnlyList<JobSummary>> GetJobsForRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = (runId, cancellationToken);
		return Task.FromResult<IReadOnlyList<JobSummary>>([]);
	}

	public Task<JobSummary?> GetJobAsync(Guid jobId, CancellationToken cancellationToken)
	{
		_ = (jobId, cancellationToken);
		return Task.FromResult<JobSummary?>(null);
	}

	public Task<bool> PauseRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = (runId, cancellationToken);
		return Task.FromResult(true);
	}

	public Task<bool> ResumeRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = (runId, cancellationToken);
		return Task.FromResult(true);
	}

	public Task<AbortRunResult> AbortRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		_ = (runId, cancellationToken);
		return Task.FromResult(new AbortRunResult([], []));
	}

	public Task<JobRetryOutcome> RetryJobAsync(Guid jobId, string actor, CancellationToken cancellationToken)
	{
		_ = (jobId, actor, cancellationToken);
		return Task.FromResult(JobRetryOutcome.Retried);
	}

	public Task<JobCancelOutcome> CancelJobAsync(Guid jobId, CancellationToken cancellationToken)
	{
		_ = (jobId, cancellationToken);
		return Task.FromResult(JobCancelOutcome.Cancelled);
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

	public Task<CredentialSwapResult> SwapAndResumeBlockedCredentialAsync(
		Guid runId, Guid replacementCredentialId, string actor, string? reason, CancellationToken cancellationToken)
	{
		_ = (runId, replacementCredentialId, actor, reason, cancellationToken);
		return Task.FromResult(new CredentialSwapResult(CredentialSwapOutcome.RunNotHalted, null, null, []));
	}

	public Task<RunListResult> ListRunsAsync(int limit, int offset, CancellationToken cancellationToken)
	{
		_ = (limit, offset, cancellationToken);
		return Task.FromResult(new RunListResult([], 0));
	}

	public Task SetUploadStatusAsync(Guid jobId, string uploadStatus, string? detail, CancellationToken cancellationToken)
	{
		_ = (jobId, uploadStatus, detail, cancellationToken);
		return Task.CompletedTask;
	}
}
