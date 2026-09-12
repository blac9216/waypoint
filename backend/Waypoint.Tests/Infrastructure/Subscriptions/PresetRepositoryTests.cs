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

/// <summary>Issue #1421, migration 0104: <c>presets</c> against real Postgres.</summary>
[Collection("Postgres")]
public sealed class PresetRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private PresetRepository _repository = null!;

	public PresetRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetAsync();
		_repository = new PresetRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private async Task ResetAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand delete = new("DELETE FROM subscriptions; DELETE FROM presets", connection);
		await delete.ExecuteNonQueryAsync();
	}

	private static Preset NewShippedPreset(string name = "vcf-current") => new(
		Id: Guid.Empty,
		Stack: "VCF",
		Generation: "9",
		Name: name,
		LineGranularity: SubscriptionLineGranularity.Minor,
		AnchorVersion: "9.0",
		IsCustom: false,
		SourcePresetId: null,
		CreatedAt: default,
		UpdatedAt: default);

	[Fact]
	public async Task CreateAsync_ThenGetAsync_RoundTrips()
	{
		Preset preset = NewShippedPreset();

		Guid id = await _repository.CreateAsync(preset, CancellationToken.None);

		Preset? loaded = await _repository.GetAsync(id, CancellationToken.None);
		Assert.NotNull(loaded);
		Assert.Equal("VCF", loaded!.Stack);
		Assert.Equal("9", loaded.Generation);
		Assert.Equal(SubscriptionLineGranularity.Minor, loaded.LineGranularity);
		Assert.Equal("9.0", loaded.AnchorVersion);
		Assert.False(loaded.IsCustom);
		Assert.Null(loaded.SourcePresetId);
	}

	[Fact]
	public async Task GetAsync_UnknownId_ReturnsNull()
	{
		Preset? loaded = await _repository.GetAsync(Guid.NewGuid(), CancellationToken.None);

		Assert.Null(loaded);
	}

	[Fact]
	public async Task CreateAsync_CustomCloneWithSourcePresetId_RoundTrips()
	{
		Guid shippedId = await _repository.CreateAsync(NewShippedPreset("vcf-shipped"), CancellationToken.None);
		Preset clone = new(
			Id: Guid.Empty,
			Stack: "VCF",
			Generation: "9",
			Name: "vcf-shipped-custom",
			LineGranularity: SubscriptionLineGranularity.Subminor,
			AnchorVersion: "9.0.1",
			IsCustom: true,
			SourcePresetId: shippedId,
			CreatedAt: default,
			UpdatedAt: default);

		Guid cloneId = await _repository.CreateAsync(clone, CancellationToken.None);

		Preset? loaded = await _repository.GetAsync(cloneId, CancellationToken.None);
		Assert.NotNull(loaded);
		Assert.True(loaded!.IsCustom);
		Assert.Equal(shippedId, loaded.SourcePresetId);
	}

	[Fact]
	public async Task ListShippedAsync_ExcludesCustomPresets()
	{
		Guid shippedId = await _repository.CreateAsync(NewShippedPreset("vvf-shipped"), CancellationToken.None);
		await _repository.CreateAsync(
			new Preset(Guid.Empty, "VVF", "9", "vvf-custom", SubscriptionLineGranularity.Major, "9.0", IsCustom: true, SourcePresetId: shippedId, default, default),
			CancellationToken.None);

		IReadOnlyList<Preset> shipped = await _repository.ListShippedAsync(CancellationToken.None);

		Assert.Single(shipped);
		Assert.Equal(shippedId, shipped[0].Id);
	}

	[Fact]
	public async Task DeleteAsync_RemovesRow_AndIsANoOpForAnUnknownId()
	{
		Guid id = await _repository.CreateAsync(NewShippedPreset(), CancellationToken.None);

		await _repository.DeleteAsync(id, CancellationToken.None);
		await _repository.DeleteAsync(Guid.NewGuid(), CancellationToken.None);

		Assert.Null(await _repository.GetAsync(id, CancellationToken.None));
	}

	[Fact]
	public async Task UpdateAsync_ChangesNameGranularityAndAnchor_ButNeverStackOrIsCustom()
	{
		Guid id = await _repository.CreateAsync(NewShippedPreset("original") with { IsCustom = true }, CancellationToken.None);
		Preset original = (await _repository.GetAsync(id, CancellationToken.None))!;

		Preset updated = original with
		{
			Name = "renamed",
			LineGranularity = SubscriptionLineGranularity.Major,
			AnchorVersion = "9.1",
			Stack = "VVF", // deliberately ignored by UpdateAsync -- proves it below
			IsCustom = false, // deliberately ignored by UpdateAsync -- proves it below
		};
		await _repository.UpdateAsync(updated, CancellationToken.None);

		Preset? loaded = await _repository.GetAsync(id, CancellationToken.None);
		Assert.Equal("renamed", loaded!.Name);
		Assert.Equal(SubscriptionLineGranularity.Major, loaded.LineGranularity);
		Assert.Equal("9.1", loaded.AnchorVersion);
		Assert.Equal("VCF", loaded.Stack); // UPDATE never touches stack
		Assert.True(loaded.IsCustom); // UPDATE never touches is_custom
	}

	/// <summary>Review round 1 finding F2: <c>presets.stack</c> is a closed vocabulary (<see cref="PresetStacks"/>) enforced by <c>presets_stack_check</c>.</summary>
	[Fact]
	public async Task CreateAsync_InvalidStack_ViolatesCheckConstraint()
	{
		Preset invalidStack = NewShippedPreset() with { Id = Guid.Empty, Stack = "not-a-real-stack" };

		await Assert.ThrowsAsync<PostgresException>(() => _repository.CreateAsync(invalidStack, CancellationToken.None));
	}

	/// <summary>
	/// Review round 1 finding F4: <c>presets_lineage_requires_custom_check</c>
	/// rejects a shipped (<c>is_custom = false</c>) row that names a
	/// <c>source_preset_id</c> -- the migration's own comment says lineage only
	/// exists for a clone.
	/// </summary>
	[Fact]
	public async Task CreateAsync_ShippedPresetWithSourcePresetId_ViolatesCheckConstraint()
	{
		Guid shippedId = await _repository.CreateAsync(NewShippedPreset("vcf-shipped-lineage-source"), CancellationToken.None);
		Preset invalidLineage = NewShippedPreset("vcf-shipped-with-lineage") with { Id = Guid.Empty, IsCustom = false, SourcePresetId = shippedId };

		await Assert.ThrowsAsync<PostgresException>(() => _repository.CreateAsync(invalidLineage, CancellationToken.None));
	}

	/// <summary>
	/// Review round 1 finding F4: <c>presets_source_preset_id_not_self_check</c>
	/// rejects a preset that names itself as its own clone source.
	/// </summary>
	[Fact]
	public async Task CreateAsync_SelfReferencingSourcePresetId_ViolatesCheckConstraint()
	{
		Guid selfId = Guid.NewGuid();
		Preset selfReferencing = NewShippedPreset("self-referencing") with { Id = selfId, IsCustom = true, SourcePresetId = selfId };

		await Assert.ThrowsAsync<PostgresException>(() => _repository.CreateAsync(selfReferencing, CancellationToken.None));
	}
}
