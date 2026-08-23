/**
 * Config → Depot & Tokens data layer (issue #571, completing #560/#39's
 * frontend half against the backend landed in PR #570 and PR #602).
 *
 * Three backend surfaces feed this one tab:
 *
 *   GET/POST       /credentials              — depot-token is a real
 *                                               `credential_type` (excluded
 *                                               from the *creatable* dropdown
 *                                               in credentials.ts, not from
 *                                               the wire type) so this module
 *                                               reuses credentials.ts's
 *                                               create/update/test/list
 *                                               functions directly rather
 *                                               than re-implementing them.
 *   GET            /downloads/readiness       — combined tool+token readiness
 *                                               (PR #570).
 *   POST           /downloads/tool/install    — local-repository install path
 *   POST           /downloads/tool/upload     — manual upload (multipart +
 *                                               mandatory detached .sig)
 *   GET            /downloads/tool/installs   — install ledger, newest first,
 *                                               including rejected attempts
 *
 * Every nullable field on the wire is OMITTED when null (global
 * `WhenWritingNull` policy, confirmed against `DownloadsApiTests.cs`) — never
 * sent as an explicit `null`. That is why every optional field below is typed
 * `T | undefined`, not `T | null`, and why this module never treats "absent"
 * and "false" as the same thing: `tool_installed` absent means "no
 * download-runner has ever reported," `false` means a real negative, `true`
 * means installed. Same shape for `depot_token_health` (absent means "no
 * depot-token credential row exists at all," not "unhealthy").
 */

import { apiGet, apiPost, apiPostForm } from "../../lib/api";

/** `GET /downloads/readiness` (`DownloadReadinessResponse`, PR #570). */
export interface DownloadReadiness {
	ready: boolean;
	depot_token_configured: boolean;
	/** Omitted when no depot-token credential exists at all — distinct from "unknown"/"valid"/"auth_failing", which are real values once a credential exists. */
	depot_token_health?: "unknown" | "valid" | "auth_failing" | string;
	/** Omitted (not `false`) when no download-runner heartbeat has ever reported tool presence — genuinely unknown, never inferred. */
	tool_installed?: boolean;
	/** `"depot_token"` | `"depot_token_auth_failing"` (mutually exclusive) | `"tool_not_installed"`. */
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

/** Manual upload: artifact + mandatory detached `.sig` (issue #39 AC — an unsigned upload is never accepted, even before verification runs). Field names (`artifact`, `signature`, `version`) match `ManagedToolController.Upload`'s parameter names exactly. */
export function uploadManagedTool(artifact: File, signature: File, version?: string): Promise<ManagedToolInstallQueuedResponse> {
	const form = new FormData();
	form.set("artifact", artifact);
	form.set("signature", signature);
	if (version) {
		form.set("version", version);
	}
	return apiPostForm<ManagedToolInstallQueuedResponse>("/downloads/tool/upload", form);
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
