/**
 * The EFFECTIVE card (issue #559 acceptance criterion: "Effective resolution
 * clearly shows which layer wins and the source version used") — one row
 * per doc kind, driven by `GET /config-docs/resolve?profile&target&kind`.
 * Requires a target (the endpoint's own contract: resolution is always
 * relative to one target, which pins its site too). Expired attestations
 * are shown as visibly inactive with the "control remains Open" explanation
 * (domain-model.md: "the control reports Open, the run logs a WARN").
 */
import { useEffect, useState } from "react";
import { ApiError } from "../../lib/api";
import { CONFIG_DOC_KINDS, describeLayer, formatTimestamp, isExpiredAttestation, resolveConfigDocs, type ConfigDocResolutionEntry } from "./benchmarks";
import "./BenchmarksScreen.css";

export function EffectiveResolutionPanel({
	profileKey,
	targetId,
	siteNameById,
	targetNameById,
}: {
	profileKey: string;
	targetId: string | null;
	siteNameById: Map<string, string>;
	targetNameById: Map<string, string>;
}) {
	const [entries, setEntries] = useState<ConfigDocResolutionEntry[]>([]);
	const [loading, setLoading] = useState(false);
	const [error, setError] = useState<string | null>(null);

	useEffect(() => {
		if (!targetId) {
			setEntries([]);
			return;
		}
		setLoading(true);
		setError(null);
		resolveConfigDocs({ profile: profileKey, target: targetId })
			.then(setEntries)
			.catch((err: unknown) => {
				setError(err instanceof ApiError ? err.message : "Could not resolve the effective configuration.");
			})
			.finally(() => setLoading(false));
	}, [profileKey, targetId]);

	if (!targetId) {
		return (
			<div className="bench-effective">
				<div className="bench-effective__title">EFFECTIVE</div>
				<div className="bench-layer__empty-note">Select a target to see which layer wins for it.</div>
			</div>
		);
	}

	return (
		<div className="bench-effective">
			<div className="bench-effective__title">EFFECTIVE</div>
			{loading && <div className="bench-layer__status">Resolving…</div>}
			{error && <div className="bench-layer__error">{error}</div>}
			{!loading &&
				!error &&
				CONFIG_DOC_KINDS.map(({ value, label }) => {
					const entry = entries.find((e) => e.kind === value);
					const expired = entry ? isExpiredAttestation(entry) : false;
					return (
						<div key={value} className={`bench-effective__row ${expired ? "bench-effective__row--expired" : ""}`}>
							<div className="bench-effective__row-kind">{label}</div>
							{!entry || (!entry.doc_id && !expired) ? (
								<div className="bench-effective__row-body">Nothing defined at any layer for this target.</div>
							) : expired ? (
								<div className="bench-effective__row-body">
									<span className="bench-effective__badge bench-effective__badge--expired">EXPIRED — NOT APPLIED</span>
									<div className="bench-layer__empty-note">
										Expired {formatTimestamp(entry.attestation_expires_at)} — the control reports Open and the run logs a warning. Create a new
										version to renew this waiver.
									</div>
								</div>
							) : (
								<div className="bench-effective__row-body">
									<span className="bench-effective__badge">{describeLayer(entry.layer ?? "global", siteNameById, targetNameById)}</span>
									<span className="bench-effective__row-meta">
										v{entry.version} · {entry.author} · updated {formatTimestamp(entry.updated_at)}
									</span>
								</div>
							)}
						</div>
					);
				})}
		</div>
	);
}
