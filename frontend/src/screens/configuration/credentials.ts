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
 */

import { apiDelete, apiGet, apiPost, apiPut } from "../../lib/api";

export type CredentialType = "vcenter" | "nsx" | "ssh" | "token";

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
}

export function fetchCredentials(): Promise<Credential[]> {
	return apiGet<Credential[]>("/credentials");
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

export interface CredentialTestResult {
	id: string;
	succeeded: boolean;
	health: CredentialHealth | string;
	message: string;
}

export function testCredential(id: string): Promise<CredentialTestResult> {
	return apiPost<CredentialTestResult>(`/credentials/${id}/test`);
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
