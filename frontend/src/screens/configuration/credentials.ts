/**
 * Credentials data layer (docs/api-contract.md "Credentials (service/shared
 * only — ADR-0011)"), against the #20 backend
 * (`CredentialsController`/`CredentialDtos.cs`):
 *
 *   GET/POST       /credentials       — Viewer+ read, Admin write
 *   GET/PUT/DELETE /credentials/{id}
 *   POST           /credentials/{id}/test
 *
 * Write-only secrets (ADR-0005 / security.md control 3): `CredentialResponse`
 * has no secret field at all — `has_secret` is a boolean, never the material
 * itself — so there is nothing to accidentally round-trip. `secret` only ever
 * appears in the outgoing create/update request body, and only when the user
 * actually typed a new one; leaving the field blank on an edit omits it so
 * the stored secret is left untouched (mirrors sites.ts's optional-field PUT
 * convention).
 *
 * Owner is always `"shared"` (ADR-0011: no personal-credential rows) — the
 * form never collects it, and it is not sent on create.
 *
 * Issue #245/PR #322 changed `POST /credentials/{id}/test` from a synchronous
 * `{succeeded, health, message}` 200 to a `202 + {run_id, job_id}` — it now
 * queues a real `credential-test` job (`CredentialTestJobHandler`) instead of
 * answering inline. The job's terminal outcome is what flips
 * `credentials.health` server-side, not this call — so the caller (see
 * `CredentialsTab.tsx`'s `doTest`) must follow the job via the existing SSE
 * plumbing (`lib/events.ts` / the Live Run board's pattern) to its terminal
 * `job.state` and then re-fetch the credential to read the authoritative
 * `health` (issue #323).
 */

import { apiDelete, apiGet, apiPost, apiPut, type ApiErrorBlocker } from "../../lib/api";

/**
 * The full backend closed set (`Waypoint.Core.Secrets.CredentialTypes.All`,
 * migration 0022's DB CHECK) — includes `depot-token` so a fetched depot-token
 * credential (see `CREDENTIAL_TYPES` below) renders correctly in the list/type
 * column instead of falling through as an unrecognized type.
 */
export type CredentialType = "vcenter" | "nsx" | "ssh" | "token" | "depot-token";

/**
 * The CREATABLE subset, rendered in this tab's "Type" dropdown. Deliberately
 * excludes `depot-token`: per `CredentialTypes`' backend doc comment (#252/#383),
 * depot-token is the single well-known row `CatalogIndexJobHandler` resolves by
 * type to authenticate catalog-index runs to the Broadcom depot — it is not one
 * of domain-model.md's four user-facing connection types. It stays out of
 * this dropdown so an operator can't half-configure it here (no dialable
 * host, no username in the normal sense); depot-token creation/replacement
 * lives in its own dedicated flow — the Config → Depot & Tokens tab
 * (`DepotTokensTab.tsx`, issue #571) — not this connection-credential list.
 */
export const CREDENTIAL_TYPES: { value: CredentialType; label: string }[] = [
	{ value: "vcenter", label: "vCenter" },
	{ value: "nsx", label: "NSX" },
	{ value: "ssh", label: "SSH" },
	{ value: "token", label: "Token" },
];

export type CredentialHealth = "valid" | "auth_failing" | "unknown";

export interface Credential {
	id: string;
	name: string;
	credential_type: CredentialType | string;
	owner: string;
	health: CredentialHealth | string;
	sudo_enabled: boolean;
	has_secret: boolean;
	used_by_job_count: number;
	/** Omitted from the wire entirely (not `null`) until the credential's
	 * first secret write — the backend's `DefaultIgnoreCondition.WhenWritingNull`
	 * drops nullable fields rather than sending `null`. */
	rotated_at?: string;
	created_at: string;
	updated_at: string;
	/** Omitted from the wire until set — see `rotated_at`. */
	username?: string;
	/** Migration 0035 (PR #570): omitted until the first `credential-test` job
	 * outcome, any credential type, success or failure — see `rotated_at`. */
	last_tested_at?: string;
	/** Migration 0035 (PR #570): omitted until a real upstream response
	 * supplies one — nothing populates this yet for any credential type.
	 * Never rendered as a fabricated date when absent (issue #560/#571 AC) —
	 * see `DepotTokensTab.tsx`'s `ExpiryValue`. */
	expires_at?: string;
}

export function fetchCredentials(): Promise<Credential[]> {
	return apiGet<Credential[]>("/credentials");
}

export function fetchCredential(id: string): Promise<Credential> {
	return apiGet<Credential>(`/credentials/${id}`);
}

export interface CredentialWriteInput {
	name: string;
	credential_type: CredentialType;
	username: string;
	/** SSH-only; ignored (and never sent) for any other type. */
	sudo_enabled: boolean;
	/** Blank means "leave the stored secret alone" on edit, and "no initial
	 * secret" on create — never round-tripped from a GET, so there is no
	 * pre-fill case to guard against. */
	secret: string;
}

export function createCredential(input: CredentialWriteInput): Promise<Credential> {
	return apiPost<Credential>("/credentials", {
		name: input.name,
		credential_type: input.credential_type,
		username: input.username || undefined,
		sudo_enabled: input.credential_type === "ssh" ? input.sudo_enabled : undefined,
		secret: input.secret || undefined,
	});
}

export function updateCredential(id: string, input: CredentialWriteInput): Promise<Credential> {
	return apiPut<Credential>(`/credentials/${id}`, {
		name: input.name,
		username: input.username,
		sudo_enabled: input.credential_type === "ssh" ? input.sudo_enabled : undefined,
		secret: input.secret || undefined,
	});
}

export function deleteCredential(id: string): Promise<void> {
	return apiDelete<void>(`/credentials/${id}`);
}

/** `202` body from `POST /credentials/{id}/test` — one run containing one
 * `credential-test` job (`CredentialsController.Test`). Nothing about the
 * outcome is known yet; the caller follows `job_id` via SSE. */
export interface CredentialTestQueuedResponse {
	run_id: string;
	job_id: string;
}

export function testCredential(id: string): Promise<CredentialTestQueuedResponse> {
	return apiPost<CredentialTestQueuedResponse>(`/credentials/${id}/test`);
}

export function formatHealth(health: string): string {
	switch (health) {
		case "valid":
			return "valid";
		case "auth_failing":
			return "auth failing";
		case "unknown":
			return "unknown";
		default:
			return health;
	}
}

export function formatTimestamp(iso: string | null | undefined): string {
	if (!iso) {
		return "—";
	}
	const date = new Date(iso);
	if (Number.isNaN(date.getTime())) {
		return "—";
	}
	return date.toISOString().slice(0, 16).replace("T", " ") + "Z";
}

/**
 * Issue #593: one clause per `409 credential_in_use` blocking category
 * (`Waypoint.Core.Errors.BlockingCategory` on the wire, via `ApiErrorBlocker`).
 * Deliberately a closed switch, not a passthrough of the category string --
 * an unrecognized future category still renders (the `default` clause), but
 * every category this endpoint documents today gets accurate, count-aware
 * phrasing rather than a raw wire token like `active_jobs` in the UI.
 */
export function describeCredentialBlocker(blocker: ApiErrorBlocker): string {
	const { category, count } = blocker;
	const plural = count === 1 ? "" : "s";
	switch (category) {
		case "targets":
			return `${count} target${plural}`;
		case "schedules":
			return `${count} schedule${plural}`;
		case "configuration":
			return "the STIG Manager connection";
		case "active_jobs":
			return `${count} active job${plural}`;
		default:
			return `${count} ${category.replace(/_/g, " ")}`;
	}
}

/**
 * Issue #593 (epic #577): renders the `409 credential_in_use` body's
 * machine-readable `blockers` into the guidance the delete confirmation/error
 * surfaces — naming exactly what still references the credential (targets,
 * schedules, the STIG Manager connection, active jobs) instead of the old
 * blanket "history must be removed" text, which was actively wrong once
 * terminal history stopped blocking deletion. `blockers` is optional only
 * because an older/unpatched API build might omit it; the fallback keeps the
 * error non-crashing, not accurate.
 */
export function describeCredentialInUse(name: string, blockers: ApiErrorBlocker[] | undefined): string {
	if (!blockers || blockers.length === 0) {
		return `"${name}" is still referenced by live configuration or active work and cannot be deleted yet.`;
	}
	return `"${name}" is still referenced by ${blockers.map(describeCredentialBlocker).join(", ")} and cannot be deleted until those references are removed. Completed run/job history is not a blocker.`;
}
