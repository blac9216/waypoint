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

namespace Waypoint.Core.Subscriptions;

public enum PresetWriteOutcome
{
	Ok,
	ValidationError,
	NotFound,
	NotCustom,
}

public sealed record PresetWriteResult(PresetWriteOutcome Outcome, Preset? Preset, string? Error)
{
	public static PresetWriteResult Ok(Preset preset) => new(PresetWriteOutcome.Ok, preset, null);

	public static PresetWriteResult Fail(PresetWriteOutcome outcome, string error) => new(outcome, null, error);
}

/// <summary>A preset edit request -- <c>null</c> fields leave the existing value unchanged (issue #1450).</summary>
public sealed record PresetUpdateFields(string? Name, string? LineGranularity, string? AnchorVersion);

/// <summary>
/// Issue #1450: preset read + clone-to-custom + edit-clone orchestration over
/// <see cref="IPresetRepository"/>. Cloning always produces an independent row
/// (<see cref="Preset.IsCustom"/> <c>true</c>) -- editing that clone never mutates the
/// shipped source it was cloned from, and a later shipped-preset content update (an
/// appliance update, not this API) never touches an existing clone, since a clone is a
/// fully separate row from the moment <see cref="CloneAsync"/> creates it (#1045's own
/// AC). Writes to a shipped preset (<see cref="Preset.IsCustom"/> <c>false</c>) are
/// always rejected by <see cref="UpdateAsync"/> -- shipped content updates only via
/// appliance updates, never through this API.
/// </summary>
public sealed class PresetService
{
	private readonly IPresetRepository _presets;

	public PresetService(IPresetRepository presets)
	{
		ArgumentNullException.ThrowIfNull(presets);
		_presets = presets;
	}

	public Task<IReadOnlyList<Preset>> ListAsync(CancellationToken cancellationToken) => _presets.ListAllAsync(cancellationToken);

	public Task<Preset?> GetAsync(Guid id, CancellationToken cancellationToken) => _presets.GetAsync(id, cancellationToken);

	public async Task<PresetWriteResult> CloneAsync(Guid sourcePresetId, string? name, CancellationToken cancellationToken)
	{
		Preset? source = await _presets.GetAsync(sourcePresetId, cancellationToken).ConfigureAwait(false);
		if (source is null)
		{
			return PresetWriteResult.Fail(PresetWriteOutcome.NotFound, $"Preset '{sourcePresetId}' was not found.");
		}

		Preset clone = new(
			Id: Guid.NewGuid(),
			Stack: source.Stack,
			Generation: source.Generation,
			Name: string.IsNullOrWhiteSpace(name) ? $"{source.Name} (custom)" : name,
			LineGranularity: source.LineGranularity,
			AnchorVersion: source.AnchorVersion,
			IsCustom: true,
			SourcePresetId: source.Id,
			CreatedAt: default,
			UpdatedAt: default);

		Guid id = await _presets.CreateAsync(clone, cancellationToken).ConfigureAwait(false);
		return PresetWriteResult.Ok((await _presets.GetAsync(id, cancellationToken).ConfigureAwait(false))!);
	}

	public async Task<PresetWriteResult> UpdateAsync(Guid id, PresetUpdateFields fields, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(fields);

		Preset? existing = await _presets.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (existing is null)
		{
			return PresetWriteResult.Fail(PresetWriteOutcome.NotFound, $"Preset '{id}' was not found.");
		}
		if (!existing.IsCustom)
		{
			return PresetWriteResult.Fail(PresetWriteOutcome.NotCustom, "Shipped presets are read-only; clone it first to make an editable copy.");
		}

		string name = string.IsNullOrWhiteSpace(fields.Name) ? existing.Name : fields.Name;
		SubscriptionLineGranularity granularity = existing.LineGranularity;
		if (fields.LineGranularity is not null)
		{
			if (!SubscriptionLineGranularityValues.All.Contains(fields.LineGranularity))
			{
				return PresetWriteResult.Fail(
					PresetWriteOutcome.ValidationError,
					$"'line_granularity' must be one of: {string.Join(", ", SubscriptionLineGranularityValues.All)}.");
			}
			granularity = SubscriptionLineGranularityValues.FromDbValue(fields.LineGranularity);
		}
		string? anchorVersion = fields.AnchorVersion ?? existing.AnchorVersion;

		Preset updated = existing with { Name = name, LineGranularity = granularity, AnchorVersion = anchorVersion };
		await _presets.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
		return PresetWriteResult.Ok((await _presets.GetAsync(id, cancellationToken).ConfigureAwait(false))!);
	}
}
