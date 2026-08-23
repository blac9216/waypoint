/**
 * One (profile, kind) slot's Global/Site/Target layer editor — the core of
 * the Benchmarks screen (issue #559). Renders:
 *   - a layer tab strip (Global always available; Site/Target require a
 *     picked site/target — `siteId`/`targetId` props),
 *   - the selected layer's stored YAML (or "not defined at this layer"),
 *   - immutable version history (author/timestamp) with read-only inspect
 *     of any prior version,
 *   - an Admin-only editor that creates a new version through the existing
 *     `PUT /config-docs/{id}` API, never mutating history in place,
 *   - inline validation errors (`invalid_yaml`/`validation_error` from the
 *     API) that leave the draft exactly as typed — the draft textarea's
 *     state is never overwritten by a failed save, and the body is sent
 *     byte-for-byte (no client-side YAML parse/reformat) so the backend's
 *     "byte-stable" version semantics hold (issue #559 Risks:
 *     "avoid silently normalizing operator documents").
 *
 * Per-control waiver authoring is a labeled hint inside the attestation
 * editor, not a structured form — see benchmarks.ts's module doc for why
 * (no per-control storage in the config-doc schema).
 */
import { useCallback, useEffect, useState } from "react";
import { ApiError } from "../../lib/api";
import { roleAtLeast, roleGateProps, type Role } from "../../lib/roles";
import {
	fetchConfigDocVersion,
	fetchConfigDocVersions,
	fetchConfigDocs,
	formatLayer,
	formatTimestamp,
	saveConfigDoc,
	type ConfigDocKind,
	type ConfigDocSummary,
	type ConfigDocVersionSummary,
	type LayerKind,
} from "./benchmarks";
import "./BenchmarksScreen.css";

const LAYER_TABS: { value: LayerKind; label: string }[] = [
	{ value: "global", label: "Global" },
	{ value: "site", label: "Site" },
	{ value: "target", label: "Target" },
];

const ATTESTATION_PLACEHOLDER = `# Attestation (waiver) YAML — the backend stores this document as-is.
# Include, per the profile's expectations:
#   - the affected control id(s) and scope
#   - justification
#   - expiry (expired attestations are not applied — the control stays Open)
controls:
  - id: SV-000000r0_rule
    justification: "Compensating control documented in <reference>."
    expires: "2026-12-31T00:00:00Z"
`;

export function LayerDocPanel({
	profileKey,
	kind,
	role,
	siteId,
	siteName,
	targetId,
	targetName,
}: {
	profileKey: string;
	kind: ConfigDocKind;
	role: Role;
	siteId: string | null;
	siteName: string | null;
	targetId: string | null;
	targetName: string | null;
}) {
	const [layer, setLayer] = useState<LayerKind>("global");
	const [docsBySlot, setDocsBySlot] = useState<Map<string, ConfigDocSummary>>(new Map());
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);
	const [versions, setVersions] = useState<ConfigDocVersionSummary[]>([]);
	const [inspectingVersion, setInspectingVersion] = useState<ConfigDocVersionSummary | null>(null);
	const [editing, setEditing] = useState(false);
	const [draft, setDraft] = useState("");
	const [saving, setSaving] = useState(false);
	const [saveError, setSaveError] = useState<string | null>(null);

	const canWrite = roleAtLeast(role, "Admin");
	const writeGate = roleGateProps(role, "Admin", `Requires Admin — this action is not available to ${role}`);

	const layerRef = layer === "site" ? siteId : layer === "target" ? targetId : null;
	const layerWireValue = layer === "global" ? "global" : layerRef ? formatLayer(layer, layerRef) : null;
	const currentDoc = layerWireValue ? docsBySlot.get(layerWireValue) : undefined;

	const load = useCallback(() => {
		setLoading(true);
		setLoadError(null);
		fetchConfigDocs({ kind, profile: profileKey })
			.then((docs) => {
				setDocsBySlot(new Map(docs.map((d) => [d.layer, d])));
			})
			.catch((err: unknown) => {
				setLoadError(err instanceof ApiError ? err.message : "Could not load config documents.");
			})
			.finally(() => setLoading(false));
	}, [kind, profileKey]);

	useEffect(() => {
		load();
	}, [load]);

	useEffect(() => {
		setEditing(false);
		setSaveError(null);
		setInspectingVersion(null);
		setVersions([]);
		if (currentDoc) {
			fetchConfigDocVersions(currentDoc.id)
				.then(setVersions)
				.catch(() => setVersions([]));
		}
	}, [currentDoc]);

	const startEdit = () => {
		setDraft(currentDoc ? currentDoc.body : kind === "attestation" ? ATTESTATION_PLACEHOLDER : "");
		setSaveError(null);
		setEditing(true);
	};

	const submit = async () => {
		if (!layerWireValue) {
			return;
		}
		setSaving(true);
		setSaveError(null);
		try {
			const id = currentDoc?.id ?? crypto.randomUUID();
			const saved = await saveConfigDoc(id, {
				kind: currentDoc ? undefined : kind,
				profile: currentDoc ? undefined : profileKey,
				layer: currentDoc ? undefined : layerWireValue,
				body: draft,
			});
			setDocsBySlot((prev) => new Map(prev).set(layerWireValue, saved));
			setEditing(false);
			// Draft is intentionally left as-is until a fresh edit starts — a
			// successful save already replaced `currentDoc`, so the next
			// `startEdit()` will re-seed from the new saved body.
			fetchConfigDocVersions(saved.id)
				.then(setVersions)
				.catch(() => {});
		} catch (err) {
			// Inline, and the draft is untouched — `draft` state is not reset on
			// failure, so the operator's YAML is never lost to a validation error
			// (issue #559 acceptance criterion).
			setSaveError(err instanceof ApiError ? err.message : "Could not save this version.");
		} finally {
			setSaving(false);
		}
	};

	const inspectVersion = (version: ConfigDocVersionSummary) => {
		if (!currentDoc) {
			return;
		}
		if (version.version === currentDoc.version) {
			setInspectingVersion(null);
			return;
		}
		fetchConfigDocVersion(currentDoc.id, version.version)
			.then(setInspectingVersion)
			.catch(() => setInspectingVersion(version));
	};

	const layerAvailable = (value: LayerKind) => value === "global" || (value === "site" ? !!siteId : !!targetId);

	return (
		<div className="bench-layer">
			<div className="bench-layer__tabs">
				{LAYER_TABS.map((tab) => {
					const available = layerAvailable(tab.value);
					const label =
						tab.value === "site" && siteName ? `Site — ${siteName}` : tab.value === "target" && targetName ? `Target — ${targetName}` : tab.label;
					return (
						<button
							key={tab.value}
							type="button"
							disabled={!available}
							title={available ? undefined : `Select a ${tab.value} to view its ${tab.value} layer`}
							className={`bench-layer__tab ${layer === tab.value ? "is-active" : ""}`}
							onClick={() => setLayer(tab.value)}
						>
							{label}
						</button>
					);
				})}
			</div>

			{loading && <div className="bench-layer__status">Loading…</div>}
			{loadError && <div className="bench-layer__error">{loadError}</div>}

			{!loading && !loadError && (
				<>
					{!currentDoc && !editing && (
						<div className="bench-layer__empty">
							<div>No {kind} document defined at this layer.</div>
							<div className="bench-layer__empty-note">
								{layer === "global"
									? "No default is set — Site/Target layers (or nothing) will resolve for this profile."
									: "This layer inherits from a less specific layer at resolution time (see EFFECTIVE below), unless a document is created here."}
							</div>
							<button type="button" {...writeGate} onClick={startEdit}>
								Create version at this layer
							</button>
						</div>
					)}

					{currentDoc && !editing && (
						<div className="bench-layer__doc">
							<div className="bench-layer__doc-meta">
								<span>
									v{currentDoc.version} · {currentDoc.author} · {formatTimestamp(currentDoc.updated_at)}
								</span>
								<button type="button" {...writeGate} onClick={startEdit}>
									Create new version
								</button>
							</div>
							<pre className="bench-layer__yaml">{currentDoc.body}</pre>
						</div>
					)}

					{editing && (
						<div className="bench-layer__editor">
							<textarea
								className="bench-layer__textarea mono"
								value={draft}
								onChange={(event) => setDraft(event.target.value)}
								spellCheck={false}
								disabled={!canWrite || saving}
							/>
							{saveError && <div className="bench-layer__error">{saveError}</div>}
							<div className="bench-layer__editor-actions">
								<button type="button" onClick={() => setEditing(false)} disabled={saving}>
									Cancel
								</button>
								<button type="button" className="bench-layer__submit" {...writeGate} onClick={submit} disabled={saving || !canWrite}>
									{saving ? "Saving…" : "Save new version"}
								</button>
							</div>
						</div>
					)}

					<div className="bench-layer__history">
						<div className="bench-layer__history-title">VERSION HISTORY</div>
						{versions.length === 0 && <div className="bench-layer__empty-note">No versions at this layer yet.</div>}
						{versions
							.slice()
							.sort((a, b) => b.version - a.version)
							.map((v) => (
								<div key={v.version} className="bench-layer__history-row">
									<span className="mono">v{v.version}</span>
									<span>{v.author}</span>
									<span>{formatTimestamp(v.created_at)}</span>
									<button type="button" onClick={() => inspectVersion(v)}>
										{inspectingVersion?.version === v.version ? "Hide" : "Inspect"}
									</button>
								</div>
							))}
						{inspectingVersion && (
							<div className="bench-layer__doc bench-layer__doc--readonly">
								<div className="bench-layer__doc-meta">
									<span>
										Read-only — v{inspectingVersion.version} · {inspectingVersion.author} · {formatTimestamp(inspectingVersion.created_at)}
									</span>
								</div>
								<pre className="bench-layer__yaml">{inspectingVersion.body}</pre>
							</div>
						)}
					</div>
				</>
			)}
		</div>
	);
}
