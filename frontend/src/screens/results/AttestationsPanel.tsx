/**
 * Attestations-applied sidebar panel — extracted from `ResultsScreen.tsx`
 * (issue #416 decomposition, no behavior change). See that file's module doc
 * for the persisted, at-scan-time snapshot semantics (#306/PR #336).
 *
 * "Open in Benchmarks" (issue #559): navigates to `/benchmarks` with
 * whatever identifiers this row actually carries. `AppliedAttestationResponse.control`
 * is documented as "always the fixed AttestationProfile profile name, not a
 * per-control id" (RunContracts.cs) — so `profile` is the only identifier a
 * row is guaranteed to have; a `site:{id}`/`target:{id}` `scope` additionally
 * supplies a real target/site ref for `applied` rows (never for `expired`
 * rows, which only carry a target NAME — `ExpiredAttestation.target` — not a
 * guid). The link is only ever built from identifiers that exist; it never
 * invents a target id to satisfy the deep link.
 */
import { useRouter } from "../../lib/router-context";
import { formatTimestamp, parseAttestationScope, type AppliedAttestation } from "./results";
import type { ExpiredAttestation } from "./useRunDetail";

function benchmarksHref(profile: string, targetRef: string | null): string {
	const params = new URLSearchParams({ profile });
	if (targetRef) {
		params.set("target", targetRef);
	}
	return `/benchmarks?${params.toString()}`;
}

export function AttestationsPanel({ expired, applied }: { expired: ExpiredAttestation[]; applied: AppliedAttestation[] | null }) {
	const { navigate } = useRouter();

	const firstApplied = applied && applied.length > 0 ? applied[0] : null;
	const firstExpired = expired.length > 0 ? expired[0] : null;

	let openInBenchmarksHref: string | null = null;
	if (firstApplied) {
		const { layer, ref } = parseAttestationScope(firstApplied.scope);
		openInBenchmarksHref = benchmarksHref(firstApplied.control, layer === "target" ? ref : null);
	} else if (firstExpired) {
		openInBenchmarksHref = benchmarksHref(firstExpired.profile, null);
	}

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
				disabled={!openInBenchmarksHref}
				title={openInBenchmarksHref ? undefined : "No attestation identifiers are available for this run to open in Benchmarks."}
				onClick={() => {
					if (openInBenchmarksHref) {
						navigate(openInBenchmarksHref);
					}
				}}
			>
				Open in Benchmarks
			</button>
		</div>
	);
}
