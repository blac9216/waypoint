/**
 * Config → Compliance Content data layer (issue #40, frontend slice 2 of 2 —
 * backend landed in PR #566). Wraps `docs/api-contract.md`'s
 * `/compliance-content` surface:
 *
 *   GET  /compliance-content        — singleton repo/ref config + last pull
 *   PUT  /compliance-content        — Admin: set repository_url/ref_type/ref_value
 *   GET  /compliance-content/pulls  — pull history (who/when/commit/state), newest first
 *   POST /compliance-content/pull   — Admin: 202, fans a `content-pull` job
 *   GET  /profiles                  — inventory that also feeds Benchmarks (#559)
 *
 * `POST /compliance-content/check` (upstream diff) and
 * `POST /compliance-content/import` (air-gapped bundle import) are explicitly
 * out of scope for the backend slice (see `ComplianceContentController`'s
 * doc comment) — there is no "update available" signal from the repo config
 * itself. The one real update signal on the wire is a profile inventory row
 * whose `state` is `update_pending` (`ProfileStates.UpdatePending`), so the
 * banner in `ComplianceContentTab` is derived from that instead of a
 * changelog field that does not exist yet.
 */

import { apiGet, apiPost, apiPut } from "../../lib/api";

export type ComplianceContentRefType = "tag" | "branch";

export interface ComplianceContentConfig {
	repository_url: string;
	ref_type: ComplianceContentRefType | string;
	ref_value: string;
	pulled_commit: string | null;
	pulled_by: string | null;
	pulled_at: string | null;
	created_at: string;
	updated_at: string;
}

export interface ComplianceContentWriteInput {
	repository_url: string;
	ref_type: ComplianceContentRefType;
	ref_value: string;
}

export function fetchComplianceContentConfig(): Promise<ComplianceContentConfig> {
	return apiGet<ComplianceContentConfig>("/compliance-content");
}

export function putComplianceContentConfig(input: ComplianceContentWriteInput): Promise<ComplianceContentConfig> {
	return apiPut<ComplianceContentConfig>("/compliance-content", input);
}

/** One row of `GET /compliance-content/pulls` — who/when/commit/state (issue #40 AC). */
export interface ComplianceContentPull {
	id: string;
	job_id: string | null;
	ref_type: string;
	ref_value: string;
	commit: string | null;
	status: "succeeded" | "failed" | string;
	note: string | null;
	initiated_by: string | null;
	created_at: string;
}

export function fetchComplianceContentPulls(): Promise<ComplianceContentPull[]> {
	return apiGet<ComplianceContentPull[]>("/compliance-content/pulls");
}

/** 202 response for `POST /compliance-content/pull` (`ContentPullStartedResponse`). */
export interface ContentPullStartedResponse {
	run_id: string;
}

export function startContentPull(): Promise<ContentPullStartedResponse> {
	return apiPost<ContentPullStartedResponse>("/compliance-content/pull");
}

/** The subset of `RunResponse` the pull-status poll needs — same shape as
 * `sites.ts`'s `DiscoveryRunStatus`, reused independently here since the two
 * modules intentionally don't share a runtime dependency. */
export interface ContentPullRunStatus {
	id: string;
	state: "pending" | "running" | "completed" | "completed_with_failures" | "aborted" | string;
	job_count_failed: number;
	job_count_blocked: number;
}

export function fetchContentPullRun(runId: string): Promise<ContentPullRunStatus> {
	return apiGet<ContentPullRunStatus>(`/runs/${runId}`);
}

/** Run states with no further transition — polling stops here (mirrors
 * `sites.ts`'s `isTerminalRunState`). */
export function isTerminalRunState(state: string): boolean {
	return state === "completed" || state === "completed_with_failures" || state === "aborted";
}

/** Profile inventory row (`GET /profiles`, `ProfileResponse`) — drives both
 * this tab's inventory table and the Benchmarks profile list (#559). */
export interface Profile {
	id: string;
	profile_key: string;
	name: string;
	version: string | null;
	commit: string;
	state: "current" | "update_pending" | "pinned" | string;
	updated_at: string;
}

export function fetchProfiles(): Promise<Profile[]> {
	return apiGet<Profile[]>("/profiles");
}

export function formatProfileState(state: string): string {
	switch (state) {
		case "current":
			return "current";
		case "update_pending":
			return "update pending";
		case "pinned":
			return "pinned";
		default:
			return state;
	}
}

export function formatPullStatus(status: string): string {
	switch (status) {
		case "succeeded":
			return "succeeded";
		case "failed":
			return "failed";
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
