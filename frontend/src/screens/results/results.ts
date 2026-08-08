/**
 * Results & History data layer (issue #27) — docs/ui/prototype/README.md
 * screen 4, against docs/api-contract.md's "Runs & jobs" and "Config
 * documents" rows. Two sub-surfaces:
 *
 *   1. Run list + detail: `GET /runs` (paginated, newest-first — merged per
 *      issue #210) and `GET /runs/{id}` + `GET /runs/{id}/jobs`, all already
 *      implemented (`RunsController`). These are the same `RunResponse`/
 *      `JobResponse` wire shapes `liverun.ts` consumes, kept as separate
 *      types here rather than imported: this screen only reads a handful of
 *      fields from each and has no SSE/state-machine coupling to
 *      `liverun.ts`'s reducer, so sharing the type would just be an
 *      incidental coupling between two independently-evolving screens.
 *
 *   2. Per-target artifacts, STIG Manager upload status, and
 *      attestations-applied: `docs/api-contract.md` documents
 *      `/runs/{id}/artifacts`, `/jobs/{id}/artifacts/{kind}`, and
 *      `/runs/{id}/attestations-applied`, but none of the three exist on the
 *      backend yet (`RunsController`/`JobsController` only implement
 *      list/detail/jobs/pause/resume/abort/resume-blocked/cancel as of this
 *      PR — see their doc comments). This module still defines the fetch
 *      functions against the documented shapes so the screen is ready the
 *      moment the backend lands them (same "build against the contract"
 *      convention `startscan.ts` and `catalog.ts` already use), and the
 *      screen degrades to an explicit "not available yet" empty state
 *      instead of crashing when the calls 404. Tracked as issue #299
 *      (backend REST surface for these three routes; blocked on #275
 *      landing artifact persistence first).
 *
 *   Attestations-applied specifically: the full `/runs/{id}/attestations-applied`
 *   endpoint (control, scope, justification, author/version, expired-skips)
 *   does not exist yet either (also #299). What DOES exist and is merged is
 *   `GET /config-docs/resolve?profile&target&kind=attestation`
 *   (`ConfigDocsController.Resolve`, issue #266), which returns
 *   `attestation_expired`/`attestation_expires_at` per (profile, target).
 *   `fetchAttestationResolution` uses that endpoint as a partial substitute
 *   in `ResultsScreen.tsx` — it can show which target/profile pairs
 *   currently resolve to an expired waiver, but not the full per-control
 *   waiver ledger #299/#275 will eventually populate `attestations-applied`
 *   from. This is a deliberate, narrower stand-in, not a guess at their
 *   shape.
 */
import { apiGet } from "../../lib/api";

/** `RunResponse` (RunContracts.cs) — this screen's subset. */
export interface RunListItem {
	id: string;
	run_type: string;
	state: "pending" | "running" | "completed" | "completed_with_failures" | "aborted" | string;
	scope: string;
	initiated_by: string | null;
	created_at: string;
	started_at: string | null;
	completed_at: string | null;
	job_count: number;
	job_count_queued: number;
	job_count_running: number;
	job_count_completed: number;
	job_count_failed: number;
	job_count_blocked: number;
}

export interface RunListResult {
	items: RunListItem[];
	/** Total row count across all pages. `lib/api.ts`'s `apiFetch` deliberately
	 * has no `X-Total-Count` support yet (its own note: "Add it with a real
	 * endpoint, not before" — this is that endpoint) and returning only the
	 * parsed JSON body throws the header away before this module ever sees
	 * it. This screen has no "load more"/pager UI yet (50-row single page is
	 * enough for M2), so `items.length` is used as a placeholder rather than
	 * plumbing header access through `apiFetch` for a value nothing reads;
	 * revisit alongside `apiFetch` if/when a paged list UI needs the real
	 * total. */
	totalCount: number;
}

/** `GET /runs` — Viewer+, paginated (issue #210). Newest-first. Goes through
 * `apiGet` like every other read in this codebase (bearer token, `ApiError`
 * normalization) rather than a raw `fetch` — see `RunListResult.totalCount`
 * for why the `X-Total-Count` header isn't read here. */
export async function fetchRunList(limit = 50, offset = 0): Promise<RunListResult> {
	const items = await apiGet<RunListItem[]>(`/runs?limit=${limit}&offset=${offset}`);
	return { items, totalCount: items.length };
}

export function fetchRun(runId: string): Promise<RunListItem> {
	return apiGet<RunListItem>(`/runs/${runId}`);
}

/** `JobResponse` (RunContracts.cs) — this screen's subset. */
export interface RunJobItem {
	id: string;
	job_type: string;
	target_id: string | null;
	target_name: string | null;
	state: string;
	stage: string | null;
	attempt_count: number;
	created_at: string;
	started_at: string | null;
	finished_at: string | null;
}

export function fetchRunJobs(runId: string): Promise<RunJobItem[]> {
	return apiGet<RunJobItem[]>(`/runs/${runId}/jobs`);
}

/** CAT I/II/III severity, spelled out — never abbreviated to a bare "I"/"II"/"III"
 * without the full word available (design-brief "Layout Rules Learned the Hard
 * Way" #4: a clipped "CAT II" reading as "CAT I" is a correctness bug, not a
 * cosmetic one). */
export type Severity = "CAT I" | "CAT II" | "CAT III";

export const SEVERITIES: Severity[] = ["CAT I", "CAT II", "CAT III"];

/** One target row of `GET /runs/{id}/artifacts` (documented, not yet
 * implemented — see module doc). Severity counts are OPEN findings only,
 * matching the prototype's per-target table and the KPI tiles above it. */
export interface RunArtifactRow {
	target: string;
	benchmark: string;
	catIOpen: number;
	catIIOpen: number;
	catIIIOpen: number;
	/** Available artifact kinds for this target, e.g. `["ckl", "hdf"]`. */
	artifactKinds: string[];
	/** STIG Manager upload status, derived from job state at render time by
	 * `stigManagerStatus` below when the endpoint's own status field is
	 * absent — kept as an explicit union so a stub/placeholder value can't
	 * silently pass through as a real status. */
	uploadStatus: "uploaded" | "not-uploaded" | "conflict" | "pending";
}

export function fetchRunArtifacts(runId: string): Promise<RunArtifactRow[]> {
	return apiGet<RunArtifactRow[]>(`/runs/${runId}/artifacts`);
}

/** `GET /jobs/{id}/artifacts/{kind}` — CKL/HDF download route (documented,
 * not yet implemented). Returns the URL to hand to an `<a>`/`fetch`, not the
 * bytes — callers decide whether to navigate or fetch-and-zip. */
export function artifactDownloadUrl(jobId: string, kind: "ckl" | "hdf"): string {
	return `/api/v1/jobs/${jobId}/artifacts/${kind}`;
}

/** `GET /runs/{id}/artifacts?bundle=zip` — the server-side bundle route the
 * contract documents. Not used by `exportCklBundle` below (that builds the
 * zip client-side per the issue's AC, since the backend route doesn't exist
 * yet either); kept here as the eventual replacement once it lands, so the
 * follow-up is a one-line swap rather than a new fetch shape. */
export function runArtifactsBundleUrl(runId: string): string {
	return `/api/v1/runs/${runId}/artifacts?bundle=zip`;
}

/** `GET /runs/{id}/attestations-applied` (documented, not yet implemented — issue #299). */
export interface AppliedAttestation {
	control: string;
	scope: "global" | "site" | "target";
	coverage: string;
	justification: string;
	author: string;
	version: number;
	applied_at: string;
	expired: boolean;
}

export function fetchAttestationsApplied(runId: string): Promise<AppliedAttestation[]> {
	return apiGet<AppliedAttestation[]>(`/runs/${runId}/attestations-applied`);
}

/** `ConfigDocResolutionResponse` (ConfigDocContracts.cs) — the merged,
 * narrower substitute described in the module doc. Only the fields this
 * screen reads. */
export interface ConfigDocResolution {
	kind: string;
	profile: string;
	layer: string | null;
	doc_id: string | null;
	version: number | null;
	author: string | null;
	updated_at: string | null;
	attestation_expired: boolean;
	attestation_expires_at: string | null;
}

/**
 * `GET /config-docs/resolve?profile&target&kind=attestation` for one
 * (profile, target) pair — issue #266, merged. Used as today's stand-in for
 * the expired-skips slice of the sidebar until `/runs/{id}/attestations-applied`
 * lands; see module doc.
 */
export function fetchAttestationResolution(profile: string, targetId: string): Promise<ConfigDocResolution[]> {
	return apiGet<ConfigDocResolution[]>(
		`/config-docs/resolve?profile=${encodeURIComponent(profile)}&target=${encodeURIComponent(targetId)}&kind=attestation`,
	);
}

/**
 * Derives the STIG Manager upload display status from job state, the same
 * "state model drives presentation" convention `LiveRunScreen`'s
 * `stateColor` uses. An upload happens after a job reaches `uploaded`; prior
 * states read as "pending" (nothing has been attempted yet), and `failed`/
 * `auth-failed` read as `not-uploaded` rather than a false "conflict" — a
 * true 409 conflict is a distinct outcome this function cannot see from job
 * state alone and is left to the (not yet implemented) artifacts endpoint's
 * own `uploadStatus` field.
 */
export function stigManagerStatusLabel(state: string): string {
	switch (state) {
		case "uploaded":
		case "done":
			return "uploaded";
		case "failed":
		case "auth-failed":
			return "not-uploaded";
		default:
			return "pending";
	}
}

export function formatRunDuration(startedAt: string | null, completedAt: string | null): string {
	if (!startedAt) {
		return "—";
	}
	const start = new Date(startedAt).getTime();
	const end = completedAt ? new Date(completedAt).getTime() : Date.now();
	if (Number.isNaN(start) || Number.isNaN(end) || end < start) {
		return "—";
	}
	const totalSeconds = Math.floor((end - start) / 1000);
	const minutes = Math.floor(totalSeconds / 60);
	const seconds = totalSeconds % 60;
	return `${minutes}m ${String(seconds).padStart(2, "0")}s`;
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

/** Site name out of a run's `scope` JSON string (Start-a-Scan's wire shape:
 * `{"site_id": "..."}`  — no site NAME is carried in scope, only the id, so
 * this can only ever surface the id today). Kept small and defensive: scope
 * is free-form per api-contract.md ("scope... uninterpreted" for non-scan
 * run types), so a parse failure must never break the run list. */
export function scopeSiteId(scope: string): string | null {
	try {
		const parsed = JSON.parse(scope) as { site_id?: unknown };
		return typeof parsed.site_id === "string" ? parsed.site_id : null;
	} catch {
		return null;
	}
}
