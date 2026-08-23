/**
 * Benchmarks data layer (issue #559, epic #558) — the profile/control
 * browser and versioned config-document editor against:
 *
 *   GET  /profiles                       — `ProfilesController` (#40/#566),
 *                                           compliance-content inventory.
 *   GET  /config-docs                    — filter by kind/profile/layer.
 *   GET  /config-docs/{id}                 GET/PUT, PUT versions.
 *   GET  /config-docs/{id}/versions      — full history, read-only.
 *   GET  /config-docs/{id}/versions/{v}  — inspect one prior version.
 *   GET  /config-docs/resolve            — the EFFECTIVE card.
 *
 * There is no `/profiles/{id}/controls` endpoint yet (docs/api-contract.md
 * documents it, but only the `/profiles` list landed in #566) and no
 * per-control structured storage anywhere in the config-doc schema
 * (docs/domain-model.md "STIG configuration documents": "stored as
 * documents... not parsed into forms; the schemas belong to Broadcom/MITRE").
 * This module and the screen built on it therefore browse profiles (source/
 * version/state metadata) and whole-document YAML per (kind, profile,
 * layer) — never individual controls. Waiver "scope" is a free-text field
 * inside the attestation YAML the operator writes, not a structured
 * control picker; the screen surfaces it as a labeled hint, not a form
 * that implies the backend parses it.
 */
import { apiGet, apiPut } from "../../lib/api";

/** `ProfileResponse` (ComplianceContentContracts.cs) — `GET /profiles`. */
export interface ProfileSummary {
	id: string;
	profile_key: string;
	name: string;
	version: string | null;
	commit: string;
	state: string;
	updated_at: string;
}

export function fetchProfiles(): Promise<ProfileSummary[]> {
	return apiGet<ProfileSummary[]>("/profiles");
}

export type ConfigDocKind = "input" | "attestation" | "remediation-input";

export const CONFIG_DOC_KINDS: { value: ConfigDocKind; label: string }[] = [
	{ value: "input", label: "Inputs" },
	{ value: "attestation", label: "Attestations" },
	{ value: "remediation-input", label: "Remediation Inputs" },
];

export type LayerKind = "global" | "site" | "target";

/** Builds the `global|site:{id}|target:{id}` wire form `ConfigDocContracts.cs`'s
 * `FormatLayer` produces and `ConfigDocsController.ParseLayerOrThrow` parses. */
export function formatLayer(layer: LayerKind, ref: string | null): string {
	return layer === "global" ? "global" : `${layer}:${ref}`;
}

/** `ConfigDocResponse` (ConfigDocContracts.cs). */
export interface ConfigDocSummary {
	id: string;
	kind: string;
	profile: string;
	layer: string;
	version: number;
	author: string;
	body: string;
	created_at: string;
	updated_at: string;
}

export function fetchConfigDocs(params: { kind?: string; profile?: string; layer?: string }): Promise<ConfigDocSummary[]> {
	const query = new URLSearchParams();
	if (params.kind) query.set("kind", params.kind);
	if (params.profile) query.set("profile", params.profile);
	if (params.layer) query.set("layer", params.layer);
	const qs = query.toString();
	return apiGet<ConfigDocSummary[]>(`/config-docs${qs ? `?${qs}` : ""}`);
}

export function fetchConfigDoc(id: string): Promise<ConfigDocSummary> {
	return apiGet<ConfigDocSummary>(`/config-docs/${id}`);
}

/** `ConfigDocVersionResponse` (ConfigDocContracts.cs). */
export interface ConfigDocVersionSummary {
	version: number;
	author: string;
	body: string;
	created_at: string;
}

export function fetchConfigDocVersions(id: string): Promise<ConfigDocVersionSummary[]> {
	return apiGet<ConfigDocVersionSummary[]>(`/config-docs/${id}/versions`);
}

export function fetchConfigDocVersion(id: string, version: number): Promise<ConfigDocVersionSummary> {
	return apiGet<ConfigDocVersionSummary>(`/config-docs/${id}/versions/${version}`);
}

/**
 * `PUT /config-docs/{id}` — the sole write path. `id` may name an existing
 * doc (versions it, `kind`/`profile`/`layer` ignored) or a not-yet-existing
 * id (creates the slot at @v1; `kind`/`profile`/`layer` required). Callers
 * that don't yet know an id for a (kind, profile, layer) slot generate one
 * client-side with `crypto.randomUUID()` — this mirrors the backend's own
 * "first PUT creates the slot" contract (`ConfigDocsController.Save`'s doc
 * comment) rather than inventing a POST that doesn't exist.
 */
export function saveConfigDoc(
	id: string,
	body: { kind?: string; profile?: string; layer?: string; body: string },
): Promise<ConfigDocSummary> {
	return apiPut<ConfigDocSummary>(`/config-docs/${id}`, body);
}

/** `ConfigDocResolutionResponse` (ConfigDocContracts.cs) — `GET /config-docs/resolve`. */
export interface ConfigDocResolutionEntry {
	kind: string;
	profile: string;
	layer: string | null;
	body: string | null;
	doc_id: string | null;
	version: number | null;
	author: string | null;
	updated_at: string | null;
	attestation_expired: boolean;
	attestation_expires_at: string | null;
}

export function resolveConfigDocs(params: { profile: string; target: string; kind?: string }): Promise<ConfigDocResolutionEntry[]> {
	const query = new URLSearchParams({ profile: params.profile, target: params.target });
	if (params.kind) query.set("kind", params.kind);
	return apiGet<ConfigDocResolutionEntry[]>(`/config-docs/resolve?${query.toString()}`);
}

/** Display label for a `layer` wire value (`"global"` or `"{type}:{ref}"`). */
export function describeLayer(layer: string, siteNameById: Map<string, string>, targetNameById: Map<string, string>): string {
	if (layer === "global") {
		return "Global";
	}
	const [type, ref] = layer.split(":", 2);
	if (type === "site") {
		return `Site — ${siteNameById.get(ref ?? "") ?? ref}`;
	}
	if (type === "target") {
		return `Target — ${targetNameById.get(ref ?? "") ?? ref}`;
	}
	return layer;
}

export function formatTimestamp(iso: string | null): string {
	if (!iso) {
		return "—";
	}
	const date = new Date(iso);
	if (Number.isNaN(date.getTime())) {
		return "—";
	}
	return date.toISOString().slice(0, 19).replace("T", " ") + "Z";
}

/** Whether an attestation-kind resolution has lapsed — domain-model.md
 * "Expired attestations are not applied": the control still reports Open.
 * Mirrors `ConfigDocResolver`'s server-side check; the UI never re-derives
 * expiry from `attestation_expires_at` alone because the field can be set
 * (a future expiry) without `attestation_expired` being true yet — the
 * server is the single source of truth for "is this expired right now." */
export function isExpiredAttestation(entry: ConfigDocResolutionEntry): boolean {
	return entry.kind === "attestation" && entry.attestation_expired;
}
