/**
 * Dashboard data layer (issue #513, prototype screen 2) — `GET /api/v1/dashboard`
 * (`DashboardController`/`DashboardResponse`, DashboardContracts.cs). Field names
 * match the wire exactly (snake_case), same convention as `results.ts`.
 *
 * This module deliberately does NOT fetch appliance version/mode/update info or
 * schedules — those come from `useSystem()` (already-running `/system` poll) and
 * `fetchSchedules()` (`lib/schedules.ts`) respectively; see `DashboardResponse`'s
 * backend doc comment for why the aggregate excludes what those two already answer.
 */
import { apiGet } from "../../lib/api";

export interface DashboardTargets {
	total: number;
	site_count: number;
	by_kind: Record<string, number>;
}

export interface DashboardFindings {
	counts_available: boolean;
	cat_i_open: number;
	cat_ii_open: number;
	cat_iii_open: number;
}

export interface DashboardSite {
	id: string;
	name: string;
	target_count: number;
	compliance_percent: number | null;
	cat_i_open: number | null;
	cat_ii_open: number | null;
	cat_iii_open: number | null;
	last_scan_at: string | null;
}

export interface DashboardRun {
	id: string;
	run_type: string;
	state: string;
	scope: string;
	job_count: number;
	created_at: string;
}

/** `kind` is one of `credential_auth_failing`, `inventory_stale`, or
 * `depot_token_expiring` — the last is a dormant signal type today (never
 * appears on the wire) per `DashboardAttentionResponse`'s backend doc comment.
 * Unattested CAT I is explicitly OUT of scope here (tracked in #514). */
export interface DashboardAttentionItem {
	kind: string;
	text: string;
	meta: string;
}

export interface DashboardData {
	targets: DashboardTargets;
	findings: DashboardFindings;
	sites: DashboardSite[];
	recent_runs: DashboardRun[];
	attention: DashboardAttentionItem[];
}

export function fetchDashboard(): Promise<DashboardData> {
	return apiGet<DashboardData>("/dashboard");
}

/** Site name out of a run's `scope` JSON string — same shape/defensiveness as
 * `results.ts`'s `scopeSiteId` (scope is free-form per api-contract.md, a
 * parse failure must never break the dashboard). */
export function scopeSiteId(scope: string): string | null {
	try {
		const parsed = JSON.parse(scope) as { site_id?: unknown };
		return typeof parsed.site_id === "string" ? parsed.site_id : null;
	} catch {
		return null;
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
	return date.toISOString().slice(0, 19).replace("T", " ") + "Z";
}

/** Run-state -> RECENT RUNS dot color token, mirroring `ResultsScreen`'s
 * `results__run-dot--{state}` palette (ok/warn/bad/accent by state). */
export function runStateTone(state: string): "ok" | "warn" | "bad" | "accent" | "muted" {
	switch (state) {
		case "completed":
			return "ok";
		case "completed_with_failures":
			return "warn";
		case "aborted":
			return "bad";
		case "running":
			return "accent";
		default:
			return "muted";
	}
}
