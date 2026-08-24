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

/**
 * The explicit, shared target-kind inventory-capability contract (issue
 * #557's Risks/Considerations: "should come from a shared/explicit
 * target-kind contract rather than accumulating scattered `kind ===
 * "vsphere"` checks as more integrations are added"). Mirrors the backend's
 * `DiscoveryController.Discover` guard (`Waypoint.Core.Sites.TargetKinds`),
 * which today only accepts `vsphere` — kept as a Set (not a single constant)
 * so a second kind gaining discovery support later is a one-line addition
 * here, not a new comparison scattered across the UI.
 */
export const INVENTORY_CAPABLE_TARGET_KINDS: ReadonlySet<TargetKind> = new Set(["vsphere"]);

/** Whether `kind` supports `POST /targets/{id}/discover` — see {@link INVENTORY_CAPABLE_TARGET_KINDS}. */
export function isInventoryCapable(kind: TargetKind | string): boolean {
	return INVENTORY_CAPABLE_TARGET_KINDS.has(kind as TargetKind);
}

export type DiscoveryStatus = "never_discovered" | "discovering" | "discovered" | "failed";

export interface Site {
	id: string;
	name: string;
	description: string | null;
	stigman_override: string | null;
	created_at: string;
	updated_at: string;
}

/**
 * One purpose → credential binding on a target (issue #584, ADR-0021
 * docs/adr/0021-credential-purpose-matrix.md). `credential_name`/
 * `credential_type` are display-only enrichment the backend attaches (never
 * secret material) — `credential_ref` is the only field ever written back.
 */
export interface TargetCredentialBinding {
	purpose: string;
	credential_ref: string;
	credential_name: string | null;
	credential_type: string | null;
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
	/** DEPRECATED (issue #584): mirrors the target kind's default-purpose binding in `bindings` — see TargetContracts.cs's doc comment. Still round-tripped; prefer `bindings`. */
	credential_ref: string | null;
	discovery_status: DiscoveryStatus | string;
	last_refreshed: string | null;
	created_at: string;
	updated_at: string;
	/** Issue #584: purpose-specific credential bindings — see `TargetCredentialBinding`. Always present (possibly empty) from #584 onward. */
	bindings: TargetCredentialBinding[];
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

/** Issue #584: sets (creates or replaces/overrides) the credential bound to one purpose on a target. Returns the updated target (including the full `bindings` array) so the caller can refresh from one response. */
export function setTargetCredentialBinding(targetId: string, purpose: string, credentialId: string): Promise<Target> {
	return apiPut<Target>(`/targets/${targetId}/credential-bindings/${purpose}`, { credential_ref: credentialId });
}

/** Issue #584: clears (removes) the binding for one purpose on a target, if present. */
export function clearTargetCredentialBinding(targetId: string, purpose: string): Promise<Target> {
	return apiDelete<Target>(`/targets/${targetId}/credential-bindings/${purpose}`);
}

/** `DiscoverQueuedResponse` (DiscoveryController.cs) — `POST /targets/{id}/discover`.
 * Admin-only server-side (`RequireAdminRole`); queues a one-job `discover`
 * run and returns both ids so the caller can poll/link to it. */
export interface DiscoverQueuedResponse {
	run_id: string;
	job_id: string;
}

/** Queues a discovery (inventory refresh) run for one target — issue #557's
 * "Refresh Inventory" action. Callable regardless of current
 * `discovery_status`/staleness (a manual refresh, not gated on staleness). */
export function queueDiscover(targetId: string): Promise<DiscoverQueuedResponse> {
	return apiPost<DiscoverQueuedResponse>(`/targets/${targetId}/discover`);
}

/** The subset of `RunResponse` (RunContracts.cs) the discovery-poll needs —
 * state plus the failure job count, so a failed discover job (which can fail
 * before `targets.discovery_status` itself updates, per #557/#556) is
 * detectable from the run alone without a second jobs fetch on every poll
 * tick. */
export interface DiscoveryRunStatus {
	id: string;
	state: "pending" | "running" | "completed" | "completed_with_failures" | "aborted" | string;
	job_count_failed: number;
	job_count_blocked: number;
}

export function fetchDiscoveryRun(runId: string): Promise<DiscoveryRunStatus> {
	return apiGet<DiscoveryRunStatus>(`/runs/${runId}`);
}

/** Run states with no further transition (RunContracts.cs / api-contract.md's
 * run state machine) — polling stops once the run lands on one of these. */
export function isTerminalRunState(state: string): boolean {
	return state === "completed" || state === "completed_with_failures" || state === "aborted";
}

/** `/credentials` — id/name/type, per docs/api-contract.md's data ledger
 * ("`/credentials` (names only)"). The full response carries more (health,
 * ...) but the picker only ever reads `id`/`name`/`credential_type` (the
 * last purely to filter purpose-compatible options, issue #584); it must
 * never request or render secret material (there is none to request — the
 * backend's CredentialResponse has no secret field at all). */
export interface CredentialOption {
	id: string;
	name: string;
	credential_type: string;
}

/** Every well-known Broadcom depot credential type (issue #690) — never selectable from a target/scan-wizard credential picker. */
const DEPOT_CREDENTIAL_TYPES: ReadonlySet<string> = new Set(["depot-token", "depot-activation-code", "legacy-download-token"]);

/**
 * Issue #571/#690 (#560 AC: "Depot tokens do not appear in target credential
 * selectors"): `GET /credentials` returns every stored credential, including
 * the Broadcom depot rows (the well-known credentials `CatalogIndexJobHandler`/
 * `ManagedToolInstallJobHandler` resolve by type — see credentials.ts's
 * `CREDENTIAL_TYPES` doc comment). None of them has a dialable host or is one
 * of domain-model.md's four connection types, so none must ever be selectable
 * from a target/scan-wizard credential picker. Filtered client-side rather
 * than with a server-side query param: the full list is already fetched
 * unpaginated (this endpoint has no pagination), so a second round trip or a
 * new backend contract would add nothing.
 */
export function fetchCredentialOptions(): Promise<CredentialOption[]> {
	return apiGet<CredentialOption[]>("/credentials").then((credentials) =>
		credentials
			.filter((c) => !DEPOT_CREDENTIAL_TYPES.has(c.credential_type))
			.map((c) => ({ id: c.id, name: c.name, credential_type: c.credential_type })),
	);
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
