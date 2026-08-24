/**
 * Non-compliance job-detail renderers (issue #591) — concise operational
 * summaries plus a link to the owning domain screen, never scan-only
 * controls (stage/finding/attestation/artifact presentation stays in
 * `complianceRenderers.tsx`). Covers every `job_type` in
 * `JobCapabilities.Compliance` other than scan/remediate/purge
 * (discover, credential-test, content-pull/content-import) and every type in
 * `JobCapabilities.Download` (catalog-index, download, tool-install,
 * bundle-export/import, content-library-sync, update) —
 * backend/Waypoint.Core/Jobs/JobCapabilities.cs is the authoritative set
 * this file's registrations (`detailRenderers.registry.ts`) are keyed
 * against.
 *
 * Domain screens some of these link to (Configuration's Sites & Targets /
 * Compliance Content tabs) have no query-param deep-link support today —
 * unlike Results (`?run=`, added alongside this issue) or Benchmarks
 * (`?profile=&target=`, issue #559), `ConfigurationScreen.tsx` picks its tab
 * from local `useState` with no URL sync. Adding that is a real, separate
 * frontend change (a new query-param contract for a screen this issue
 * doesn't otherwise touch) — out of scope here per the "additive, minimal"
 * guidance for touching code outside the renderer seam. These links land on
 * `/config` (the screen) rather than a specific tab; the link label names
 * the tab a user should select once there.
 */
import { TypeDetailShell, SummaryFact } from "./detailPresentation";
import type { JobDetailProps } from "./detailRenderers";

/**
 * Discovery (issue #557/#604): finds targets within a site's discovery
 * scope. Its lasting output is the Targets list, not a per-job artifact —
 * links to Configuration's Sites & Targets tab.
 */
export function DiscoverJobDetail({ job, group }: JobDetailProps) {
	return (
		<TypeDetailShell
			job={job}
			group={group}
			kicker="Discovery"
			facts={<SummaryFact label="Scope" value={job.target_name ?? job.target_id ?? "site scan"} />}
			domainLink={{ to: "/config", label: "View discovered targets in Sites & Targets" }}
		/>
	);
}

/**
 * Credential test (issue #245/#323): a single pass/fail/auth-failed check
 * against one credential, no durable domain output beyond the credential's
 * own `health` field (already visible on the Credentials tab) — a concise
 * summary is the whole story, no domain link needed beyond that tab.
 */
export function CredentialTestJobDetail({ job, group }: JobDetailProps) {
	return (
		<TypeDetailShell
			job={job}
			group={group}
			kicker="Credential test"
			facts={<SummaryFact label="Result" value={job.state} />}
			domainLink={{ to: "/config", label: "View credential health in Configuration" }}
		/>
	);
}

/**
 * Content pull/import (ADR-0017): populates the compliance content library
 * (profiles/benchmarks) the Compliance Content tab manages.
 */
export function ContentJobDetail({ job, group }: JobDetailProps) {
	return (
		<TypeDetailShell
			job={job}
			group={group}
			kicker="Compliance content"
			facts={<SummaryFact label="Stage" value={job.stage ?? "—"} />}
			domainLink={{ to: "/config", label: "View Compliance Content" }}
		/>
	);
}

/**
 * Catalog index (issue #566/#576 family): (re)builds the Download Catalog's
 * index of available VCF artifacts.
 */
export function CatalogIndexJobDetail({ job, group }: JobDetailProps) {
	return (
		<TypeDetailShell
			job={job}
			group={group}
			kicker="Catalog index"
			facts={<SummaryFact label="Stage" value={job.stage ?? "—"} />}
			domainLink={{ to: "/catalog", label: "View Download Catalog" }}
		/>
	);
}

/**
 * Download / tool-install: fetches artifacts (or an operator-installed
 * managed tool, CLAUDE.md "Never project-publish vendor binaries") into the
 * offline repository the Library screen presents. `HadErrors` (issue #612)
 * is advisory-only on the PowerShell executor and is not surfaced on any
 * wire shape this renderer reads (`JobResponse`/`LiveJobRow` carry no such
 * field) — this renderer therefore has nothing to misrender as a failure;
 * only the job's own terminal `state` (`done`/`failed`) drives presentation,
 * never a log heuristic.
 */
export function DownloadJobDetail({ job, group }: JobDetailProps) {
	return (
		<TypeDetailShell
			job={job}
			group={group}
			kicker="Download"
			facts={<SummaryFact label="Stage" value={job.stage ?? "—"} />}
			domainLink={{ to: "/library", label: "View Library" }}
		/>
	);
}

/**
 * Bundle export/import (ADR-0015 air-gap bundles): no registered handler yet
 * (`JobCapabilities.cs`: "later") — the closed `job_type` set already
 * reserves these values, so the registry maps them now rather than falling
 * through to the generic renderer once a handler lands. Links to Transfer
 * (ADR-0019 decision 4), today a placeholder screen (`screens.tsx`'s
 * `TransferScreen`) — the link is honest either way: it goes to the route
 * that will host bundle transfer, not a screen that doesn't exist.
 */
export function BundleJobDetail({ job, group }: JobDetailProps) {
	return (
		<TypeDetailShell
			job={job}
			group={group}
			kicker="Bundle transfer"
			facts={<SummaryFact label="Stage" value={job.stage ?? "—"} />}
			domainLink={{ to: "/transfer", label: "View Transfer" }}
		/>
	);
}

/** Content-library sync: no registered handler yet — see `BundleJobDetail`'s
 * doc comment for why it's mapped ahead of a handler landing. */
export function ContentLibrarySyncJobDetail({ job, group }: JobDetailProps) {
	return (
		<TypeDetailShell
			job={job}
			group={group}
			kicker="Content library sync"
			facts={<SummaryFact label="Stage" value={job.stage ?? "—"} />}
			domainLink={{ to: "/library", label: "View Library" }}
		/>
	);
}

/** System update: no registered handler yet — see `BundleJobDetail`'s doc
 * comment for why it's mapped ahead of a handler landing. Links to Transfer
 * per ADR-0019 decision 4 (system administration surface), the same
 * placeholder destination bundle jobs use until a dedicated screen lands. */
export function UpdateJobDetail({ job, group }: JobDetailProps) {
	return (
		<TypeDetailShell
			job={job}
			group={group}
			kicker="System update"
			facts={<SummaryFact label="Stage" value={job.stage ?? "—"} />}
			domainLink={{ to: "/transfer", label: "View Transfer" }}
		/>
	);
}
