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
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Core.Scheduling;
using Waypoint.Tests.Support;

namespace Waypoint.Tests.Api;

/// <summary>
/// Controller tests for /schedules (issue #31): role guards, the server-side read-only
/// job_type rejection ("Validation notes" in the issue -- enforced BY DESIGN, not
/// config), and CRUD against a fake <see cref="IScheduleRepository"/> so no Postgres is
/// needed here -- the same "swap the fake repository through TestAuthHandler" shape
/// <see cref="Waypoint.Tests.Api.RunsTestApiFactory"/> uses for /runs.
/// </summary>
public sealed class SchedulesEndpointTests : IClassFixture<SchedulesTestApiFactory>
{
	private readonly SchedulesTestApiFactory _factory;

	public SchedulesEndpointTests(SchedulesTestApiFactory factory)
	{
		_factory = factory;
	}

	[Theory]
	[InlineData("Viewer")]
	public async Task Create_WithRoleBelowCyber_Returns403(string role)
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/api/v1/schedules", role,
			new { name = $"s-{Guid.NewGuid():N}", job_type = "scan", cron_expression = "0 2 * * *" });

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Theory]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	[InlineData("Admin")]
	public async Task Create_WithCyberOrAbove_Returns201(string role)
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/api/v1/schedules", role,
			new { name = $"s-{Guid.NewGuid():N}", job_type = "scan", cron_expression = "0 2 * * *" });

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
	}

	/// <summary>
	/// The domain rule this issue exists to enforce: remediate/download/bundle-import/
	/// update are excluded from scheduling BY DESIGN. Covers the exact three named in
	/// the issue's acceptance criteria plus a fourth non-read-only type for the axis.
	/// <c>purge</c> (issue #594, epic #577) joins this list for the same reason
	/// <c>remediate</c> does -- a destructive, explicit-confirmation-gated operation
	/// must never be schedulable/implicit. <c>jobs_job_type_check</c>/<c>runs_run_type_check</c>
	/// (migration 0042) accept <c>purge</c> for the job-queue/audit shape, but
	/// <see cref="Waypoint.Core.Scheduling.ScheduleJobTypes.All"/> -- the closed set
	/// this endpoint actually validates against -- never includes it, so no separate
	/// enforcement point had to be added; this proves that structural guarantee rather
	/// than asserting it only in a migration comment.
	/// </summary>
	[Theory]
	[InlineData("remediate")]
	[InlineData("bundle-import")]
	[InlineData("update")]
	[InlineData("download")]
	[InlineData("purge")]
	public async Task Create_WithNonReadOnlyJobType_Returns400(string jobType)
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/api/v1/schedules", "Admin",
			new { name = $"s-{Guid.NewGuid():N}", job_type = jobType, cron_expression = "0 2 * * *" });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.Contains("unsupported_job_type", body, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("scan")]
	[InlineData("discover")]
	[InlineData("credential-test")]
	[InlineData("catalog-index")]
	public async Task Create_WithEveryReadOnlyJobType_Returns201(string jobType)
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/api/v1/schedules", "Admin",
			new { name = $"s-{Guid.NewGuid():N}", job_type = jobType, cron_expression = "0 2 * * *" });

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
	}

	/// <summary>
	/// Issue #517 review: the three Admin-gated job types (discover/credential-test/
	/// catalog-index are <c>[RequireAdminRole]</c> at their direct endpoints) must reject a
	/// Cyber caller on the scheduling surface too -- otherwise a Cyber user could schedule a
	/// job they cannot trigger directly (privilege escalation). Operator is also below Admin,
	/// so it is rejected as well; only Admin succeeds (covered by
	/// <see cref="Create_WithEveryReadOnlyJobType_Returns201"/>).
	/// </summary>
	[Theory]
	[InlineData("Cyber", "discover")]
	[InlineData("Cyber", "credential-test")]
	[InlineData("Cyber", "catalog-index")]
	[InlineData("Operator", "discover")]
	[InlineData("Operator", "credential-test")]
	[InlineData("Operator", "catalog-index")]
	public async Task Create_BelowAdminForAdminGatedJobType_Returns403(string role, string jobType)
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/api/v1/schedules", role,
			new { name = $"s-{Guid.NewGuid():N}", job_type = jobType, cron_expression = "0 2 * * *" });

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	/// <summary>The scan floor is Cyber, so Cyber creating a scan schedule still succeeds after the per-type differentiation.</summary>
	[Fact]
	public async Task Create_CyberWithScan_Returns201()
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/api/v1/schedules", "Cyber",
			new { name = $"s-{Guid.NewGuid():N}", job_type = "scan", cron_expression = "0 2 * * *" });

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
	}

	/// <summary>
	/// Update re-checks the EXISTING schedule's job_type: a Cyber user must not modify (or
	/// pause/resume, which flows through the same PUT `enabled` field) an Admin's
	/// Admin-typed schedule. Admin creates the schedule; Cyber's PUT is 403.
	/// </summary>
	[Theory]
	[InlineData("discover")]
	[InlineData("credential-test")]
	[InlineData("catalog-index")]
	public async Task Update_CyberOnAdminTypedSchedule_Returns403(string jobType)
	{
		HttpClient client = _factory.CreateClient();
		string id = await CreateAsAdminAsync(client, jobType);

		HttpResponseMessage response = await SendAsync(client, HttpMethod.Put, $"/api/v1/schedules/{id}", "Cyber",
			new { enabled = false });

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	/// <summary>A Cyber user updating a Cyber-floor (scan) schedule succeeds.</summary>
	[Fact]
	public async Task Update_CyberOnScanSchedule_Returns200()
	{
		HttpClient client = _factory.CreateClient();
		string id = await CreateAsAdminAsync(client, "scan");

		HttpResponseMessage response = await SendAsync(client, HttpMethod.Put, $"/api/v1/schedules/{id}", "Cyber",
			new { enabled = false });

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	/// <summary>Delete re-checks the existing job_type: a Cyber user must not delete an Admin's Admin-typed schedule.</summary>
	[Theory]
	[InlineData("discover")]
	[InlineData("credential-test")]
	[InlineData("catalog-index")]
	public async Task Delete_CyberOnAdminTypedSchedule_Returns403(string jobType)
	{
		HttpClient client = _factory.CreateClient();
		string id = await CreateAsAdminAsync(client, jobType);

		HttpResponseMessage response = await SendAsync(client, HttpMethod.Delete, $"/api/v1/schedules/{id}", "Cyber", body: null);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	/// <summary>A Cyber user deleting a scan schedule succeeds.</summary>
	[Fact]
	public async Task Delete_CyberOnScanSchedule_Returns204()
	{
		HttpClient client = _factory.CreateClient();
		string id = await CreateAsAdminAsync(client, "scan");

		HttpResponseMessage response = await SendAsync(client, HttpMethod.Delete, $"/api/v1/schedules/{id}", "Cyber", body: null);

		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
	}

	private static Task<string> CreateAsAdminAsync(HttpClient client, string jobType) =>
		CreateNamedAsAdminAsync(client, $"s-{Guid.NewGuid():N}", jobType);

	private static async Task<string> CreateNamedAsAdminAsync(HttpClient client, string name, string jobType)
	{
		HttpResponseMessage created = await SendAsync(client, HttpMethod.Post, "/api/v1/schedules", "Admin",
			new { name, job_type = jobType, cron_expression = "0 2 * * *" });
		Assert.Equal(HttpStatusCode.Created, created.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
		return document.RootElement.GetProperty("id").GetString()!;
	}

	[Fact]
	public async Task Create_WithInvalidCron_Returns400()
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/api/v1/schedules", "Admin",
			new { name = $"s-{Guid.NewGuid():N}", job_type = "scan", cron_expression = "not a cron" });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	/// <summary>docs/domain-model.md Scheduling: "record ... the schedule's creator" -- the authenticated caller, never a body field.</summary>
	[Fact]
	public async Task Create_RecordsCreatedByFromTheAuthenticatedCaller()
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/schedules")
		{
			Content = new StringContent(
				JsonSerializer.Serialize(new { name = $"s-{Guid.NewGuid():N}", job_type = "scan", cron_expression = "0 2 * * *" }),
				Encoding.UTF8, "application/json")
		};
		request.Headers.Add(TestAuthHandler.RoleHeaderName, "Cyber");

		HttpResponseMessage response = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("test-user", document.RootElement.GetProperty("created_by").GetString());
	}

	[Fact]
	public async Task Get_UnknownId_Returns404()
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, $"/api/v1/schedules/{Guid.NewGuid()}", "Viewer", body: null);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task DuplicateName_Returns409()
	{
		HttpClient client = _factory.CreateClient();
		string name = $"dup-{Guid.NewGuid():N}";
		await SendAsync(client, HttpMethod.Post, "/api/v1/schedules", "Admin",
			new { name, job_type = "scan", cron_expression = "0 2 * * *" });

		HttpResponseMessage second = await SendAsync(client, HttpMethod.Post, "/api/v1/schedules", "Admin",
			new { name, job_type = "scan", cron_expression = "0 2 * * *" });

		Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
	}

	/// <summary>
	/// Issue #520: an empty repository returns an empty array, not null or 404. Uses its
	/// own freshly-constructed factory (rather than the class-shared <see cref="_factory"/>,
	/// whose <see cref="FakeScheduleRepository"/> singleton accumulates schedules across
	/// every other test in this class) so "empty" is genuinely observed, not assumed.
	/// </summary>
	[Fact]
	public async Task List_NoSchedules_ReturnsEmptyArray()
	{
		using SchedulesTestApiFactory factory = new();
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, "/api/v1/schedules", "Viewer", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
		Assert.Empty(document.RootElement.EnumerateArray());
	}

	/// <summary>
	/// Issue #520: <see cref="_factory"/> is a shared <see cref="IClassFixture{TFixture}"/>
	/// (its <see cref="FakeScheduleRepository"/> is a singleton persisting across every test
	/// in this class, matching how <c>RunsTestApiFactory</c> is used elsewhere), so List
	/// tests below cannot assert global emptiness or an exact global count -- they scope
	/// their assertions to schedules they themselves created, identified by a per-test
	/// unique name suffix.
	/// </summary>
	[Fact]
	public async Task List_WithViewerRole_Returns200()
	{
		HttpClient client = _factory.CreateClient();
		await CreateAsAdminAsync(client, "scan");

		HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, "/api/v1/schedules", "Viewer", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
	}

	/// <summary>
	/// Issue #520: List returns every schedule regardless of job_type/role floor (a Viewer
	/// can see -- not write -- an Admin-typed schedule too), and the wire shape carries the
	/// full <c>ScheduleResponse</c> projection including <c>next_run_at</c> and
	/// <c>last_result</c> (null until the schedule has ever dispatched).
	/// </summary>
	[Fact]
	public async Task List_ReturnsEveryScheduleWithFullShape_IncludingNextRunAndLastResult()
	{
		HttpClient client = _factory.CreateClient();
		string suffix = Guid.NewGuid().ToString("N");
		string scanName = $"list-shape-scan-{suffix}";
		string discoverName = $"list-shape-discover-{suffix}";
		string scanId = await CreateNamedAsAdminAsync(client, scanName, "scan");
		string discoverId = await CreateNamedAsAdminAsync(client, discoverName, "discover");

		HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, "/api/v1/schedules", "Viewer", body: null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement[] items = document.RootElement.EnumerateArray()
			.Where(item => string.Equals(item.GetProperty("id").GetString(), scanId, StringComparison.Ordinal)
				|| string.Equals(item.GetProperty("id").GetString(), discoverId, StringComparison.Ordinal))
			.ToArray();
		Assert.Equal(2, items.Length);

		JsonElement scanItem = items.Single(item => string.Equals(item.GetProperty("id").GetString(), scanId, StringComparison.Ordinal));
		Assert.Equal("scan", scanItem.GetProperty("job_type").GetString());
		Assert.False(string.IsNullOrEmpty(scanItem.GetProperty("next_run_at").GetString()));
		// WaypointJsonOptions.Apply uses JsonIgnoreCondition.WhenWritingNull -- a schedule
		// that has never dispatched omits last_result/last_run_id entirely rather than
		// emitting an explicit JSON null, so "not present" is the correct assertion here.
		Assert.False(scanItem.TryGetProperty("last_result", out _));
		Assert.False(scanItem.TryGetProperty("last_run_id", out _));
		Assert.True(scanItem.GetProperty("enabled").GetBoolean());
		Assert.Equal("test-user", scanItem.GetProperty("created_by").GetString());
	}

	/// <summary>The repository (real implementation orders by name; the in-memory fake mirrors that ordering) is reflected verbatim on the wire -- List does not silently drop or reorder entries the repository returns.</summary>
	[Fact]
	public async Task List_OrdersSchedulesByName()
	{
		HttpClient client = _factory.CreateClient();
		string suffix = Guid.NewGuid().ToString("N");
		string nameZ = $"zzz-{suffix}";
		string nameA = $"aaa-{suffix}";

		await SendAsync(client, HttpMethod.Post, "/api/v1/schedules", "Admin",
			new { name = nameZ, job_type = "scan", cron_expression = "0 2 * * *" });
		await SendAsync(client, HttpMethod.Post, "/api/v1/schedules", "Admin",
			new { name = nameA, job_type = "scan", cron_expression = "0 2 * * *" });

		HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, "/api/v1/schedules", "Viewer", body: null);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string[] names = document.RootElement.EnumerateArray()
			.Select(item => item.GetProperty("name").GetString()!)
			.Where(name => string.Equals(name, nameZ, StringComparison.Ordinal) || string.Equals(name, nameA, StringComparison.Ordinal))
			.ToArray();

		Assert.Equal([nameA, nameZ], names);
	}

	private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string role, object? body)
	{
		HttpRequestMessage request = new(method, path);
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		if (body is not null)
		{
			request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
		}

		return await client.SendAsync(request);
	}
}

/// <summary>Test host wiring a fake in-memory <see cref="IScheduleRepository"/> through <see cref="TestAuthHandler"/>, mirroring <see cref="RunsTestApiFactory"/>.</summary>
public sealed class SchedulesTestApiFactory : WaypointApiFactory
{
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

			var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IScheduleRepository));
			if (descriptor != null)
			{
				services.Remove(descriptor);
			}

			services.AddSingleton<IScheduleRepository, FakeScheduleRepository>();
		});
	}
}

/// <summary>Minimal in-memory fake for SchedulesController tests -- only the surface the controller calls.</summary>
public sealed class FakeScheduleRepository : IScheduleRepository
{
	private readonly Dictionary<Guid, Schedule> _schedules = [];

	/// <summary>Mirrors the real <c>ScheduleRepository.ListAsync</c>'s <c>ORDER BY name</c> so List-ordering tests exercise a faithful double.</summary>
	public Task<IReadOnlyList<Schedule>> ListAsync(CancellationToken cancellationToken) =>
		Task.FromResult<IReadOnlyList<Schedule>>(
			_schedules.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList());

	public Task<Schedule?> GetAsync(Guid id, CancellationToken cancellationToken) =>
		Task.FromResult(_schedules.GetValueOrDefault(id));

	public Task<Guid?> CreateAsync(
		string name, string jobType, string cronExpression, string scopeJson, Guid? credentialId,
		DateTimeOffset nextRunAt, string createdBy, CancellationToken cancellationToken)
	{
		if (_schedules.Values.Any(s => string.Equals(s.Name, name, StringComparison.Ordinal)))
		{
			return Task.FromResult<Guid?>(null);
		}

		Guid id = Guid.NewGuid();
		DateTimeOffset now = DateTimeOffset.UtcNow;
		_schedules[id] = new Schedule(
			id, name, jobType, cronExpression, scopeJson, credentialId, Enabled: true, PausedReason: null,
			nextRunAt, LastRunAt: null, LastRunId: null, LastResult: null, createdBy, now, now);
		return Task.FromResult<Guid?>(id);
	}

	public Task<ScheduleWriteOutcome> UpdateAsync(
		Guid id, string? name, string? cronExpression, string? scopeJson, Guid? credentialId, bool clearCredential,
		bool? enabled, DateTimeOffset? nextRunAt, CancellationToken cancellationToken)
	{
		if (!_schedules.TryGetValue(id, out Schedule? existing))
		{
			return Task.FromResult(ScheduleWriteOutcome.NotFound);
		}

		if (name is not null && _schedules.Values.Any(s => s.Id != id && string.Equals(s.Name, name, StringComparison.Ordinal)))
		{
			return Task.FromResult(ScheduleWriteOutcome.NameTaken);
		}

		_schedules[id] = existing with
		{
			Name = name ?? existing.Name,
			CronExpression = cronExpression ?? existing.CronExpression,
			ScopeJson = scopeJson ?? existing.ScopeJson,
			CredentialId = clearCredential ? null : credentialId ?? existing.CredentialId,
			Enabled = enabled ?? existing.Enabled,
			NextRunAt = nextRunAt ?? existing.NextRunAt,
			UpdatedAt = DateTimeOffset.UtcNow,
		};
		return Task.FromResult(ScheduleWriteOutcome.Ok);
	}

	public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_schedules.Remove(id));

	public Task<IReadOnlyList<Schedule>> ListDueAsync(DateTimeOffset asOf, CancellationToken cancellationToken) =>
		Task.FromResult<IReadOnlyList<Schedule>>(
			_schedules.Values.Where(s => s.Enabled && s.PausedReason is null && s.NextRunAt is not null && s.NextRunAt <= asOf).ToList());

	public Task MarkDispatchedAsync(Guid id, DateTimeOffset nextRunAt, Guid runId, CancellationToken cancellationToken)
	{
		if (_schedules.TryGetValue(id, out Schedule? existing))
		{
			_schedules[id] = existing with { NextRunAt = nextRunAt, LastRunAt = DateTimeOffset.UtcNow, LastRunId = runId };
		}

		return Task.CompletedTask;
	}

	public Task SetPausedReasonAsync(Guid id, string? reason, CancellationToken cancellationToken)
	{
		if (_schedules.TryGetValue(id, out Schedule? existing))
		{
			_schedules[id] = existing with { PausedReason = reason };
		}

		return Task.CompletedTask;
	}

	public Task SetLastResultAsync(Guid id, string result, CancellationToken cancellationToken)
	{
		if (_schedules.TryGetValue(id, out Schedule? existing))
		{
			_schedules[id] = existing with { LastResult = result };
		}

		return Task.CompletedTask;
	}
}
