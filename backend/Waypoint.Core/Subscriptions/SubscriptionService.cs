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

using Waypoint.Core.Secrets;

namespace Waypoint.Core.Subscriptions;

/// <summary>A subscription create/update request, independent of the wire contract (issue #1450).</summary>
public sealed record SubscriptionWriteFields(
	string? Product,
	string? Lane,
	string? LineGranularity,
	string? AnchorVersion,
	Guid? PresetId,
	int? RefreshWindowDays,
	int? RetentionOverrideDays,
	bool? IsEnabled);

public enum SubscriptionWriteOutcome
{
	Ok,
	ValidationError,
	PresetNotFound,
	NotFound,
}

public sealed record SubscriptionWriteResult(SubscriptionWriteOutcome Outcome, Subscription? Subscription, string? Error)
{
	public static SubscriptionWriteResult Ok(Subscription subscription) => new(SubscriptionWriteOutcome.Ok, subscription, null);

	public static SubscriptionWriteResult Fail(SubscriptionWriteOutcome outcome, string error) => new(outcome, null, error);
}

/// <summary>
/// Issue #1450: subscription CRUD + preset-adopt orchestration over
/// <see cref="ISubscriptionRepository"/> and <see cref="IPresetRepository"/>. Adopting a
/// preset (<see cref="CreateAsync"/> with a non-null <c>PresetId</c>) copies the preset's
/// <c>LineGranularity</c>/<c>AnchorVersion</c> at create time only -- the preset itself is
/// never mutated, and a later shipped-preset content update never reaches an already
/// -created subscription (per #1045's own AC). This service never enqueues a download or
/// evaluation -- that is #1046's evaluation job, not this create/update/delete path's job.
/// </summary>
public sealed class SubscriptionService
{
	private readonly ISubscriptionRepository _subscriptions;
	private readonly IPresetRepository _presets;

	public SubscriptionService(ISubscriptionRepository subscriptions, IPresetRepository presets)
	{
		ArgumentNullException.ThrowIfNull(subscriptions);
		ArgumentNullException.ThrowIfNull(presets);
		_subscriptions = subscriptions;
		_presets = presets;
	}

	public Task<IReadOnlyList<Subscription>> ListAsync(CancellationToken cancellationToken) => _subscriptions.ListAllAsync(cancellationToken);

	public Task<Subscription?> GetAsync(Guid id, CancellationToken cancellationToken) => _subscriptions.GetAsync(id, cancellationToken);

	public async Task<SubscriptionWriteResult> CreateAsync(SubscriptionWriteFields fields, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(fields);

		if (string.IsNullOrWhiteSpace(fields.Product))
		{
			return SubscriptionWriteResult.Fail(SubscriptionWriteOutcome.ValidationError, "'product' is required.");
		}
		if (!RepoStores.IsValid(fields.Lane))
		{
			return SubscriptionWriteResult.Fail(
				SubscriptionWriteOutcome.ValidationError,
				$"'lane' must be one of: {string.Join(", ", RepoStores.All)}.");
		}

		string? anchorVersion = fields.AnchorVersion;
		SubscriptionLineGranularity? granularity = null;
		if (fields.LineGranularity is not null && !TryParseGranularity(fields.LineGranularity, out granularity))
		{
			return SubscriptionWriteResult.Fail(
				SubscriptionWriteOutcome.ValidationError,
				$"'line_granularity' must be one of: {string.Join(", ", SubscriptionLineGranularityValues.All)}.");
		}

		if (fields.PresetId is Guid presetId)
		{
			// Adopt-a-preset path (#1450 AC3): seed the dials from the preset at adopt
			// time. The preset's own values win over anything the caller also supplied,
			// so "adopting a preset" always means adopting ITS line, never a caller
			// -asserted one that happens to disagree with it.
			Preset? preset = await _presets.GetAsync(presetId, cancellationToken).ConfigureAwait(false);
			if (preset is null)
			{
				return SubscriptionWriteResult.Fail(SubscriptionWriteOutcome.PresetNotFound, $"Preset '{presetId}' was not found.");
			}
			if (preset.AnchorVersion is null)
			{
				return SubscriptionWriteResult.Fail(
					SubscriptionWriteOutcome.ValidationError,
					"This preset has no anchor version set yet and cannot be adopted.");
			}
			anchorVersion = preset.AnchorVersion;
			granularity = preset.LineGranularity;
		}
		else if (granularity is null)
		{
			return SubscriptionWriteResult.Fail(SubscriptionWriteOutcome.ValidationError, "'line_granularity' is required.");
		}

		if (string.IsNullOrWhiteSpace(anchorVersion))
		{
			return SubscriptionWriteResult.Fail(SubscriptionWriteOutcome.ValidationError, "'anchor_version' is required.");
		}
		if (fields.RefreshWindowDays is <= 0)
		{
			return SubscriptionWriteResult.Fail(SubscriptionWriteOutcome.ValidationError, "'refresh_window_days' must be a positive number of days.");
		}
		if (fields.RetentionOverrideDays is <= 0)
		{
			return SubscriptionWriteResult.Fail(SubscriptionWriteOutcome.ValidationError, "'retention_override_days' must be a positive number of days.");
		}

		Subscription subscription = new(
			Id: Guid.NewGuid(),
			Product: fields.Product,
			Lane: fields.Lane!,
			LineGranularity: granularity!.Value,
			AnchorVersion: anchorVersion,
			PresetId: fields.PresetId,
			RefreshWindowDays: fields.RefreshWindowDays,
			RetentionOverrideDays: fields.RetentionOverrideDays,
			IsEnabled: fields.IsEnabled ?? true,
			CreatedAt: default,
			UpdatedAt: default);

		Guid id = await _subscriptions.CreateAsync(subscription, cancellationToken).ConfigureAwait(false);
		return SubscriptionWriteResult.Ok((await _subscriptions.GetAsync(id, cancellationToken).ConfigureAwait(false))!);
	}

	public async Task<SubscriptionWriteResult> UpdateAsync(Guid id, SubscriptionWriteFields fields, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(fields);

		Subscription? existing = await _subscriptions.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (existing is null)
		{
			return SubscriptionWriteResult.Fail(SubscriptionWriteOutcome.NotFound, $"Subscription '{id}' was not found.");
		}

		if (string.IsNullOrWhiteSpace(fields.Product))
		{
			return SubscriptionWriteResult.Fail(SubscriptionWriteOutcome.ValidationError, "'product' is required.");
		}
		if (!RepoStores.IsValid(fields.Lane))
		{
			return SubscriptionWriteResult.Fail(
				SubscriptionWriteOutcome.ValidationError,
				$"'lane' must be one of: {string.Join(", ", RepoStores.All)}.");
		}
		if (!TryParseGranularity(fields.LineGranularity, out SubscriptionLineGranularity? granularity))
		{
			return SubscriptionWriteResult.Fail(
				SubscriptionWriteOutcome.ValidationError,
				$"'line_granularity' must be one of: {string.Join(", ", SubscriptionLineGranularityValues.All)}.");
		}
		if (string.IsNullOrWhiteSpace(fields.AnchorVersion))
		{
			return SubscriptionWriteResult.Fail(SubscriptionWriteOutcome.ValidationError, "'anchor_version' is required.");
		}
		if (fields.RefreshWindowDays is <= 0)
		{
			return SubscriptionWriteResult.Fail(SubscriptionWriteOutcome.ValidationError, "'refresh_window_days' must be a positive number of days.");
		}
		if (fields.RetentionOverrideDays is <= 0)
		{
			return SubscriptionWriteResult.Fail(SubscriptionWriteOutcome.ValidationError, "'retention_override_days' must be a positive number of days.");
		}

		// PresetId is deliberately never re-derived here -- a subscription's
		// originating preset is fixed at adopt time (#1450 AC3); an edit only ever
		// touches the fields the wire contract exposes.
		Subscription updated = existing with
		{
			Product = fields.Product,
			Lane = fields.Lane!,
			LineGranularity = granularity!.Value,
			AnchorVersion = fields.AnchorVersion,
			RefreshWindowDays = fields.RefreshWindowDays,
			RetentionOverrideDays = fields.RetentionOverrideDays,
			IsEnabled = fields.IsEnabled ?? existing.IsEnabled,
		};

		await _subscriptions.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
		return SubscriptionWriteResult.Ok((await _subscriptions.GetAsync(id, cancellationToken).ConfigureAwait(false))!);
	}

	/// <summary>Deletes a subscription. Returns <c>false</c> when no such subscription exists (caller maps that to 404).</summary>
	public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
	{
		Subscription? existing = await _subscriptions.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (existing is null)
		{
			return false;
		}
		await _subscriptions.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
		return true;
	}

	private static bool TryParseGranularity(string? value, out SubscriptionLineGranularity? granularity)
	{
		if (value is not null && SubscriptionLineGranularityValues.All.Contains(value))
		{
			granularity = SubscriptionLineGranularityValues.FromDbValue(value);
			return true;
		}
		granularity = null;
		return false;
	}
}
