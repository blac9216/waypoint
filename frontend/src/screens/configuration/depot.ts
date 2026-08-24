/**
 * Config → Depot & Tokens data layer (issue #571, completing #560/#39's
 * frontend half against the backend landed in PR #570 and PR #602; issue #690
 * split the single depot-token concept into two independent credentials).
 *
 * Three backend surfaces feed this one tab:
 *
 *   GET/POST       /credentials              — depot-activation-code and
 *                                               legacy-download-token are real
 *                                               `credential_type`s (excluded
 *                                               from the *creatable* dropdown
 *                                               in credentials.ts, not from
 *                                               the wire type) so this module
 *                                               reuses credentials.ts's
 *                                               create/update/test/list
 *                                               functions directly rather
 *                                               than re-implementing them.
 *   GET            /downloads/readiness       — combined tool+credential
 *                                               readiness, reported per
 *                                               credential (PR #570, #690).
 *   POST           /downloads/tool/install    — local-repository install path
 *   POST           /downloads/tool/upload     — manual upload (multipart +
 *                                               published checksum)
 *   POST           /downloads/tool/fetch      — depot-fetch install path
 *                                               (connected mode only — the
 *                                               backend refuses with 409
 *                                               mode_unavailable otherwise,
 *                                               issue #39 remainder)
 *   GET            /downloads/tool/installs   — install ledger, newest first,
 *                                               including rejected attempts
 *
 * Every nullable field on the wire is OMITTED when null (global
 * `WhenWritingNull` policy, confirmed against `DownloadsApiTests.cs`) — never
 * sent as an explicit `null`. That is why every optional field below is typed
 * `T | undefined`, not `T | null`, and why this module never treats "absent"
 * and "false" as the same thing: `tool_installed` absent means "no
 * download-runner has ever reported," `false` means a real negative, `true`
 * means installed. Same shape for `activation_code_health`/
 * `legacy_download_token_health` (absent means "no such credential row exists
 * at all," not "unhealthy").
 */

import { apiGet, apiPost, apiPostForm } from "../../lib/api";

/** `GET /downloads/readiness` (`DownloadReadinessResponse`, PR #570, extended by issue #690). */
export interface DownloadReadiness {
	ready: boolean;
	activation_code_configured: boolean;
	/** Omitted when no depot-activation-code credential exists at all — distinct from "unknown"/"valid"/"auth_failing", which are real values once a credential exists. */
	activation_code_health?: "unknown" | "valid" | "auth_failing" | string;
	legacy_download_token_configured: boolean;
	/** Omitted when no legacy-download-token credential exists at all. Never gates `ready` — reported for visibility only (issue #690). */
	legacy_download_token_health?: "unknown" | "valid" | "auth_failing" | string;
	/** Omitted (not `false`) when no download-runner heartbeat has ever reported tool presence — genuinely unknown, never inferred. */
	tool_installed?: boolean;
	/** `"activation_code"` | `"activation_code_auth_failing"` (mutually exclusive) | `"tool_not_installed"`. */
	missing_prerequisites: string[];
}

export function fetchDownloadReadiness(): Promise<DownloadReadiness> {
	return apiGet<DownloadReadiness>("/downloads/readiness");
}

/** `POST /downloads/tool/install` and `POST /downloads/tool/upload` both return this (`ManagedToolInstallQueuedResponse`) — string ids, not raw GUIDs, unlike `CredentialTestQueuedResponse`. */
export interface ManagedToolInstallQueuedResponse {
	run_id: string;
	job_id: string;
}

export function installManagedToolFromLocalRepository(sourcePath: string, version?: string): Promise<ManagedToolInstallQueuedResponse> {
	return apiPost<ManagedToolInstallQueuedResponse>("/downloads/tool/install", {
		source_path: sourcePath,
		version: version || undefined,
	});
}

/** Manual upload with SHA-256 preferred and MD5 accepted only as legacy integrity. */
export function uploadManagedTool(
	artifact: File,
	checksums: { sha256?: string; md5?: string },
	version?: string,
): Promise<ManagedToolInstallQueuedResponse> {
	const form = new FormData();
	form.set("artifact", artifact);
	if (checksums.sha256) form.set("sha256", checksums.sha256);
	if (checksums.md5) form.set("md5", checksums.md5);
	if (version) {
		form.set("version", version);
	}
	return apiPostForm<ManagedToolInstallQueuedResponse>("/downloads/tool/upload", form);
}

/** `POST /downloads/tool/fetch` (issue #39 remainder): connected-mode-only depot fetch, authenticated with the stored depot-token credential. No `source_path` — the depot URL is server-side configuration, not something the client names. */
export function fetchManagedToolFromDepot(version?: string): Promise<ManagedToolInstallQueuedResponse> {
	return apiPost<ManagedToolInstallQueuedResponse>("/downloads/tool/fetch", {
		version: version || undefined,
	});
}

export type ManagedToolInstallOutcome = "installed" | "rejected" | "failed" | string;

/** One row of `GET /downloads/tool/installs` (`ManagedToolInstallResponse`), newest first, including rejected attempts (issue #39 AC). */
export interface ManagedToolInstall {
	id: string;
	source: "local-repository" | "upload" | "depot" | string;
	source_path: string;
	version?: string;
	sha256?: string;
	outcome: ManagedToolInstallOutcome;
	/** Present only for a rejected/failed outcome. */
	rejected_reason?: string;
	initiated_by: string;
	created_at: string;
}

export function fetchManagedToolInstalls(): Promise<ManagedToolInstall[]> {
	return apiGet<ManagedToolInstall[]>("/downloads/tool/installs");
}

export function formatManagedToolOutcome(outcome: string): string {
	switch (outcome) {
		case "installed":
			return "installed";
		case "rejected":
			return "rejected";
		case "failed":
			return "failed";
		default:
			return outcome;
	}
}

export function formatSource(source: string): string {
	switch (source) {
		case "local-repository":
			return "local repository";
		case "upload":
			return "manual upload";
		case "depot":
			return "depot fetch";
		default:
			return source;
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
