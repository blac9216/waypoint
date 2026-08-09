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
 *     `GET /runs/{id}/artifacts` (issue #299/#305). Rows carry
 *     `counts_available`; the CAT counts are omitted (not zero) when the
 *     HDF is absent/corrupt, and this screen renders that as an explicit
 *     "n/a" pill rather than a false "0 open" (issue #307 — a corrupt scan
 *     must never look clean). Still renders a graceful "not available yet"
 *     empty state if the call itself fails.
 *   - Attestations-applied: `GET /runs/{id}/attestations-applied`
 *     (#299/#305, wire shape updated by #306/PR #336) is the primary source
 *     for the sidebar's waiver list. Each row is a PERSISTED, AT-SCAN-TIME
 *     snapshot (see `results.ts`'s `AppliedAttestation` doc comment) —
 *     immutable per run, not re-derived on load — and the panel shows the
 *     real `applied_at` scan-time timestamp rather than the old
 *     live-resolution caveat (issue #307's caveat no longer applies; #306
 *     closed the gap it described). The sidebar also still calls the
 *     MERGED `GET /config-docs/resolve?profile&target&kind=attestation`
 *     (issue #266) to flag which of those rows are currently EXPIRED,
 *     mirroring what the persisted ledger records server-side.
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
import { useAuth } from "../../lib/auth-context";
import { roleGateProps } from "../../lib/roles";
import {
	artifactDownloadUrl,
	fetchAttestationResolution,
	fetchAttestationsApplied,
	fetchRun,
	fetchRunArtifacts,
	fetchRunJobs,
	fetchRunList,
	formatRunDuration,
	formatTimestamp,
	parseAttestationScope,
	scopeSiteId,
	SEVERITIES,
	stigManagerStatusLabel,
	type AppliedAttestation,
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
 * ResultsScreen.test.tsx for the assertion this backs.
 *
 * `count` is `undefined` when the row's `counts_available` is `false` (HDF
 * absent/unparseable) — rendered as an explicit "n/a", never a bare `0`.
 * Collapsing "could not count" into `0` would read as a clean, compliant
 * target on a corrupt scan (issue #307 / #299 round-1 review blocker). */
function SeverityPill({ severity, count }: { severity: Severity; count: number | undefined }) {
	const display = count === undefined ? "n/a" : String(count);
	const title = count === undefined ? `${severity}: not available (could not count)` : `${severity} open: ${count}`;
	return (
		<span
			className={`results__severity ${count === undefined ? "results__severity--na" : SEVERITY_CLASS[severity]}`}
			title={title}
		>
			{severity} <span className="mono">{display}</span>
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
	const [attestationsApplied, setAttestationsApplied] = useState<AppliedAttestation[] | null>(null);
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
			setAttestationsApplied(null);
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
				// `benchmark` is nullable on the wire (RunArtifactResponse) — a row
				// with no benchmark has nothing to resolve an attestation against.
				if (!row.benchmark) {
					continue;
				}
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
				// A 404/network failure here must not blank the whole screen — the
				// table renders its own "not available yet" state so one flaky
				// sub-fetch doesn't take out KPIs/jobs/sidebar with it.
				if (!cancelled) {
					setArtifacts(null);
					setArtifactsUnavailable(true);
				}
			}

			try {
				const applied = await fetchAttestationsApplied(runId!);
				if (!cancelled) {
					setAttestationsApplied(applied);
				}
			} catch {
				// Same best-effort treatment as artifacts above — the sidebar
				// panel falls back to its existing expired-only view.
				if (!cancelled) {
					setAttestationsApplied(null);
				}
			}
		}

		void load();
		return () => {
			cancelled = true;
		};
	}, [runId]);

	return { run, setRun, jobs, artifacts, artifactsUnavailable, expiredAttestations, attestationsApplied, loading, loadError };
}

/** Sums a CAT column across rows that have `counts_available:true`, and
 * separately reports whether every row was countable. A KPI tile summed
 * across a mix of countable and uncountable rows would silently understate
 * the true open-finding count as if the uncountable rows were zero — the
 * same "never fabricate 0" rule `SeverityPill` enforces per-row applies to
 * the aggregate too, so the tile falls back to "n/a" rather than a
 * misleadingly precise-looking partial sum whenever any row is uncountable. */
function sumCatColumn(artifacts: RunArtifactRow[] | null, field: "cat_i_open" | "cat_ii_open" | "cat_iii_open"): number | null {
	if (!artifacts || artifacts.length === 0) {
		return null;
	}
	if (artifacts.some((a) => !a.counts_available)) {
		return null;
	}
	return artifacts.reduce((sum, a) => sum + (a[field] ?? 0), 0);
}

function kpiTiles(run: RunListItem | null, artifacts: RunArtifactRow[] | null) {
	const jobTotal = run?.job_count ?? 0;
	const jobDone = run?.job_count_completed ?? 0;
	const compliancePercent = jobTotal > 0 ? Math.round((jobDone / jobTotal) * 1000) / 10 : 0;
	const catI = sumCatColumn(artifacts, "cat_i_open");
	const catII = sumCatColumn(artifacts, "cat_ii_open");
	const catIII = sumCatColumn(artifacts, "cat_iii_open");
	return [
		{ label: "COMPLIANCE", value: `${compliancePercent}%`, className: "results__kpi--ok" },
		{ label: "CAT I OPEN", value: catI === null ? "n/a" : String(catI), className: catI === null ? "results__kpi--na" : "results__kpi--bad" },
		{
			label: "CAT II OPEN",
			value: catII === null ? "n/a" : String(catII),
			className: catII === null ? "results__kpi--na" : "results__kpi--warn",
		},
		{
			label: "CAT III OPEN",
			value: catIII === null ? "n/a" : String(catIII),
			className: catIII === null ? "results__kpi--na" : "results__kpi--muted",
		},
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

	const { run, jobs, artifacts, artifactsUnavailable, expiredAttestations, attestationsApplied, loading, loadError } =
		useRunDetail(selectedRunId, selectedRow);

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
								<AttestationsPanel expired={expiredAttestations} applied={attestationsApplied} />
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
								Per-target artifacts could not be loaded for this run — GET /runs/{"{id}"}/artifacts failed or returned
								no data.
							</td>
						</tr>
					)}
					{!loading &&
						!unavailable &&
						artifacts?.map((row) => (
							<tr key={row.job_id}>
								<td className="results__col-target mono">{row.target}</td>
								<td className="results__col-bench mono">{row.benchmark ?? "—"}</td>
								<td className="results__col-sev">
									<SeverityPill severity="CAT I" count={row.counts_available ? row.cat_i_open : undefined} />
								</td>
								<td className="results__col-sev">
									<SeverityPill severity="CAT II" count={row.counts_available ? row.cat_ii_open : undefined} />
								</td>
								<td className="results__col-sev">
									<SeverityPill severity="CAT III" count={row.counts_available ? row.cat_iii_open : undefined} />
								</td>
								<td className="results__col-artifacts mono">{row.artifact_kinds.join(" · ").toUpperCase()}</td>
								<td className="results__col-stigman">
									<UploadStatusPill status={row.upload_status} />
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

function UploadStatusPill({ status }: { status: RunArtifactRow["upload_status"] }) {
	const label = status === "not-uploaded" ? "not uploaded" : status;
	return <span className={`results__upload-pill results__upload-pill--${status}`}>{label}</span>;
}

function AttestationsPanel({ expired, applied }: { expired: ExpiredAttestation[]; applied: AppliedAttestation[] | null }) {
	return (
		<div className="results__panel results__panel--sidebar">
			<div className="results__panel-title">ATTESTATIONS APPLIED</div>
			<div className="results__panel-note">
				{/* Honest framing per issue #306/PR #336: each row below is a persisted
				    snapshot recorded the instant the attest stage resolved that target's
				    attestation at scan time — immutable for this run regardless of any
				    later edit to the underlying config-doc. Superseded the #307/#299
				    "live resolution" caveat, which no longer applies now that this is
				    genuinely recorded history. */}
				Recorded at scan time: rows are the attestation each target actually ran with, permanently — editing the
				underlying config-doc afterward does not change what's shown here for this run.
			</div>
			{applied && applied.length > 0 && (
				<>
					{applied.map((item, i) => {
						const { layer, ref } = parseAttestationScope(item.scope);
						return (
							<div key={`applied-${item.control}-${item.scope}-${i}`} className="results__attest-row">
								<div className="results__attest-top">
									<span className="mono results__attest-target">{item.control}</span>
									<span className={`results__attest-scope-pill ${item.expired ? "" : "results__attest-scope-pill--active"}`}>
										{item.expired ? "EXPIRED" : layer.toUpperCase()}
										{ref ? ` · ${ref}` : ""}
									</span>
									<span className="results__spacer" />
									<span className="mono results__attest-meta">
										applied {formatTimestamp(item.applied_at)}
										{item.attestation_updated_at ? ` · doc edited ${formatTimestamp(item.attestation_updated_at)}` : ""}
									</span>
								</div>
								<div className="results__attest-note">
									{item.coverage} · v{item.version} · {item.author} — {item.justification}
								</div>
							</div>
						);
					})}
				</>
			)}
			{(!applied || applied.length === 0) && (
				<>
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
				</>
			)}
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
