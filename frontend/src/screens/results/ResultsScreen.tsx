/**
 * Results & History — docs/ui/prototype/README.md screen 4, the last screen
 * of milestone M2 (issue #27, part of epic #13). Two panes: a searchable run
 * history list (left rail) and a run detail pane (KPI tiles, per-target
 * artifacts table, attestations-applied + upload-status sidebar, export and
 * Remediate actions).
 *
 * Data sources — see results.ts's module doc for the full breakdown:
 *   - Run list/detail/jobs: `GET /runs`, `GET /runs/{id}`, `GET /runs/{id}/jobs`
 *     (all merged — RunsController).
 *   - Per-target artifacts + STIG Manager upload status:
 *     `GET /runs/{id}/artifacts` (documented, NOT YET IMPLEMENTED on the
 *     backend — tracked as issue #299). This screen calls it and renders a
 *     graceful "not available yet" empty state on failure rather than
 *     assuming success.
 *   - Attestations-applied: `GET /runs/{id}/attestations-applied` is also not
 *     yet implemented (#299). As a narrower stand-in, the sidebar instead
 *     calls the MERGED `GET /config-docs/resolve?profile&target&kind=attestation`
 *     (issue #266) once per distinct (profile, target) pair from the
 *     artifacts rows, and lists only the EXPIRED ones — the "expired-skips"
 *     half of the sidebar the issue calls out explicitly. The full applied-waiver
 *     ledger (control, justification, author/version for every waiver that
 *     fired, not just expired ones) has no backing endpoint yet; that gap is
 *     real follow-up (#299), not a shortcut — see the PR body.
 *
 * Severity labels (AC1, design-brief "Layout Rules Learned the Hard Way" #4):
 * always rendered as the full "CAT I"/"CAT II"/"CAT III" text, in a pill wide
 * enough for the longest label with no `text-overflow: ellipsis` — never
 * abbreviated to a bare Roman numeral. See ResultsScreen.test.tsx for the
 * non-truncation assertion.
 *
 * STIG Manager upload status + retry: derived from job state via
 * `stigManagerStatusLabel` (results.ts), matching `LiveRunScreen`'s
 * "state model drives presentation" convention. The retry button is a
 * visible-but-disabled stub (`roleGateProps`-style treatment, reason text
 * instead of a role) — actually retrying uploads needs the STIG Manager
 * integration (issue #25) to land first.
 *
 * Remediate: visible, disabled, Admin-gated via `roleGateProps` exactly like
 * the pre-existing placeholder in screens.tsx (which this component
 * replaces) — stubbed until M4 (epic #15).
 */
import { useCallback, useEffect, useMemo, useState } from "react";
import { ApiError } from "../../lib/api";
import { useAuth } from "../../lib/auth";
import { roleGateProps } from "../../lib/roles";
import {
	artifactDownloadUrl,
	fetchAttestationResolution,
	fetchRun,
	fetchRunArtifacts,
	fetchRunJobs,
	fetchRunList,
	formatRunDuration,
	formatTimestamp,
	scopeSiteId,
	SEVERITIES,
	stigManagerStatusLabel,
	type ConfigDocResolution,
	type RunArtifactRow,
	type RunJobItem,
	type RunListItem,
	type Severity,
} from "./results";
import { buildZip, type ZipEntry } from "./zip";
import "./ResultsScreen.css";

const SEVERITY_CLASS: Record<Severity, string> = {
	"CAT I": "results__severity--1",
	"CAT II": "results__severity--2",
	"CAT III": "results__severity--3",
};

/** Full-text severity pill (AC1 / layout rule 4): the label is always the
 * complete "CAT I"/"CAT II"/"CAT III" string, never a bare numeral, and the
 * pill has no `overflow:hidden`/`text-overflow:ellipsis` — see
 * ResultsScreen.test.tsx for the assertion this backs. */
function SeverityPill({ severity, count }: { severity: Severity; count: number }) {
	return (
		<span className={`results__severity ${SEVERITY_CLASS[severity]}`} title={`${severity} open: ${count}`}>
			{severity} <span className="mono">{count}</span>
		</span>
	);
}

interface ExpiredAttestation {
	target: string;
	profile: string;
	resolution: ConfigDocResolution;
}

/** Loads `run`, `jobs`, `artifacts` (best-effort), and the expired-skips
 * substitute for one run id. Kept as one hook (rather than three) because
 * every consumer of this screen wants the same combined loading/error state
 * — there is no case where only jobs or only artifacts is useful without the
 * others. `initialRun` seeds the header instantly from the row the sidebar
 * click already has in hand, so selecting a run doesn't blank the detail
 * pane while `GET /runs/{id}` is in flight; the fetch below still runs to
 * pick up fields the list row doesn't carry. */
function useRunDetail(runId: string | null, initialRun: RunListItem | null) {
	const [run, setRun] = useState<RunListItem | null>(initialRun);
	const [jobs, setJobs] = useState<RunJobItem[]>([]);
	const [artifacts, setArtifacts] = useState<RunArtifactRow[] | null>(null);
	const [artifactsUnavailable, setArtifactsUnavailable] = useState(false);
	const [expiredAttestations, setExpiredAttestations] = useState<ExpiredAttestation[]>([]);
	const [loading, setLoading] = useState(false);
	const [loadError, setLoadError] = useState<string | null>(null);

	useEffect(() => {
		setRun(initialRun);
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, [runId]);

	useEffect(() => {
		if (!runId) {
			setJobs([]);
			setArtifacts(null);
			setArtifactsUnavailable(false);
			setExpiredAttestations([]);
			return;
		}

		let cancelled = false;
		setLoading(true);
		setLoadError(null);

		async function loadExpiredAttestations(rows: RunArtifactRow[]) {
			// One (profile, target) pair per distinct benchmark+target combination
			// in the artifacts rows — results.ts's fetchAttestationResolution takes
			// a target id, but RunArtifactRow only carries a display name (the
			// documented artifacts shape has no target id field), so this can only
			// resolve when the artifact row's target name is itself a valid target
			// id/lookup key. That mismatch is exactly the "current api-contract does
			// not fully connect these two surfaces yet" gap called out in the PR
			// body; guarded here rather than assumed away.
			const results: ExpiredAttestation[] = [];
			for (const row of rows) {
				try {
					const resolutions = await fetchAttestationResolution(row.benchmark, row.target);
					for (const resolution of resolutions) {
						if (resolution.attestation_expired) {
							results.push({ target: row.target, profile: row.benchmark, resolution });
						}
					}
				} catch {
					// Best-effort per-row: one target's resolve failing (e.g. the name
					// isn't a real target id) must not blank the rest of the sidebar.
				}
			}
			if (!cancelled) {
				setExpiredAttestations(results);
			}
		}

		async function load() {
			try {
				const [runDetail, jobRows] = await Promise.all([fetchRun(runId!), fetchRunJobs(runId!)]);
				if (cancelled) {
					return;
				}
				setRun(runDetail);
				setJobs(jobRows);
			} catch (err) {
				if (!cancelled) {
					setLoadError(err instanceof ApiError ? err.message : "Could not load this run.");
				}
			} finally {
				if (!cancelled) {
					setLoading(false);
				}
			}

			try {
				const rows = await fetchRunArtifacts(runId!);
				if (!cancelled) {
					setArtifacts(rows);
					void loadExpiredAttestations(rows);
				}
			} catch {
				// GET /runs/{id}/artifacts is documented but not yet implemented
				// (results.ts module doc) — a 404/network failure here is expected
				// today, not an error to surface as a load failure for the whole
				// screen. The table renders its own "not available yet" state.
				if (!cancelled) {
					setArtifacts(null);
					setArtifactsUnavailable(true);
				}
			}
		}

		void load();
		return () => {
			cancelled = true;
		};
	}, [runId]);

	return { run, setRun, jobs, artifacts, artifactsUnavailable, expiredAttestations, loading, loadError };
}

function kpiTiles(run: RunListItem | null, artifacts: RunArtifactRow[] | null) {
	const jobTotal = run?.job_count ?? 0;
	const jobDone = run?.job_count_completed ?? 0;
	const compliancePercent = jobTotal > 0 ? Math.round((jobDone / jobTotal) * 1000) / 10 : 0;
	const catI = artifacts?.reduce((sum, a) => sum + a.catIOpen, 0) ?? 0;
	const catII = artifacts?.reduce((sum, a) => sum + a.catIIOpen, 0) ?? 0;
	const catIII = artifacts?.reduce((sum, a) => sum + a.catIIIOpen, 0) ?? 0;
	return [
		{ label: "COMPLIANCE", value: `${compliancePercent}%`, className: "results__kpi--ok" },
		{ label: "CAT I OPEN", value: String(catI), className: "results__kpi--bad" },
		{ label: "CAT II OPEN", value: String(catII), className: "results__kpi--warn" },
		{ label: "CAT III OPEN", value: String(catIII), className: "results__kpi--muted" },
		{ label: "ATTESTED N/A", value: "—", className: "results__kpi--na" },
	];
}

/**
 * Fetches every job's CKL and zips them client-side (AC3). Uses a raw
 * `fetch` rather than `lib/api.ts`'s `apiGet` because the artifact route
 * returns binary XML, not the JSON `apiFetch` always parses — but still
 * attaches the bearer token by hand (`useAuth()`'s `token`, the same value
 * `lib/events.ts`'s SSE connection authenticates with) so this doesn't
 * silently downgrade to an unauthenticated request the way a bare `fetch`
 * with no headers would.
 */
async function downloadCklBundle(runId: string, jobs: RunJobItem[], token: string | null): Promise<void> {
	const entries: ZipEntry[] = [];
	for (const job of jobs) {
		const url = artifactDownloadUrl(job.id, "ckl");
		try {
			const headers: Record<string, string> = { Accept: "application/octet-stream" };
			if (token) {
				headers.Authorization = `Bearer ${token}`;
			}
			const response = await fetch(url, { headers });
			if (!response.ok) {
				continue;
			}
			const data = new Uint8Array(await response.arrayBuffer());
			const name = `${job.target_name ?? job.id}.ckl`;
			entries.push({ name, data });
		} catch {
			// One target's CKL failing to fetch must not abort the whole bundle —
			// same "individual target failures must not halt" rule CLAUDE.md's Key
			// Constraints applies to runs applies here to the export.
		}
	}
	const blob = buildZip(entries);
	const objectUrl = URL.createObjectURL(blob);
	const link = document.createElement("a");
	link.href = objectUrl;
	link.download = `${runId}-ckl-bundle.zip`;
	document.body.appendChild(link);
	link.click();
	document.body.removeChild(link);
	URL.revokeObjectURL(objectUrl);
}

export function ResultsScreen() {
	const { user, token } = useAuth();
	const [runs, setRuns] = useState<RunListItem[]>([]);
	const [runsLoading, setRunsLoading] = useState(true);
	const [runsError, setRunsError] = useState<string | null>(null);
	const [selectedRunId, setSelectedRunId] = useState<string | null>(null);
	const [selectedRow, setSelectedRow] = useState<RunListItem | null>(null);
	const [search, setSearch] = useState("");
	const [exporting, setExporting] = useState(false);

	useEffect(() => {
		let cancelled = false;
		fetchRunList(50, 0)
			.then((result) => {
				if (cancelled) {
					return;
				}
				setRuns(result.items);
				if (result.items.length > 0) {
					setSelectedRunId((current) => current ?? result.items[0].id);
					setSelectedRow((current) => current ?? result.items[0]);
				}
			})
			.catch((err) => {
				if (!cancelled) {
					setRunsError(err instanceof ApiError ? err.message : "Could not load run history.");
				}
			})
			.finally(() => {
				if (!cancelled) {
					setRunsLoading(false);
				}
			});
		return () => {
			cancelled = true;
		};
	}, []);

	const { run, jobs, artifacts, artifactsUnavailable, expiredAttestations, loading, loadError } = useRunDetail(
		selectedRunId,
		selectedRow,
	);

	const filteredRuns = useMemo(() => {
		const term = search.trim().toLowerCase();
		if (!term) {
			return runs;
		}
		return runs.filter((r) => r.id.toLowerCase().includes(term) || (scopeSiteId(r.scope) ?? "").toLowerCase().includes(term));
	}, [runs, search]);

	const handleSelectRun = useCallback((row: RunListItem) => {
		setSelectedRunId(row.id);
		setSelectedRow(row);
	}, []);

	// Remediate is a stub regardless of role (epic #15/M4 has not landed) — but
	// still runs through roleGateProps for the same visible-but-disabled
	// treatment and opacity every other role-gated control uses, then the
	// stub note is layered on top of (never instead of) the role reason so a
	// sub-Admin role sees why AND that it's not built yet.
	const remediateRoleGate = user ? roleGateProps(user.role, "Admin") : { disabled: true, style: { opacity: 0.42 } };
	const remediateGate = {
		...remediateRoleGate,
		disabled: true,
		title:
			user && user.role !== "Admin"
				? "Requires Admin — remediation is not available to your role, and is stubbed until M4 (epic #15) for every role"
				: "Remediation is stubbed until M4 (epic #15) — typed confirmation not yet implemented",
	};

	const handleExport = useCallback(async () => {
		if (!run) {
			return;
		}
		setExporting(true);
		try {
			await downloadCklBundle(run.id, jobs, token);
		} finally {
			setExporting(false);
		}
	}, [run, jobs, token]);

	if (runsLoading) {
		return (
			<div className="results-screen">
				<div className="results__empty">Loading run history…</div>
			</div>
		);
	}

	if (runsError) {
		return (
			<div className="results-screen">
				<div className="results__empty results__empty--error">{runsError}</div>
			</div>
		);
	}

	return (
		<div className="results-screen">
			<div className="results__sidebar-list">
				<div className="results__search-row">
					<input
						className="results__search"
						placeholder="search runs…"
						value={search}
						onChange={(e) => setSearch(e.target.value)}
						aria-label="Search runs"
					/>
				</div>
				<div className="results__run-list">
					{filteredRuns.map((row) => (
						<button
							type="button"
							key={row.id}
							className={`results__run-row ${row.id === selectedRunId ? "is-selected" : ""}`}
							onClick={() => handleSelectRun(row)}
						>
							<div className="results__run-row-top">
								<span className={`results__run-dot results__run-dot--${row.state}`} />
								<span className="mono results__run-id">{row.id}</span>
								<span className="results__run-kind">{row.run_type}</span>
							</div>
							<div className="results__run-row-meta">
								{scopeSiteId(row.scope) ?? "—"} · {row.job_count} targets ·{" "}
								{formatRunDuration(row.started_at, row.completed_at)}
							</div>
							<div className="mono results__run-row-when">
								{formatTimestamp(row.created_at)} · {row.initiated_by ?? "system"}
							</div>
						</button>
					))}
					{filteredRuns.length === 0 && <div className="results__empty">No runs match "{search}".</div>}
				</div>
			</div>

			<div className="results__detail">
				{!run && <div className="results__empty">Select a run to view details.</div>}
				{run && (
					<>
						<div className="results__detail-header">
							<div className="results__title-block">
								<div className="mono results__run-title">{run.id}</div>
								<div className="results__run-subtitle">
									{scopeSiteId(run.scope) ?? "—"} · {run.job_count} targets · {run.run_type} ·{" "}
									{run.completed_at ? `completed ${formatTimestamp(run.completed_at)}` : run.state} in{" "}
									{formatRunDuration(run.started_at, run.completed_at)} · initiated by {run.initiated_by ?? "system"}
								</div>
							</div>
							<div className="results__spacer" />
							<button type="button" className="results__export-btn" onClick={handleExport} disabled={exporting || jobs.length === 0}>
								{exporting ? "Exporting…" : "Export CKL bundle"}
							</button>
							<button type="button" className="results__remediate-btn" {...remediateGate}>
								Remediate findings…
							</button>
						</div>

						<div className="results__kpis">
							{kpiTiles(run, artifacts).map((tile) => (
								<div key={tile.label} className="results__kpi-tile">
									<div className="results__kpi-label">{tile.label}</div>
									<div className={`results__kpi-value ${tile.className}`}>{tile.value}</div>
								</div>
							))}
						</div>

						{loadError && <div className="results__action-error">{loadError}</div>}

						<div className="results__panes">
							<ArtifactsTable loading={loading} artifacts={artifacts} unavailable={artifactsUnavailable} jobs={jobs} />
							<div className="results__side-panels">
								<AttestationsPanel expired={expiredAttestations} />
								<UploadStatusPanel jobs={jobs} />
							</div>
						</div>
					</>
				)}
			</div>
		</div>
	);
}

function ArtifactsTable({
	loading,
	artifacts,
	unavailable,
	jobs,
}: {
	loading: boolean;
	artifacts: RunArtifactRow[] | null;
	unavailable: boolean;
	jobs: RunJobItem[];
}) {
	return (
		<div className="results__panel">
			<div className="results__panel-header">
				<div className="results__panel-title">PER-TARGET ARTIFACTS</div>
				<div className="results__spacer" />
				<div className="mono results__panel-meta">{jobs.length} targets</div>
			</div>
			<table className="results__table">
				<thead>
					<tr>
						<th className="results__col-target">TARGET</th>
						<th className="results__col-bench">BENCHMARK</th>
						<th className="results__col-sev">{SEVERITIES[0]}</th>
						<th className="results__col-sev">{SEVERITIES[1]}</th>
						<th className="results__col-sev">{SEVERITIES[2]}</th>
						<th className="results__col-artifacts">ARTIFACTS</th>
						<th className="results__col-stigman">STIG MANAGER</th>
					</tr>
				</thead>
				<tbody>
					{loading && (
						<tr>
							<td colSpan={7} className="results__empty">
								Loading artifacts…
							</td>
						</tr>
					)}
					{!loading && unavailable && (
						<tr>
							<td colSpan={7} className="results__empty">
								Per-target artifacts are not available yet — GET /runs/{"{id}"}/artifacts is documented but not yet
								implemented on the backend (issue #299).
							</td>
						</tr>
					)}
					{!loading &&
						!unavailable &&
						artifacts?.map((row) => (
							<tr key={row.target}>
								<td className="results__col-target mono">{row.target}</td>
								<td className="results__col-bench mono">{row.benchmark}</td>
								<td className="results__col-sev">
									<SeverityPill severity="CAT I" count={row.catIOpen} />
								</td>
								<td className="results__col-sev">
									<SeverityPill severity="CAT II" count={row.catIIOpen} />
								</td>
								<td className="results__col-sev">
									<SeverityPill severity="CAT III" count={row.catIIIOpen} />
								</td>
								<td className="results__col-artifacts mono">{row.artifactKinds.join(" · ").toUpperCase()}</td>
								<td className="results__col-stigman">
									<UploadStatusPill status={row.uploadStatus} />
								</td>
							</tr>
						))}
					{!loading && !unavailable && artifacts?.length === 0 && (
						<tr>
							<td colSpan={7} className="results__empty">
								No artifacts for this run.
							</td>
						</tr>
					)}
				</tbody>
			</table>
		</div>
	);
}

function UploadStatusPill({ status }: { status: RunArtifactRow["uploadStatus"] }) {
	const label = status === "not-uploaded" ? "not uploaded" : status;
	return <span className={`results__upload-pill results__upload-pill--${status}`}>{label}</span>;
}

function AttestationsPanel({ expired }: { expired: ExpiredAttestation[] }) {
	return (
		<div className="results__panel results__panel--sidebar">
			<div className="results__panel-title">ATTESTATIONS APPLIED</div>
			<div className="results__panel-note">
				Waivers resolved at scan time. Expired waivers below fell through and left their control Open — full applied-waiver
				detail (control, justification, author/version for every waiver, not just expired ones) awaits
				GET /runs/{"{id}"}/attestations-applied (issue #275).
			</div>
			{expired.length === 0 && <div className="results__panel-empty">No expired attestations resolved for this run.</div>}
			{expired.map((item, i) => (
				<div key={`${item.target}-${item.profile}-${i}`} className="results__attest-row">
					<div className="results__attest-top">
						<span className="mono results__attest-target">{item.target}</span>
						<span className="results__attest-scope-pill">EXPIRED</span>
						<span className="results__spacer" />
						<span className="mono results__attest-meta">{formatTimestamp(item.resolution.attestation_expires_at)}</span>
					</div>
					<div className="results__attest-note">
						{item.profile} · v{item.resolution.version ?? "—"} · {item.resolution.author ?? "unknown author"}
					</div>
				</div>
			))}
			<button
				type="button"
				className="results__open-benchmarks-btn"
				disabled
				title="Open in Benchmarks is stubbed until the full config-doc editor lands (docs/ui/prototype screen 5)"
			>
				Open in Benchmarks
			</button>
		</div>
	);
}

function UploadStatusPanel({ jobs }: { jobs: RunJobItem[] }) {
	const uploaded = jobs.filter((j) => stigManagerStatusLabel(j.state) === "uploaded").length;
	const notUploaded = jobs.filter((j) => stigManagerStatusLabel(j.state) === "not-uploaded").length;
	return (
		<div className="results__panel results__panel--sidebar">
			<div className="results__panel-title">UPLOAD STATUS</div>
			<div className="results__stat-row">
				<span className="results__stat-label">Uploaded</span>
				<span className="mono results__stat-value results__stat-value--ok">
					{uploaded} / {jobs.length}
				</span>
			</div>
			<div className="results__stat-row">
				<span className="results__stat-label">Not uploaded</span>
				<span className="mono results__stat-value results__stat-value--bad">{notUploaded}</span>
			</div>
			<button
				type="button"
				className="results__retry-btn"
				disabled
				title="Retry is stubbed until the STIG Manager integration (issue #25) lands"
			>
				Retry failed uploads
			</button>
		</div>
	);
}
