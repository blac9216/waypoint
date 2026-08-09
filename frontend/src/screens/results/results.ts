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
 *      attestations-applied: `GET /runs/{id}/artifacts`,
 *      `GET /jobs/{id}/artifacts/{kind}`, and
 *      `GET /runs/{id}/attestations-applied` are implemented
 *      (`RunsController`/`JobsController`, issue #299/PR #305, PR #336). The
 *      wire shapes below (`RunArtifactRow`, `AppliedAttestation`) are matched
 *      field-for-field against `RunArtifactResponse`/`AppliedAttestationResponse`
 *      (RunContracts.cs), not guessed — see each interface's own doc comment
 *      for the subtleties that don't show up in a casual read of the issue:
 *      `counts_available` gating on artifacts, and `attestations-applied`
 *      being a persisted, at-scan-time ledger (issue #306/PR #336).
 *
 *   Attestations-applied: `fetchAttestationsApplied` calls the real
 *   endpoint, which reads a **persisted, per-target snapshot** written the
 *   instant the attest stage resolved each scanned target's attestation
 *   (`attestation_snapshots`, backend migration 0021) — never a live
 *   re-resolution. A historical run's answer is therefore immutable:
 *   editing the underlying config-doc afterward does not change what this
 *   endpoint reports for that run. `applied_at` is the genuine scan-time
 *   timestamp the snapshot was recorded at; `derivation`/`resolved_at` (the
 *   old live-resolution markers, issue #299/#307) are gone from the wire.
 *   `fetchAttestationResolution`/`ConfigDocResolution` stay as-is below:
 *   `ResultsScreen.tsx`'s sidebar still uses them for the "which are
 *   currently expired" slice, alongside the real endpoint.
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

/** One target row of `GET /runs/{id}/artifacts` (`RunArtifactResponse`,
 * RunContracts.cs — issue #299/#305). Field names and nullability match the
 * wire exactly (snake_case, per `lib/api.ts`'s "kept snake_case in the TS
 * types too, deliberately" convention) rather than being translated to
 * camelCase at this boundary.
 *
 * Severity counts are OPEN findings only, matching the prototype's
 * per-target table and the KPI tiles above it. `counts_available` gates the
 * three CAT counts: `false` means the HDF was absent or unparseable and the
 * counts are omitted from the response entirely (`undefined` here, not
 * `0`) — a consumer MUST check `counts_available` before trusting them, and
 * MUST NOT render an absent count as `0`, which would read as a
 * clean/compliant row instead of "could not count" (api-contract.md
 * "countability is explicit", #299 round-1 review blocker). */
export interface RunArtifactRow {
	job_id: string;
	target: string;
	benchmark: string | null;
	counts_available: boolean;
	cat_i_open?: number;
	cat_ii_open?: number;
	cat_iii_open?: number;
	/** Available artifact kinds for this target, e.g. `["ckl", "hdf"]` —
	 * reflects file *presence* on disk, independent of `counts_available`
	 * (a present-but-corrupt HDF still lists `"hdf"` here). */
	artifact_kinds: string[];
	/** STIG Manager upload status. The endpoint never returns `"conflict"`
	 * today (RunArtifactResponse's doc comment: only a real upload attempt
	 * can report that) but the type stays a superset of what's possible so a
	 * future real conflict isn't a breaking type change. */
	upload_status: "uploaded" | "not-uploaded" | "conflict" | "pending";
}

export function fetchRunArtifacts(runId: string): Promise<RunArtifactRow[]> {
	return apiGet<RunArtifactRow[]>(`/runs/${runId}/artifacts`);
}

/** `GET /jobs/{id}/artifacts/{kind}` — CKL/HDF download route. Returns the
 * URL to hand to an `<a>`/`fetch`, not the bytes — callers decide whether to
 * navigate or fetch-and-zip. */
export function artifactDownloadUrl(jobId: string, kind: "ckl" | "hdf"): string {
	return `/api/v1/jobs/${jobId}/artifacts/${kind}`;
}

/** `GET /runs/{id}/artifacts?bundle=zip` — the server-side bundle route the
 * contract documents (PR #305 did not implement it; the frontend's
 * client-side `zip.ts` covers the export button today). Not used by
 * `downloadCklBundle` in `ResultsScreen.tsx`; kept here as the eventual
 * replacement so a future swap is one line, not a new fetch shape. */
export function runArtifactsBundleUrl(runId: string): string {
	return `/api/v1/runs/${runId}/artifacts?bundle=zip`;
}

/** One row of `GET /runs/{id}/attestations-applied`
 * (`AppliedAttestationResponse`, RunContracts.cs — issue #299/#305, wire
 * shape updated by issue #306/PR #336).
 *
 * Two things a naive read of the issue misses:
 *  - `scope` is the same `layer`/`layer:{ref}` form `/config-docs/resolve`
 *    already emits (`"global"`, or `"site:{guid}"`/`"target:{guid}"`), not a
 *    bare `"global" | "site" | "target"` union — see `parseAttestationScope`
 *    below for the display split.
 *  - This is **persisted, at-scan-time history, not live resolution**: a
 *    per-target snapshot (`attestation_snapshots`, backend migration 0021)
 *    is written the instant the attest stage resolves each scanned target's
 *    attestation, and this endpoint reads that recorded snapshot back —
 *    never re-resolving live. `applied_at` is therefore the genuine
 *    scan-time timestamp the snapshot was recorded at, and is immutable for
 *    a given run regardless of any later edit to the underlying config-doc.
 *    The old `derivation`/`resolved_at` live-resolution markers (issue
 *    #299/#307) are gone from the wire. `attestation_updated_at` keeps its
 *    prior meaning — the config-doc version's own last-modified time,
 *    optional — but is now read from the snapshot rather than a live
 *    lookup; it still answers a different question than `applied_at` (doc
 *    edit time vs. scan-time application time) even though both are now
 *    real recorded facts.
 */
export interface AppliedAttestation {
	control: string;
	/** `"global"` or `"{layer}:{ref-guid}"` — see `parseAttestationScope`. */
	scope: string;
	coverage: string;
	justification: string;
	author: string;
	version: number;
	/** The genuine scan-time timestamp this snapshot was recorded at
	 * (ISO-8601) — always present, immutable for a given run. See the
	 * interface doc comment. */
	applied_at: string;
	/** The attestation config-doc version's own last-modified time, if it
	 * has one — NOT when it was applied to any scan. */
	attestation_updated_at?: string;
	expired: boolean;
}

export function fetchAttestationsApplied(runId: string): Promise<AppliedAttestation[]> {
	return apiGet<AppliedAttestation[]>(`/runs/${runId}/attestations-applied`);
}

/** Splits an `AppliedAttestation.scope` (`"global"` or `"{layer}:{ref}"`,
 * the same form `ConfigDocContracts.cs`'s `FormatLayer` produces for
 * `/config-docs` `layer`) into a display-friendly `{ layer, ref }` pair.
 * `ref` is `null` for the global layer, which carries no ref. Defensive
 * like `scopeSiteId` below: an unrecognized shape falls back to treating the
 * whole string as the layer with no ref, rather than throwing and blanking
 * the sidebar over one malformed row. */
export function parseAttestationScope(scope: string): { layer: string; ref: string | null } {
	const separatorIndex = scope.indexOf(":");
	if (separatorIndex === -1) {
		return { layer: scope, ref: null };
	}
	return { layer: scope.slice(0, separatorIndex), ref: scope.slice(separatorIndex + 1) };
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
