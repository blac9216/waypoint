/**
 * Compliance-domain job-detail renderers (issue #591): `scan`, `remediate`,
 * and `purge` — `JobCapabilities.Compliance`'s three "has a real presence
 * in the Live Jobs workspace" members (`discover`/`credential-test`/
 * `content-pull`/`content-import` are operational, not scan/remediation
 * presentation, and live in `operationalRenderers.tsx`).
 *
 * Stage/finding/attestation/artifact PRESENTATION stays entirely in the
 * compliance domain: `ArtifactsTable`/`AttestationsPanel`/`UploadStatusPanel`
 * (screens/results/*) already render that, run-scoped, once a scan's
 * artifacts exist — re-fetching and duplicating those tables at job-scope
 * here would fork the same data through two code paths. The scan and
 * remediate renderers show the same lifecycle/stage/log facts every job
 * gets (via `TypeDetailShell` -> `GenericJobDetail`) plus two links: the
 * restored Live Run console (issue #707/epic #706 — the operational
 * monitoring surface: three layouts, stage board, run controls) as the
 * prominent `primaryLink`, and the renamed Compliance Scan Results screen
 * (issue #591) for the owning run's real per-target artifacts/attestations/
 * CKL export as `domainLink`. This renderer stays a summary, never
 * duplicating the console inside it — see issue #705's own framing ("this
 * is a port into the JOB_DETAIL_RENDERERS seam, not a rewrite").
 */
import { TypeDetailShell, SummaryFact } from "./detailPresentation";
import type { JobDetailProps } from "./detailRenderers";

function resultsLink(runId: string) {
	return { to: `/results?run=${encodeURIComponent(runId)}`, label: "View in Compliance Scan Results" };
}

/** Issue #707: the restored per-run monitoring console — layouts, stage
 * board, run controls (pause/resume/abort/cancel, blocked-banner
 * credential swap) — none of which this generic-workspace renderer
 * duplicates. */
function liveRunLink(runId: string) {
	return { to: `/live-run?run=${encodeURIComponent(runId)}`, label: "Open Live Run console" };
}

export function ScanJobDetail({ job, group }: JobDetailProps) {
	return (
		<TypeDetailShell
			job={job}
			group={group}
			kicker="Compliance scan"
			facts={
				<SummaryFact
					label="Run progress"
					value={`${group.job_count_completed}/${group.job_count} targets complete${group.job_count_failed > 0 ? `, ${group.job_count_failed} failed` : ""}`}
				/>
			}
			primaryLink={liveRunLink(group.run_id)}
			domainLink={resultsLink(group.run_id)}
		/>
	);
}

/** Remediation is never schedulable (CLAUDE.md Key Constraints — "remediation
 * is never schedulable and always requires explicit human confirmation") and
 * is stubbed until M4 (epic #15, `ResultsScreen.tsx`'s `remediateGate`); this
 * renderer only ever presents lifecycle/stage facts for a `remediate` job
 * that already exists, never implies scheduling or auto-confirmation. */
export function RemediateJobDetail({ job, group }: JobDetailProps) {
	return (
		<TypeDetailShell
			job={job}
			group={group}
			kicker="Remediation"
			facts={
				<SummaryFact
					label="Run progress"
					value={`${group.job_count_completed}/${group.job_count} targets complete${group.job_count_failed > 0 ? `, ${group.job_count_failed} failed` : ""}`}
				/>
			}
			primaryLink={liveRunLink(group.run_id)}
			domainLink={resultsLink(group.run_id)}
		/>
	);
}

/**
 * Purge (issue #594/#656/#657, epic #577) deletes a terminal run's HDF/CKL
 * artifacts and belongs to `JobCapabilities.Compliance` (compliance-runner
 * owns the scan-artifact volume mount), but it is operational-history
 * maintenance, not a domain outcome (ADR-0019 decision 4: "deleting
 * operational history never implicitly deletes domain state") — a concise
 * summary of what's being purged, not the full Results-panel treatment, and
 * no link back to Results once the target run's artifacts are gone.
 */
export function PurgeJobDetail({ job, group }: JobDetailProps) {
	return (
		<TypeDetailShell
			job={job}
			group={group}
			kicker="Run purge"
			facts={<SummaryFact label="Purging run" value={group.run_id} />}
			note="Purge deletes this run's persisted artifacts and history — it is operational maintenance, not a scan/remediation outcome."
		/>
	);
}
