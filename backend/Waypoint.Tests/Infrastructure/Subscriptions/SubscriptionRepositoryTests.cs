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
using Waypoint.Core.Subscriptions;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Subscriptions;
using Waypoint.Tests.Infrastructure.Postgres;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Subscriptions;

/// <summary>Issue #1421, migration 0104: <c>subscriptions</c> against real Postgres.</summary>
[Collection("Postgres")]
public sealed class SubscriptionRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private SubscriptionRepository _repository = null!;

	public SubscriptionRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetAsync();
		_repository = new SubscriptionRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private async Task ResetAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand delete = new("DELETE FROM subscriptions; DELETE FROM presets", connection);
		await delete.ExecuteNonQueryAsync();
	}

	private static Subscription NewSubscription(string lane = "vmtools", bool isEnabled = true, Guid? presetId = null) => new(
		Id: Guid.Empty,
		Product: "VCENTER",
		Lane: lane,
		LineGranularity: SubscriptionLineGranularity.Minor,
		AnchorVersion: "9.0",
		PresetId: presetId,
		RefreshWindowDays: 7,
		RetentionOverrideDays: null,
		IsEnabled: isEnabled,
		CreatedAt: default,
		UpdatedAt: default);

	[Fact]
	public async Task CreateAsync_ThenGetAsync_RoundTrips()
	{
		Subscription subscription = NewSubscription();

		Guid id = await _repository.CreateAsync(subscription, CancellationToken.None);

		Subscription? loaded = await _repository.GetAsync(id, CancellationToken.None);
		Assert.NotNull(loaded);
		Assert.Equal("VCENTER", loaded!.Product);
		Assert.Equal("vmtools", loaded.Lane);
		Assert.Equal(SubscriptionLineGranularity.Minor, loaded.LineGranularity);
		Assert.Equal("9.0", loaded.AnchorVersion);
		Assert.Equal(7, loaded.RefreshWindowDays);
		Assert.Null(loaded.RetentionOverrideDays);
		Assert.Null(loaded.PresetId);
		Assert.True(loaded.IsEnabled);
	}

	[Fact]
	public async Task GetAsync_UnknownId_ReturnsNull()
	{
		Subscription? loaded = await _repository.GetAsync(Guid.NewGuid(), CancellationToken.None);

		Assert.Null(loaded);
	}

	[Theory]
	[InlineData(SubscriptionLineGranularity.Subminor)]
	[InlineData(SubscriptionLineGranularity.Minor)]
	[InlineData(SubscriptionLineGranularity.Major)]
	public async Task CreateAsync_RoundTripsEveryLineGranularity(SubscriptionLineGranularity granularity)
	{
		Subscription subscription = NewSubscription() with { LineGranularity = granularity };

		Guid id = await _repository.CreateAsync(subscription, CancellationToken.None);

		Subscription? loaded = await _repository.GetAsync(id, CancellationToken.None);
		Assert.Equal(granularity, loaded!.LineGranularity);
	}

	[Fact]
	public async Task CreateAsync_WithPresetId_RoundTrips()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insertPreset = new(
			"""
			INSERT INTO presets (stack, generation, name, line_granularity, anchor_version, is_custom)
			VALUES ('VCF', '9', 'vcf-current', 'minor', '9.0', false)
			RETURNING id
			""", connection);
		Guid presetId = (Guid)(await insertPreset.ExecuteScalarAsync())!;

		Guid id = await _repository.CreateAsync(NewSubscription(presetId: presetId), CancellationToken.None);

		Subscription? loaded = await _repository.GetAsync(id, CancellationToken.None);
		Assert.Equal(presetId, loaded!.PresetId);
	}

	[Fact]
	public async Task ListEnabledByLaneAsync_ExcludesDisabledAndOtherLanes()
	{
		Guid enabledVmTools = await _repository.CreateAsync(NewSubscription(lane: "vmtools", isEnabled: true), CancellationToken.None);
		await _repository.CreateAsync(NewSubscription(lane: "vmtools", isEnabled: false), CancellationToken.None);
		await _repository.CreateAsync(NewSubscription(lane: "photon", isEnabled: true), CancellationToken.None);

		IReadOnlyList<Subscription> results = await _repository.ListEnabledByLaneAsync("vmtools", CancellationToken.None);

		Assert.Single(results);
		Assert.Equal(enabledVmTools, results[0].Id);
	}

	[Fact]
	public async Task ListAllAsync_ReturnsEveryRow()
	{
		await _repository.CreateAsync(NewSubscription(lane: "vmtools"), CancellationToken.None);
		await _repository.CreateAsync(NewSubscription(lane: "photon"), CancellationToken.None);

		IReadOnlyList<Subscription> results = await _repository.ListAllAsync(CancellationToken.None);

		Assert.Equal(2, results.Count);
	}

	[Fact]
	public async Task DeleteAsync_RemovesRow_AndIsANoOpForAnUnknownId()
	{
		Guid id = await _repository.CreateAsync(NewSubscription(), CancellationToken.None);

		await _repository.DeleteAsync(id, CancellationToken.None);
		await _repository.DeleteAsync(Guid.NewGuid(), CancellationToken.None);

		Assert.Null(await _repository.GetAsync(id, CancellationToken.None));
	}

	[Fact]
	public async Task UpdateAsync_ChangesMutableFields_ButNeverPresetIdOrCreatedAt()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insertPreset = new(
			"""
			INSERT INTO presets (stack, generation, name, line_granularity, anchor_version, is_custom)
			VALUES ('VCF', '9', 'vcf-current', 'minor', '9.0', false)
			RETURNING id
			""", connection);
		Guid presetId = (Guid)(await insertPreset.ExecuteScalarAsync())!;

		Guid id = await _repository.CreateAsync(NewSubscription(presetId: presetId), CancellationToken.None);
		Subscription original = (await _repository.GetAsync(id, CancellationToken.None))!;

		Subscription updated = original with
		{
			Lane = "umds",
			LineGranularity = SubscriptionLineGranularity.Major,
			AnchorVersion = "9",
			RefreshWindowDays = 14,
			RetentionOverrideDays = 30,
			IsEnabled = false,
			PresetId = null, // deliberately ignored by UpdateAsync -- proves it below
		};
		await _repository.UpdateAsync(updated, CancellationToken.None);

		Subscription? loaded = await _repository.GetAsync(id, CancellationToken.None);
		Assert.Equal("umds", loaded!.Lane);
		Assert.Equal(SubscriptionLineGranularity.Major, loaded.LineGranularity);
		Assert.Equal("9", loaded.AnchorVersion);
		Assert.Equal(14, loaded.RefreshWindowDays);
		Assert.Equal(30, loaded.RetentionOverrideDays);
		Assert.False(loaded.IsEnabled);
		Assert.Equal(presetId, loaded.PresetId); // UPDATE never touches preset_id
		Assert.Equal(original.CreatedAt, loaded.CreatedAt);
	}

	[Fact]
	public async Task CreateAsync_InvalidLane_ViolatesCheckConstraint()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO subscriptions (product, lane, line_granularity, anchor_version)
			VALUES ('VCENTER', 'not-a-real-lane', 'minor', '9.0')
			""", connection);

		await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
	}
}
