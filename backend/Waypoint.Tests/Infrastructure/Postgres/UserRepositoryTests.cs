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
using Waypoint.Core.Authorization;
using Waypoint.Core.Users;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Users;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #512 storage-layer coverage against real Postgres: the oidc_sub-keyed upsert
/// (create on first sight, refresh username/role/auth_method on every call), the
/// last_seen_at throttle (the "without hammering the DB on every request" acceptance
/// criterion), and CreateAsync/UpdateSiteScopeAsync's own semantics.
/// </summary>
[Collection("Postgres")]
public sealed class UserRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private UserRepository _users = null!;

	public UserRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		_users = new UserRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task RecordSeenAsync_FirstSight_CreatesARow()
	{
		string sub = $"sub-{Guid.NewGuid():N}";

		await _users.RecordSeenAsync(sub, "alice", WaypointRole.Viewer, "oidc", TimeSpan.FromMinutes(5), CancellationToken.None);

		IReadOnlyList<UserRecord> all = await _users.ListAsync(CancellationToken.None);
		UserRecord created = Assert.Single(all, u => u.OidcSub == sub);
		Assert.Equal("alice", created.Username);
		Assert.Equal(WaypointRole.Viewer, created.Role);
		Assert.Equal("oidc", created.AuthMethod);
	}

	/// <summary>
	/// The core throttle acceptance criterion: a second RecordSeenAsync call within the
	/// interval must NOT advance last_seen_at, but a refreshed username/role still take
	/// effect immediately -- proving "next token evaluation" (#512's AC) is honoured for
	/// role even while the timestamp write itself is throttled.
	/// </summary>
	[Fact]
	public async Task RecordSeenAsync_WithinInterval_DoesNotAdvanceLastSeenButStillRefreshesRole()
	{
		string sub = $"sub-{Guid.NewGuid():N}";
		await _users.RecordSeenAsync(sub, "bob", WaypointRole.Viewer, "oidc", TimeSpan.FromMinutes(5), CancellationToken.None);
		UserRecord first = (await _users.ListAsync(CancellationToken.None)).Single(u => u.OidcSub == sub);

		await _users.RecordSeenAsync(sub, "bob", WaypointRole.Admin, "oidc", TimeSpan.FromMinutes(5), CancellationToken.None);
		UserRecord second = (await _users.ListAsync(CancellationToken.None)).Single(u => u.OidcSub == sub);

		Assert.Equal(first.LastSeenAt, second.LastSeenAt);
		Assert.Equal(WaypointRole.Admin, second.Role);
	}

	[Fact]
	public async Task RecordSeenAsync_PastInterval_AdvancesLastSeen()
	{
		string sub = $"sub-{Guid.NewGuid():N}";
		await _users.RecordSeenAsync(sub, "carol", WaypointRole.Viewer, "oidc", TimeSpan.Zero, CancellationToken.None);
		UserRecord first = (await _users.ListAsync(CancellationToken.None)).Single(u => u.OidcSub == sub);

		await Task.Delay(TimeSpan.FromMilliseconds(50));
		await _users.RecordSeenAsync(sub, "carol", WaypointRole.Viewer, "oidc", TimeSpan.Zero, CancellationToken.None);
		UserRecord second = (await _users.ListAsync(CancellationToken.None)).Single(u => u.OidcSub == sub);

		Assert.True(second.LastSeenAt > first.LastSeenAt);
	}

	[Fact]
	public async Task CreateAsync_DuplicateOidcSub_ReturnsNull()
	{
		string sub = $"sub-{Guid.NewGuid():N}";
		Guid? first = await _users.CreateAsync(sub, "dave", WaypointRole.Viewer, "[]", "oidc", CancellationToken.None);
		Guid? second = await _users.CreateAsync(sub, "dave2", WaypointRole.Admin, "[]", "oidc", CancellationToken.None);

		Assert.NotNull(first);
		Assert.Null(second);
	}

	[Fact]
	public async Task UpdateSiteScopeAsync_UnknownId_ReturnsFalse()
	{
		bool updated = await _users.UpdateSiteScopeAsync(Guid.NewGuid(), "[]", CancellationToken.None);
		Assert.False(updated);
	}

	[Fact]
	public async Task UpdateSiteScopeAsync_ExistingRow_UpdatesOnlySiteScope()
	{
		string sub = $"sub-{Guid.NewGuid():N}";
		Guid id = (await _users.CreateAsync(sub, "erin", WaypointRole.Cyber, "[]", "oidc", CancellationToken.None))!.Value;

		bool updated = await _users.UpdateSiteScopeAsync(id, "[\"site-a\",\"site-b\"]", CancellationToken.None);

		Assert.True(updated);
		UserRecord row = (await _users.GetAsync(id, CancellationToken.None))!;
		using System.Text.Json.JsonDocument scope = System.Text.Json.JsonDocument.Parse(row.SiteScopeJson);
		Assert.Equal(2, scope.RootElement.GetArrayLength());
		Assert.Equal(WaypointRole.Cyber, row.Role);
	}
}
