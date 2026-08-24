/**
 * Small shared presentation pieces for type-specific job-detail renderers
 * (issue #591). Every per-type renderer (`complianceRenderers.tsx`,
 * `operationalRenderers.tsx`) wraps `GenericJobDetail`'s facts/log with a
 * concise, domain-flavored summary and — when the run has produced an
 * output the owning domain screen can show — a link to that screen, rather
 * than re-implementing lifecycle/log rendering per type (AC: "reuse/move
 * existing scan presentation components rather than rewrite", generalized
 * to every domain: reuse `GenericJobDetail`, add only what's type-specific).
 *
 * The `role="region"`/`aria-label="Job detail: ..."` landmark stays on the
 * INNER `GenericJobDetail` only (never duplicated on this outer wrapper) —
 * one `region` per job detail, matching what `LiveJobsScreen.test.tsx`
 * already queries by by that role+name, and avoiding an ambiguous nested
 * pair of identically-labeled regions a screen reader (and RTL's
 * `getByRole`) would otherwise have to disambiguate.
 */
import type { ReactNode } from "react";
import { Link } from "../../lib/router";
import { GenericJobDetail } from "./detailRenderers";
import type { JobDetailProps } from "./detailRenderers";

/** One labeled fact row, matching `GenericJobDetail`'s `<dl>` convention. */
export function SummaryFact({ label, value }: { label: string; value: ReactNode }) {
	return (
		<div>
			<dt>{label}</dt>
			<dd>{value}</dd>
		</div>
	);
}

/**
 * Wraps a type-specific operational summary above `GenericJobDetail`'s
 * lifecycle/log view — every non-generic renderer keeps the same log/timing
 * diagnostics the generic fallback provides (never regresses them, per
 * issue #591's AC) and adds only what the type needs on top: a short
 * `<dl>` of domain facts, plus an optional link to the owning domain screen
 * when this job's run has produced something that screen can show.
 */
export function TypeDetailShell({
	job,
	group,
	kicker,
	facts,
	domainLink,
	note,
}: JobDetailProps & {
	/** Short label naming the domain this renderer belongs to, e.g. "Compliance scan", "Discovery". */
	kicker: string;
	facts?: ReactNode;
	domainLink?: { to: string; label: string };
	/** Optional freeform note below the facts — e.g. the HadErrors-is-advisory caveat (issue #612). */
	note?: ReactNode;
}) {
	return (
		<div className="live-jobs-detail-shell">
			<div className="live-jobs-detail__kicker mono">{kicker}</div>
			{facts && <dl className="live-jobs-detail__facts">{facts}</dl>}
			{note && <p className="live-jobs-detail__note">{note}</p>}
			{domainLink && (
				<p className="live-jobs-detail__domain-link">
					<Link to={domainLink.to}>{domainLink.label} →</Link>
				</p>
			)}
			<GenericJobDetail job={job} group={group} />
		</div>
	);
}
