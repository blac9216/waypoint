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
	/// </summary>
	[Theory]
	[InlineData("remediate")]
	[InlineData("bundle-import")]
	[InlineData("update")]
	[InlineData("download")]
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

	public Task<IReadOnlyList<Schedule>> ListAsync(CancellationToken cancellationToken) =>
		Task.FromResult<IReadOnlyList<Schedule>>(_schedules.Values.ToList());

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
