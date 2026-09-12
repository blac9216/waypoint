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

using Waypoint.Core.Subscriptions;
using Xunit;

namespace Waypoint.Tests.Core.Subscriptions;

/// <summary>
/// Issue #1450: <see cref="PresetService"/> clone-to-custom and edit-clone semantics
/// against an in-memory fake -- #1045's own AC that a clone is independent of its
/// shipped source (edits never touch the source, and a later source update never
/// touches the clone).
/// </summary>
public sealed class PresetServiceTests
{
	private sealed class FakePresetRepository : IPresetRepository
	{
		private readonly Dictionary<Guid, Preset> _rows = [];

		public Guid Seed(Preset preset)
		{
			Guid id = preset.Id == Guid.Empty ? Guid.NewGuid() : preset.Id;
			_rows[id] = preset with { Id = id };
			return id;
		}

		public Task<Guid> CreateAsync(Preset preset, CancellationToken cancellationToken) => Task.FromResult(Seed(preset));

		public Task UpdateAsync(Preset preset, CancellationToken cancellationToken)
		{
			if (_rows.ContainsKey(preset.Id))
			{
				_rows[preset.Id] = preset;
			}
			return Task.CompletedTask;
		}

		public Task<Preset?> GetAsync(Guid id, CancellationToken cancellationToken) =>
			Task.FromResult(_rows.TryGetValue(id, out Preset? row) ? row : null);

		public Task<IReadOnlyList<Preset>> ListAllAsync(CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<Preset>>([.. _rows.Values]);

		public Task<IReadOnlyList<Preset>> ListShippedAsync(CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<Preset>>([.. _rows.Values.Where(p => !p.IsCustom)]);

		public Task DeleteAsync(Guid id, CancellationToken cancellationToken)
		{
			_rows.Remove(id);
			return Task.CompletedTask;
		}
	}

	private static Preset Shipped(string name = "vcf-current") => new(
		Guid.Empty, "VCF", "9", name, SubscriptionLineGranularity.Minor, "9.0",
		IsCustom: false, SourcePresetId: null, default, default);

	[Fact]
	public async Task CloneAsync_UnknownSource_ReturnsNotFound()
	{
		PresetService service = new(new FakePresetRepository());

		PresetWriteResult result = await service.CloneAsync(Guid.NewGuid(), null, CancellationToken.None);

		Assert.Equal(PresetWriteOutcome.NotFound, result.Outcome);
	}

	[Fact]
	public async Task CloneAsync_ProducesIndependentCustomPreset_DefaultingName()
	{
		FakePresetRepository repository = new();
		Guid sourceId = repository.Seed(Shipped());
		PresetService service = new(repository);

		PresetWriteResult result = await service.CloneAsync(sourceId, null, CancellationToken.None);

		Assert.Equal(PresetWriteOutcome.Ok, result.Outcome);
		Assert.True(result.Preset!.IsCustom);
		Assert.Equal(sourceId, result.Preset.SourcePresetId);
		Assert.NotEqual(sourceId, result.Preset.Id);
		Assert.Equal("vcf-current (custom)", result.Preset.Name);
	}

	[Fact]
	public async Task CloneAsync_HonorsExplicitName()
	{
		FakePresetRepository repository = new();
		Guid sourceId = repository.Seed(Shipped());
		PresetService service = new(repository);

		PresetWriteResult result = await service.CloneAsync(sourceId, "my custom preset", CancellationToken.None);

		Assert.Equal("my custom preset", result.Preset!.Name);
	}

	/// <summary>#1045 AC: editing a clone never mutates the shipped source it was cloned from.</summary>
	[Fact]
	public async Task UpdateAsync_EditingClone_NeverMutatesShippedSource()
	{
		FakePresetRepository repository = new();
		Guid sourceId = repository.Seed(Shipped());
		PresetService service = new(repository);
		PresetWriteResult clone = await service.CloneAsync(sourceId, null, CancellationToken.None);

		PresetWriteResult updated = await service.UpdateAsync(
			clone.Preset!.Id, new PresetUpdateFields("renamed clone", "major", "9.1"), CancellationToken.None);

		Assert.Equal(PresetWriteOutcome.Ok, updated.Outcome);
		Assert.Equal("renamed clone", updated.Preset!.Name);
		Assert.Equal(SubscriptionLineGranularity.Major, updated.Preset.LineGranularity);
		Assert.Equal("9.1", updated.Preset.AnchorVersion);

		Preset? source = await repository.GetAsync(sourceId, CancellationToken.None);
		Assert.Equal("vcf-current", source!.Name);
		Assert.Equal(SubscriptionLineGranularity.Minor, source.LineGranularity);
		Assert.Equal("9.0", source.AnchorVersion);
	}

	/// <summary>#1045 AC: writes to a shipped preset are rejected -- it updates only via appliance updates, never through this API.</summary>
	[Fact]
	public async Task UpdateAsync_ShippedPreset_ReturnsNotCustom()
	{
		FakePresetRepository repository = new();
		Guid sourceId = repository.Seed(Shipped());
		PresetService service = new(repository);

		PresetWriteResult result = await service.UpdateAsync(sourceId, new PresetUpdateFields("hacked", null, null), CancellationToken.None);

		Assert.Equal(PresetWriteOutcome.NotCustom, result.Outcome);
		Preset? unchanged = await repository.GetAsync(sourceId, CancellationToken.None);
		Assert.Equal("vcf-current", unchanged!.Name);
	}

	[Fact]
	public async Task UpdateAsync_UnknownId_ReturnsNotFound()
	{
		PresetService service = new(new FakePresetRepository());

		PresetWriteResult result = await service.UpdateAsync(Guid.NewGuid(), new PresetUpdateFields("x", null, null), CancellationToken.None);

		Assert.Equal(PresetWriteOutcome.NotFound, result.Outcome);
	}

	[Fact]
	public async Task UpdateAsync_InvalidGranularity_ReturnsValidationError()
	{
		FakePresetRepository repository = new();
		Guid sourceId = repository.Seed(Shipped());
		PresetService service = new(repository);
		PresetWriteResult clone = await service.CloneAsync(sourceId, null, CancellationToken.None);

		PresetWriteResult result = await service.UpdateAsync(
			clone.Preset!.Id, new PresetUpdateFields(null, "whole-release", null), CancellationToken.None);

		Assert.Equal(PresetWriteOutcome.ValidationError, result.Outcome);
	}

	/// <summary>#1045 AC: a later shipped-preset content update (simulated here) leaves an existing clone untouched.</summary>
	[Fact]
	public async Task ShippedPresetUpdate_LeavesExistingClonesUntouched()
	{
		FakePresetRepository repository = new();
		Guid sourceId = repository.Seed(Shipped());
		PresetService service = new(repository);
		PresetWriteResult clone = await service.CloneAsync(sourceId, "my clone", CancellationToken.None);

		// Simulate an appliance update refreshing the shipped preset directly through
		// the repository (never through this service, which always rejects a shipped
		// write) -- the clone must remain exactly as it was cloned.
		Preset shipped = (await repository.GetAsync(sourceId, CancellationToken.None))!;
		await repository.UpdateAsync(shipped with { Name = "vcf-current (updated by appliance)", AnchorVersion = "9.1" }, CancellationToken.None);

		Preset? unchangedClone = await repository.GetAsync(clone.Preset!.Id, CancellationToken.None);
		Assert.Equal("my clone", unchangedClone!.Name);
		Assert.Equal("9.0", unchangedClone.AnchorVersion);
	}
}
