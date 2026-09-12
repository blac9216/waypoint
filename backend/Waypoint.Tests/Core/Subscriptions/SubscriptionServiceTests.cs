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
/// Issue #1450: <see cref="SubscriptionService"/> validation and adopt-a-preset
/// orchestration, against in-memory fakes -- no Postgres needed, unlike the
/// repository's own tests.
/// </summary>
public sealed class SubscriptionServiceTests
{
	private sealed class FakeSubscriptionRepository : ISubscriptionRepository
	{
		private readonly Dictionary<Guid, Subscription> _rows = [];

		public Task<Guid> CreateAsync(Subscription subscription, CancellationToken cancellationToken)
		{
			Guid id = subscription.Id == Guid.Empty ? Guid.NewGuid() : subscription.Id;
			_rows[id] = subscription with { Id = id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
			return Task.FromResult(id);
		}

		public Task UpdateAsync(Subscription subscription, CancellationToken cancellationToken)
		{
			if (_rows.ContainsKey(subscription.Id))
			{
				_rows[subscription.Id] = subscription with { UpdatedAt = DateTimeOffset.UtcNow };
			}
			return Task.CompletedTask;
		}

		public Task<Subscription?> GetAsync(Guid id, CancellationToken cancellationToken) =>
			Task.FromResult(_rows.TryGetValue(id, out Subscription? row) ? row : null);

		public Task<IReadOnlyList<Subscription>> ListAllAsync(CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<Subscription>>([.. _rows.Values]);

		public Task<IReadOnlyList<Subscription>> ListEnabledByLaneAsync(string lane, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<Subscription>>([.. _rows.Values.Where(s => s.Lane == lane && s.IsEnabled)]);

		public Task DeleteAsync(Guid id, CancellationToken cancellationToken)
		{
			_rows.Remove(id);
			return Task.CompletedTask;
		}
	}

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

	private static SubscriptionWriteFields ValidFields(Guid? presetId = null) => new(
		Product: "VCENTER", Lane: "vmtools", LineGranularity: "minor", AnchorVersion: "9.0",
		PresetId: presetId, RefreshWindowDays: 7, RetentionOverrideDays: null, IsEnabled: true);

	[Fact]
	public async Task CreateAsync_ValidRequest_Succeeds()
	{
		SubscriptionService service = new(new FakeSubscriptionRepository(), new FakePresetRepository());

		SubscriptionWriteResult result = await service.CreateAsync(ValidFields(), CancellationToken.None);

		Assert.Equal(SubscriptionWriteOutcome.Ok, result.Outcome);
		Assert.Equal("VCENTER", result.Subscription!.Product);
		Assert.Null(result.Subscription.PresetId);
	}

	[Theory]
	[InlineData("")]
	[InlineData(null)]
	public async Task CreateAsync_MissingProduct_ReturnsValidationError(string? product)
	{
		SubscriptionService service = new(new FakeSubscriptionRepository(), new FakePresetRepository());

		SubscriptionWriteResult result = await service.CreateAsync(ValidFields() with { Product = product }, CancellationToken.None);

		Assert.Equal(SubscriptionWriteOutcome.ValidationError, result.Outcome);
	}

	[Fact]
	public async Task CreateAsync_UnknownLane_ReturnsValidationError()
	{
		SubscriptionService service = new(new FakeSubscriptionRepository(), new FakePresetRepository());

		SubscriptionWriteResult result = await service.CreateAsync(ValidFields() with { Lane = "not-a-real-lane" }, CancellationToken.None);

		Assert.Equal(SubscriptionWriteOutcome.ValidationError, result.Outcome);
	}

	[Fact]
	public async Task CreateAsync_UnknownGranularity_ReturnsValidationError()
	{
		SubscriptionService service = new(new FakeSubscriptionRepository(), new FakePresetRepository());

		SubscriptionWriteResult result = await service.CreateAsync(ValidFields() with { LineGranularity = "whole-release" }, CancellationToken.None);

		Assert.Equal(SubscriptionWriteOutcome.ValidationError, result.Outcome);
	}

	[Fact]
	public async Task CreateAsync_NonPositiveRefreshWindow_ReturnsValidationError()
	{
		SubscriptionService service = new(new FakeSubscriptionRepository(), new FakePresetRepository());

		SubscriptionWriteResult result = await service.CreateAsync(ValidFields() with { RefreshWindowDays = 0 }, CancellationToken.None);

		Assert.Equal(SubscriptionWriteOutcome.ValidationError, result.Outcome);
	}

	[Fact]
	public async Task CreateAsync_MissingPreset_ReturnsPresetNotFound()
	{
		SubscriptionService service = new(new FakeSubscriptionRepository(), new FakePresetRepository());

		SubscriptionWriteResult result = await service.CreateAsync(ValidFields(presetId: Guid.NewGuid()), CancellationToken.None);

		Assert.Equal(SubscriptionWriteOutcome.PresetNotFound, result.Outcome);
	}

	/// <summary>#1450 AC3: adopting a preset seeds the subscription's dials from the preset, ignoring the caller-supplied ones, and never mutates the preset.</summary>
	[Fact]
	public async Task CreateAsync_AdoptsPreset_CopiesDialsAtAdoptTime_AndNeverMutatesThePreset()
	{
		FakePresetRepository presets = new();
		Guid presetId = presets.Seed(new Preset(
			Guid.Empty, "VCF", "9", "vcf-current", SubscriptionLineGranularity.Subminor, "9.0.3",
			IsCustom: false, SourcePresetId: null, default, default));
		SubscriptionService service = new(new FakeSubscriptionRepository(), presets);

		SubscriptionWriteResult result = await service.CreateAsync(
			ValidFields(presetId: presetId) with { LineGranularity = "major", AnchorVersion = "8" },
			CancellationToken.None);

		Assert.Equal(SubscriptionWriteOutcome.Ok, result.Outcome);
		Assert.Equal(SubscriptionLineGranularity.Subminor, result.Subscription!.LineGranularity);
		Assert.Equal("9.0.3", result.Subscription.AnchorVersion);
		Assert.Equal(presetId, result.Subscription.PresetId);

		Preset? preset = await presets.GetAsync(presetId, CancellationToken.None);
		Assert.Equal("9.0.3", preset!.AnchorVersion); // unchanged
	}

	[Fact]
	public async Task CreateAsync_AdoptingPresetWithNoAnchorVersion_ReturnsValidationError()
	{
		FakePresetRepository presets = new();
		Guid presetId = presets.Seed(new Preset(
			Guid.Empty, "VCF", "9", "from-scratch", SubscriptionLineGranularity.Minor, null,
			IsCustom: true, SourcePresetId: null, default, default));
		SubscriptionService service = new(new FakeSubscriptionRepository(), presets);

		SubscriptionWriteResult result = await service.CreateAsync(ValidFields(presetId: presetId), CancellationToken.None);

		Assert.Equal(SubscriptionWriteOutcome.ValidationError, result.Outcome);
	}

	[Fact]
	public async Task UpdateAsync_UnknownId_ReturnsNotFound()
	{
		SubscriptionService service = new(new FakeSubscriptionRepository(), new FakePresetRepository());

		SubscriptionWriteResult result = await service.UpdateAsync(Guid.NewGuid(), ValidFields(), CancellationToken.None);

		Assert.Equal(SubscriptionWriteOutcome.NotFound, result.Outcome);
	}

	[Fact]
	public async Task UpdateAsync_ValidChange_PersistsAndNeverAltersPresetId()
	{
		FakeSubscriptionRepository subscriptions = new();
		FakePresetRepository presets = new();
		Guid presetId = presets.Seed(new Preset(
			Guid.Empty, "VCF", "9", "vcf-current", SubscriptionLineGranularity.Minor, "9.0",
			IsCustom: false, SourcePresetId: null, default, default));
		SubscriptionService service = new(subscriptions, presets);
		SubscriptionWriteResult created = await service.CreateAsync(ValidFields(presetId: presetId), CancellationToken.None);

		SubscriptionWriteResult updated = await service.UpdateAsync(
			created.Subscription!.Id,
			ValidFields() with { AnchorVersion = "9.1", IsEnabled = false },
			CancellationToken.None);

		Assert.Equal(SubscriptionWriteOutcome.Ok, updated.Outcome);
		Assert.Equal("9.1", updated.Subscription!.AnchorVersion);
		Assert.False(updated.Subscription.IsEnabled);
		Assert.Equal(presetId, updated.Subscription.PresetId);
	}

	[Fact]
	public async Task DeleteAsync_UnknownId_ReturnsFalse()
	{
		SubscriptionService service = new(new FakeSubscriptionRepository(), new FakePresetRepository());

		Assert.False(await service.DeleteAsync(Guid.NewGuid(), CancellationToken.None));
	}

	[Fact]
	public async Task DeleteAsync_ExistingSubscription_RemovesIt()
	{
		FakeSubscriptionRepository subscriptions = new();
		SubscriptionService service = new(subscriptions, new FakePresetRepository());
		SubscriptionWriteResult created = await service.CreateAsync(ValidFields(), CancellationToken.None);

		Assert.True(await service.DeleteAsync(created.Subscription!.Id, CancellationToken.None));
		Assert.Null(await service.GetAsync(created.Subscription.Id, CancellationToken.None));
	}
}
