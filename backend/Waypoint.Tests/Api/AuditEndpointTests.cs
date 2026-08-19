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
using Waypoint.Core.Audit;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Api;

/// <summary>
/// Controller tests for /audit (issue #512): Cyber+ role guard, kind/actor/time-window
/// filtering, stable paging, and the CSV export mirroring the current filter -- against
/// a fake <see cref="IAuditRepository"/> (same "swap the fake repository through
/// TestAuthHandler" shape <see cref="SchedulesEndpointTests"/> uses).
/// </summary>
public sealed class AuditEndpointTests : IClassFixture<AuditTestApiFactory>
{
	private readonly AuditTestApiFactory _factory;

	public AuditEndpointTests(AuditTestApiFactory factory)
	{
		_factory = factory;
	}

	[Fact]
	public async Task List_BelowCyber_Returns403()
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, "/api/v1/audit", "Viewer");

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
	}

	[Theory]
	[InlineData("Cyber")]
	[InlineData("Operator")]
	[InlineData("Admin")]
	public async Task List_CyberOrAbove_Returns200(string role)
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, "/api/v1/audit", role);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.True(response.Headers.Contains("X-Total-Count"));
	}

	[Fact]
	public async Task List_FiltersByKind()
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, "/api/v1/audit?kind=credential.deleted", "Cyber");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.All(document.RootElement.EnumerateArray(), item => Assert.Equal("credential.deleted", item.GetProperty("event_type").GetString()));
	}

	[Fact]
	public async Task List_WithInvalidFromTimestamp_Returns400()
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, "/api/v1/audit?from=not-a-date", "Cyber");

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task List_AsCsv_ReturnsTextCsvWithHeaderRow()
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage response = await SendAsync(client, "/api/v1/audit?format=csv", "Cyber");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
		string body = await response.Content.ReadAsStringAsync();
		Assert.StartsWith("id,event_type,actor,credential_id,job_id,run_id,detail,occurred_at", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task List_AsCsv_MirrorsTheSameKindFilterAsJson()
	{
		HttpClient client = _factory.CreateClient();
		HttpResponseMessage jsonResponse = await SendAsync(client, "/api/v1/audit?kind=job.retried", "Cyber");
		HttpResponseMessage csvResponse = await SendAsync(client, "/api/v1/audit?kind=job.retried&format=csv", "Cyber");

		using JsonDocument document = JsonDocument.Parse(await jsonResponse.Content.ReadAsStringAsync());
		int jsonCount = document.RootElement.GetArrayLength();

		string csvBody = await csvResponse.Content.ReadAsStringAsync();
		int csvDataRows = csvBody.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1; // minus header

		Assert.Equal(jsonCount, csvDataRows);
	}

	private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string role)
	{
		HttpRequestMessage request = new(HttpMethod.Get, path);
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		return await client.SendAsync(request);
	}
}

/// <summary>Test host wiring a fake in-memory <see cref="IAuditRepository"/> through <see cref="TestAuthHandler"/>, mirroring <see cref="SchedulesTestApiFactory"/>.</summary>
public sealed class AuditTestApiFactory : WaypointApiFactory
{
	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		base.ConfigureWebHost(builder);

		builder.ConfigureTestServices(services =>
		{
			// Same TestAuthHandler-as-default-scheme wiring RoleGuardedApiFactory
			// provides, inlined here rather than switching base classes -- mirrors
			// SchedulesTestApiFactory's own shape exactly.
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

			ServiceDescriptor? descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IAuditRepository));
			if (descriptor is not null)
			{
				services.Remove(descriptor);
			}

			services.AddSingleton<IAuditRepository, FakeAuditRepository>();
		});
	}
}

/// <summary>Minimal in-memory fake for AuditController tests -- seeds a fixed, varied set of rows.</summary>
public sealed class FakeAuditRepository : IAuditRepository
{
	private static readonly IReadOnlyList<AuditEntry> SeedEntries =
	[
		new(Guid.NewGuid(), "credential.deleted", "admin", Guid.NewGuid(), null, null, "{}", DateTimeOffset.UtcNow.AddMinutes(-30)),
		new(Guid.NewGuid(), "credential.deleted", "admin", Guid.NewGuid(), null, null, "{}", DateTimeOffset.UtcNow.AddMinutes(-20)),
		new(Guid.NewGuid(), "job.retried", "cyber-user", null, Guid.NewGuid(), Guid.NewGuid(), "{\"job_id\":\"x\"}", DateTimeOffset.UtcNow.AddMinutes(-10)),
		new(Guid.NewGuid(), "secret.decrypted", "system:scan-job", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{}", DateTimeOffset.UtcNow.AddMinutes(-5)),
	];

	public Task<AuditListResult> ListAsync(AuditQuery query, int limit, int offset, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(query);

		IEnumerable<AuditEntry> filtered = SeedEntries;
		if (!string.IsNullOrWhiteSpace(query.EventType))
		{
			filtered = filtered.Where(e => string.Equals(e.EventType, query.EventType, StringComparison.Ordinal));
		}

		if (!string.IsNullOrWhiteSpace(query.Actor))
		{
			filtered = filtered.Where(e => string.Equals(e.Actor, query.Actor, StringComparison.Ordinal));
		}

		if (query.From is DateTimeOffset from)
		{
			filtered = filtered.Where(e => e.OccurredAt >= from);
		}

		if (query.To is DateTimeOffset to)
		{
			filtered = filtered.Where(e => e.OccurredAt <= to);
		}

		List<AuditEntry> ordered = [.. filtered.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)];
		List<AuditEntry> page = [.. ordered.Skip(offset).Take(limit)];
		return Task.FromResult(new AuditListResult(page, ordered.Count));
	}
}
