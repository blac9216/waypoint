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

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Audit;
using Waypoint.Infrastructure.Audit;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #512 storage-layer coverage against real Postgres: kind/actor/time-window
/// filtering (each independently and combined), stable <c>occurred_at DESC, id DESC</c>
/// ordering, and that <c>TotalCount</c> reflects the filtered set, not the whole table.
/// </summary>
[Collection("Postgres")]
public sealed class AuditRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private AuditRepository _audit = null!;
	private string _actor = null!;

	public AuditRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		_audit = new AuditRepository(_fixture.ConnectionString);

		// A distinct actor per test run isolates this test's rows from every other test
		// in the same shared "Postgres" collection database.
		_actor = $"actor-{Guid.NewGuid():N}";

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await InsertAsync(connection, "credential.deleted", _actor, DateTimeOffset.UtcNow.AddMinutes(-30));
		await InsertAsync(connection, "credential.deleted", _actor, DateTimeOffset.UtcNow.AddMinutes(-20));
		await InsertAsync(connection, "job.retried", _actor, DateTimeOffset.UtcNow.AddMinutes(-10));
		await InsertAsync(connection, "job.retried", $"other-{_actor}", DateTimeOffset.UtcNow.AddMinutes(-5));
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task ListAsync_FiltersByEventType()
	{
		AuditListResult result = await _audit.ListAsync(new AuditQuery("credential.deleted", _actor, null, null), 50, 0, CancellationToken.None);

		Assert.Equal(2, result.TotalCount);
		Assert.All(result.Items, item => Assert.Equal("credential.deleted", item.EventType));
	}

	[Fact]
	public async Task ListAsync_FiltersByActor()
	{
		AuditListResult result = await _audit.ListAsync(new AuditQuery(null, _actor, null, null), 50, 0, CancellationToken.None);

		Assert.Equal(3, result.TotalCount);
		Assert.All(result.Items, item => Assert.Equal(_actor, item.Actor));
	}

	[Fact]
	public async Task ListAsync_FiltersByTimeWindow()
	{
		AuditListResult result = await _audit.ListAsync(
			new AuditQuery(null, _actor, DateTimeOffset.UtcNow.AddMinutes(-25), DateTimeOffset.UtcNow.AddMinutes(-15)), 50, 0, CancellationToken.None);

		AuditEntry only = Assert.Single(result.Items);
		Assert.Equal("credential.deleted", only.EventType);
	}

	[Fact]
	public async Task ListAsync_OrdersNewestFirst()
	{
		AuditListResult result = await _audit.ListAsync(new AuditQuery(null, _actor, null, null), 50, 0, CancellationToken.None);

		List<DateTimeOffset> timestamps = [.. result.Items.Select(i => i.OccurredAt)];
		List<DateTimeOffset> expected = [.. timestamps.OrderByDescending(t => t)];
		Assert.Equal(expected, timestamps);
	}

	[Fact]
	public async Task ListAsync_PagesWithinTheFilteredSet()
	{
		AuditListResult page1 = await _audit.ListAsync(new AuditQuery(null, _actor, null, null), 2, 0, CancellationToken.None);
		AuditListResult page2 = await _audit.ListAsync(new AuditQuery(null, _actor, null, null), 2, 2, CancellationToken.None);

		Assert.Equal(3, page1.TotalCount);
		Assert.Equal(3, page2.TotalCount);
		Assert.Equal(2, page1.Items.Count);
		Assert.Single(page2.Items);
		Assert.DoesNotContain(page2.Items[0].Id, page1.Items.Select(i => i.Id));
	}

	private static async Task InsertAsync(NpgsqlConnection connection, string eventType, string actor, DateTimeOffset occurredAt)
	{
		await using NpgsqlCommand command = new(
			"INSERT INTO audit_log (event_type, actor, occurred_at) VALUES ($1, $2, $3)", connection);
		command.Parameters.AddWithValue(eventType);
		command.Parameters.AddWithValue(actor);
		command.Parameters.AddWithValue(occurredAt);
		await command.ExecuteNonQueryAsync();
	}
}
