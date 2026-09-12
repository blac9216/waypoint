/**
 * Presets screen data layer (issue #1469, epic #1182). Wired against
 * `PresetsController` (issue #1450, `docs/reference/api-contract.md`
 * "Subscriptions & presets"): `GET /presets`, `GET /presets/{id}`,
 * `POST /presets/{id}/clone` (Admin-only clone-to-custom), `PUT /presets/{id}`
 * (Admin-only edit of a custom clone; a write to a shipped preset is rejected
 * with 409 `preset_not_custom`). Shapes below mirror `PresetDtos.cs`
 * field-for-field.
 *
 * Adopt-a-preset (`POST /subscriptions` with `preset_id`) belongs to the
 * subscription editor (issue #1473, this issue's sibling) — this module does
 * not call it. The screen only navigates there with the preset id, per
 * #1469's own AC ("Adopt ... actions navigate to the subscription editor
 * with the preset pre-filled").
 */

import { apiGet, apiPost, apiPut } from "../../lib/api";

/** `Preset.Stack` on the wire — closed set, never free text. */
export type PresetStack = "VCF" | "VVF";

/** `SubscriptionLineGranularity` on the wire — closed set, never free text. */
export type LineGranularity = "subminor" | "minor" | "major";

export interface Preset {
	id: string;
	stack: PresetStack;
	generation: string;
	name: string;
	line_granularity: LineGranularity;
	anchor_version: string | null;
	is_custom: boolean;
	source_preset_id: string | null;
	created_at: string;
	updated_at: string;
}

export function fetchPresets(): Promise<Preset[]> {
	return apiGet<Preset[]>("/presets");
}

export function fetchPreset(id: string): Promise<Preset> {
	return apiGet<Preset>(`/presets/${encodeURIComponent(id)}`);
}

/** Clone-to-custom (Admin-only): produces an independent custom row; editing
 * the clone never mutates the source, and a later shipped-preset content
 * update never touches the clone. `name` omitted defaults server-side to
 * "`<source name>` (custom)". */
export function clonePreset(id: string, name?: string): Promise<Preset> {
	return apiPost<Preset>(`/presets/${encodeURIComponent(id)}/clone`, name ? { name } : {});
}

export interface PresetEditFields {
	name?: string;
	line_granularity?: LineGranularity;
	anchor_version?: string;
}

/** Edits a custom clone in place (Admin-only). A write to a shipped preset
 * (`is_custom: false`) is rejected by the server with 409 `preset_not_custom`
 * — callers should check `ApiError.code === "preset_not_custom"`. */
export function updatePreset(id: string, fields: PresetEditFields): Promise<Preset> {
	return apiPut<Preset>(`/presets/${encodeURIComponent(id)}`, fields);
}
