/**
 * Sites & Targets data layer (docs/api-contract.md "Sites, targets,
 * inventory" + the "Configuration" ledger row), against the #19 backend
 * (PR #238):
 *
 *   GET/POST      /sites                — Viewer+ read, Admin write
 *   GET/PUT/DELETE /sites/{id}
 *   GET/POST      /sites/{id}/targets    — Viewer+ read, Admin write
 *   GET/PUT/DELETE /targets/{id}
 *   GET           /credentials           — id/name only (never secret material)
 *
 * `connection` rides as a small JSON object; the only field either the
 * backend or the prototype names is `host` (docs/api-contract.md:
 * "connection.host"; backend's `TargetConnectionValidator` additionally
 * rejects any secret-shaped key such as `password`/`token`/`credential`
 * before it ever reaches storage). The UI only ever collects `host` — never
 * a raw secret — and passes `credential_ref` for anything requiring auth.
 */

import { apiDelete, apiGet, apiPost, apiPut } from "../../lib/api";

export type TargetKind = "vsphere" | "nsx-api" | "ssh";

export const TARGET_KINDS: { value: TargetKind; label: string }[] = [
	{ value: "vsphere", label: "vSphere (vCenter)" },
	{ value: "nsx-api", label: "NSX API" },
	{ value: "ssh", label: "SSH (SRG)" },
];

export type DiscoveryStatus = "never_discovered" | "discovering" | "discovered" | "failed";

export interface Site {
	id: string;
	name: string;
	description: string | null;
	stigman_override: string | null;
	created_at: string;
	updated_at: string;
}

export interface Target {
	id: string;
	site_id: string;
	kind: TargetKind | string;
	name: string;
	/** Raw JSON text, e.g. `{"host":"vcsa-01.example.internal"}` — see module doc. */
	connection: string;
	credential_ref: string | null;
	discovery_status: DiscoveryStatus | string;
	last_refreshed: string | null;
	created_at: string;
	updated_at: string;
}

/** The one connection field the UI (and the backend's only documented shape) uses. */
export function connectionHost(connection: string): string {
	try {
		const parsed = JSON.parse(connection) as { host?: unknown };
		return typeof parsed.host === "string" ? parsed.host : "";
	} catch {
		return "";
	}
}

export function fetchSites(): Promise<Site[]> {
	return apiGet<Site[]>("/sites");
}

export interface SiteWriteInput {
	name: string;
	description?: string | null;
}

export function createSite(input: SiteWriteInput): Promise<Site> {
	return apiPost<Site>("/sites", { name: input.name, description: input.description || undefined });
}

export function updateSite(id: string, input: SiteWriteInput): Promise<Site> {
	return apiPut<Site>(`/sites/${id}`, {
		name: input.name,
		description: input.description || undefined,
		clear_description: !input.description,
	});
}

export function deleteSite(id: string, force = false): Promise<void> {
	return apiDelete<void>(`/sites/${id}${force ? "?force=true" : ""}`);
}

export function fetchTargets(siteId: string): Promise<Target[]> {
	return apiGet<Target[]>(`/sites/${siteId}/targets`);
}

export interface TargetWriteInput {
	kind: TargetKind;
	name: string;
	host: string;
	credential_ref?: string | null;
}

export function createTarget(siteId: string, input: TargetWriteInput): Promise<Target> {
	return apiPost<Target>(`/sites/${siteId}/targets`, {
		kind: input.kind,
		name: input.name,
		connection: { host: input.host },
		credential_ref: input.credential_ref || undefined,
	});
}

export function updateTarget(id: string, input: TargetWriteInput): Promise<Target> {
	return apiPut<Target>(`/targets/${id}`, {
		kind: input.kind,
		name: input.name,
		connection: { host: input.host },
		credential_ref: input.credential_ref || undefined,
		clear_credential_ref: !input.credential_ref,
	});
}

export function deleteTarget(id: string): Promise<void> {
	return apiDelete<void>(`/targets/${id}`);
}

/** `/credentials` — id/name only, per docs/api-contract.md's data ledger
 * ("`/credentials` (names only)"). The full response carries more (type,
 * health, ...) but the picker only ever reads `id`/`name`; it must never
 * request or render secret material (there is none to request — the
 * backend's CredentialResponse has no secret field at all). */
export interface CredentialOption {
	id: string;
	name: string;
}

export function fetchCredentialOptions(): Promise<CredentialOption[]> {
	return apiGet<CredentialOption[]>("/credentials");
}

export function formatDiscoveryStatus(status: string): string {
	switch (status) {
		case "never_discovered":
			return "never discovered";
		case "discovering":
			return "discovering…";
		case "discovered":
			return "discovered";
		case "failed":
			return "discovery failed";
		default:
			return status;
	}
}

export function formatTimestamp(iso: string | null): string {
	if (!iso) {
		return "—";
	}
	const date = new Date(iso);
	if (Number.isNaN(date.getTime())) {
		return "—";
	}
	return date.toISOString().slice(0, 16).replace("T", " ") + "Z";
}
