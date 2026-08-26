/**
 * StartScanSteps — the five step-panel components for the Start-a-Scan
 * wizard (issue #419 extraction from StartScanScreen.tsx, no behavior
 * change). Each step is a pure presentation component driven by props from
 * useScanWizard; StartScanScreen.tsx wires them together.
 */
import { useEffect, useRef, useState } from "react";
import type { CredentialBindingGap, ScopeOmission } from "../../lib/api";
import { CREDENTIAL_PURPOSE_SATISFYING_TYPES, purposeLabel, requiredScanPurposes, type CredentialPurpose } from "../configuration/credential-purposes";
import type { CredentialOption, Site } from "../configuration/sites";
import { isSelectableComponent, type ComponentTreeNode, type ProfileOption } from "./startscan";
import { overrideKey, type CoverageRow, type CredentialMode, type OverrideEntry, type TargetSelection } from "./useScanWizard";

export function SiteStep({
	sites,
	loading,
	error,
	siteId,
	onSelect,
}: {
	sites: Site[];
	loading: boolean;
	error: string | null;
	siteId: string;
	onSelect: (id: string) => void;
}) {
	return (
		<div className="start-scan-screen__panel">
			<div className="start-scan-screen__panel-title">Select a site</div>
			{loading && <div className="start-scan-screen__note">Loading sites…</div>}
			{error && <div className="start-scan-screen__error">{error}</div>}
			{!loading && !error && sites.length === 0 && <div className="start-scan-screen__note">No sites configured yet.</div>}
			<div className="start-scan-screen__site-list">
				{sites.map((site) => (
					<label key={site.id} className="start-scan-screen__site-option">
						<input type="radio" name="scan-site" checked={siteId === site.id} onChange={() => onSelect(site.id)} />
						<span className="mono">{site.name}</span>
						{site.description && <span className="start-scan-screen__site-desc">{site.description}</span>}
					</label>
				))}
			</div>
		</div>
	);
}

/** Issue #733: reasons a `ScopeOmission.reason` maps to guidance text — the checkbox-tree counterpart of `bindingGapMessage` below. Never free text on the wire; this is the one place the closed set is translated for the operator. */
function scopeOmissionMessage(omission: ScopeOmission): string {
	switch (omission.reason) {
		case "component_not_found":
			return "This component no longer exists — refresh the scope and reselect.";
		case "component_not_in_scope":
			return "This component is not part of the named target — refresh the scope and reselect.";
		case "component_absent":
			return "This component was not seen on the last discovery pass — refresh the scope before selecting it.";
		case "component_retired":
			return "This component has been retired and can no longer be scanned — remove it from the selection.";
		case "fact_conflict":
			return "This component has an unresolved configured/discovered version conflict — resolve it before scanning.";
		case "catalog_incompatible":
			return "This component has no compatible catalog baseline and cannot be scanned.";
		case "target_not_found":
			return "This target no longer exists — refresh the scope and reselect.";
		default:
			return omission.detail || omission.reason;
	}
}

/** Issue #733 AC: "Stale or removed selections fail with actionable refresh guidance rather than silently widening scope" — a `400 no_runnable_component`'s `scope_omissions`, rendered above the tree so the operator can refresh and reselect instead of guessing why submission failed. */
function ScopeOmissionBanner({ omissions }: { omissions: ScopeOmission[] }) {
	if (omissions.length === 0) {
		return null;
	}
	return (
		<div className="start-scan-screen__error" role="alert">
			<div>The selected scope has no runnable component. Refresh this target&apos;s scope and reselect:</div>
			<ul>
				{omissions.map((omission, i) => (
					<li key={i}>{scopeOmissionMessage(omission)}</li>
				))}
			</ul>
		</div>
	);
}

/** One node's tri-state checkbox render state, derived purely from its own selectable descendants (never stored — see `useScanWizard.toggleInventoryItem`'s doc comment). A leaf with no selectable descendants of its own reports its own membership in `selectedIds`. */
function componentCheckState(node: ComponentTreeNode, selectedIds: Set<string>): { checked: boolean; indeterminate: boolean } {
	const selectableDescendants = collectSelectable(node);
	if (selectableDescendants.length === 0) {
		return { checked: false, indeterminate: false };
	}
	const selectedCount = selectableDescendants.filter((id) => selectedIds.has(id)).length;
	if (selectedCount === 0) {
		return { checked: false, indeterminate: false };
	}
	if (selectedCount === selectableDescendants.length) {
		return { checked: true, indeterminate: false };
	}
	return { checked: false, indeterminate: true };
}

/** Every selectable (active) id in `node`'s own subtree, including itself. */
function collectSelectable(node: ComponentTreeNode): string[] {
	const out: string[] = [];
	if (isSelectableComponent(node)) {
		out.push(node.id);
	}
	for (const child of node.children) {
		out.push(...collectSelectable(child));
	}
	return out;
}

export function ScopeStep({
	selections,
	loading,
	error,
	onToggleTarget,
	onToggleItem,
	profiles,
	profilesLoading,
	profilesError,
	profileId,
	onProfileChange,
	scopeOmissionErrors,
	hasEmptyExplicitSelection,
}: {
	selections: TargetSelection[];
	loading: boolean;
	error: string | null;
	onToggleTarget: (targetId: string, on: boolean) => void;
	onToggleItem: (targetId: string, itemId: string, on: boolean) => void;
	profiles: ProfileOption[];
	profilesLoading: boolean;
	profilesError: string | null;
	profileId: string;
	onProfileChange: (id: string) => void;
	scopeOmissionErrors: ScopeOmission[];
	hasEmptyExplicitSelection: boolean;
}) {
	return (
		<div className="start-scan-screen__panel">
			<div className="start-scan-screen__panel-title">Scope — components</div>

			{/* Issue #639: profile selection — which pulled compliance-content
			 * profile (GET /profiles) this scan executes against. Required
			 * before the wizard can advance to Confirm (useScanWizard's
			 * canConfirm). */}
			<div className="start-scan-screen__field">
				{profilesLoading && <div className="start-scan-screen__note">Loading profiles…</div>}
				{profilesError && <div className="start-scan-screen__error">{profilesError}</div>}
				{!profilesLoading && !profilesError && profiles.length === 0 && (
					<div className="start-scan-screen__note">
						No compliance content pulled yet — pull content from Compliance Content before starting a scan.
					</div>
				)}
				{!profilesLoading && !profilesError && profiles.length > 0 && (
					<label>
						<span>Profile</span>
						<select value={profileId} onChange={(e) => onProfileChange(e.target.value)}>
							<option value="">Select a profile…</option>
							{profiles.map((p) => (
								<option key={p.id} value={p.id}>
									{p.name}
									{p.version ? ` (${p.version})` : ""}
								</option>
							))}
						</select>
					</label>
				)}
			</div>

			<ScopeOmissionBanner omissions={scopeOmissionErrors} />

			{loading && <div className="start-scan-screen__note">Loading targets…</div>}
			{error && <div className="start-scan-screen__error">{error}</div>}
			{!loading && !error && selections.length === 0 && <div className="start-scan-screen__note">This site has no targets.</div>}
			<div className="start-scan-screen__tree">
				{selections.map((sel) => (
					<div key={sel.target.id} className="start-scan-screen__tree-target">
						<label className="start-scan-screen__tree-row">
							<input type="checkbox" checked={sel.targetSelected} onChange={(e) => onToggleTarget(sel.target.id, e.target.checked)} />
							<span className="mono">{sel.target.name}</span>
							<span className="start-scan-screen__tree-kind">{sel.target.kind}</span>
						</label>
						{sel.loadingInventory && <div className="start-scan-screen__note start-scan-screen__tree-indent">Loading components…</div>}
						{!sel.loadingInventory && (sel.componentTree === null || sel.componentTree.length === 0) && (
							<div className="start-scan-screen__note start-scan-screen__tree-indent">
								No cached components — scanning the whole target.
							</div>
						)}
						{!sel.loadingInventory && sel.componentTree !== null && sel.componentTree.length > 0 && (
							<div className="start-scan-screen__tree-indent">
								{sel.componentTree.map((node) => (
									<ComponentNode key={node.id} node={node} targetId={sel.target.id} selectedIds={sel.selectedItemIds} onToggle={onToggleItem} />
								))}
							</div>
						)}
					</div>
				))}
			</div>

			{/* Issue #733 AC: "An empty explicit selection is presented honestly" —
			 * warns, never blocks Next/Confirm and never silently widens back to
			 * the whole site (the backend honors a deliberately empty explicit
			 * selection as an intentional empty plan). */}
			{hasEmptyExplicitSelection && (
				<div className="start-scan-screen__note" role="status">
					No components are selected for at least one target with known components. The scan will run with an intentionally empty
					plan for that target — it will not fall back to scanning everything.
				</div>
			)}
		</div>
	);
}

function ComponentNode({
	node,
	targetId,
	selectedIds,
	onToggle,
}: {
	node: ComponentTreeNode;
	targetId: string;
	selectedIds: Set<string>;
	onToggle: (targetId: string, itemId: string, on: boolean) => void;
}) {
	const { checked, indeterminate } = componentCheckState(node, selectedIds);
	const selectable = isSelectableComponent(node);
	const checkboxRef = useRef<HTMLInputElement>(null);
	useEffect(() => {
		if (checkboxRef.current) {
			checkboxRef.current.indeterminate = indeterminate;
		}
	}, [indeterminate]);

	return (
		<div className="start-scan-screen__tree-node">
			<label className="start-scan-screen__tree-row" title={selectable ? undefined : `${node.lifecycle} — not selectable`}>
				<input
					ref={checkboxRef}
					type="checkbox"
					checked={checked}
					disabled={!selectable}
					onChange={(e) => onToggle(targetId, node.id, e.target.checked)}
				/>
				<span className="mono">{node.display_name}</span>
				<span className="start-scan-screen__tree-kind">{node.catalog_component_key}</span>
				{node.lifecycle !== "active" && <span className="start-scan-screen__tree-maint">{node.lifecycle}</span>}
				{node.fact_conflict && <span className="start-scan-screen__tree-maint">version conflict</span>}
			</label>
			{node.children.length > 0 && (
				<div className="start-scan-screen__tree-indent">
					{node.children.map((child) => (
						<ComponentNode key={child.id} node={child} targetId={targetId} selectedIds={selectedIds} onToggle={onToggle} />
					))}
				</div>
			)}
		</div>
	);
}

/**
 * Issue #587 (epic #582): Credential step. Defaults to "Use credentials
 * assigned to each target" — the coverage table reads straight off the
 * selected targets' own `bindings` (already on the wire, issue #661), no
 * extra request. Switching to "Customize per target/purpose" reveals one row
 * per (target, purpose) the scan cares about, each with its own saved/ad-hoc
 * picker plus a bulk-apply control per purpose column (compatible credential
 * types only, per the shared matrix). `bindingGapErrors` (a 400
 * `credential_binding_gaps` from the last submit attempt) are mapped onto the
 * matching row instead of rendered as a generic toast (issue #587 AC).
 */
export function CredentialStep({
	mode,
	onModeChange,
	targets,
	coverage,
	missingCoverage,
	bindingGapErrors,
	overrides,
	onSetSavedOverride,
	onSetAdHocOverride,
	onClearOverride,
	onBulkApplySaved,
	canUseAdHoc,
	adHocGate,
	credentialOptions,
	credentialOptionsError,
}: {
	mode: CredentialMode;
	onModeChange: (m: CredentialMode) => void;
	targets: { id: string; name: string; kind: string }[];
	coverage: CoverageRow[];
	missingCoverage: CoverageRow[];
	bindingGapErrors: CredentialBindingGap[];
	overrides: Map<string, OverrideEntry>;
	onSetSavedOverride: (targetId: string, purpose: CredentialPurpose, credentialId: string) => void;
	onSetAdHocOverride: (targetId: string, purpose: CredentialPurpose, username: string, secret: string) => void;
	onClearOverride: (targetId: string, purpose: CredentialPurpose) => void;
	onBulkApplySaved: (purpose: CredentialPurpose, credentialId: string) => void;
	canUseAdHoc: boolean;
	adHocGate: { disabled: boolean; style?: { opacity: number }; title?: string };
	credentialOptions: CredentialOption[];
	credentialOptionsError: string | null;
}) {
	// One row per (target, purpose) any selected target's scan cares about —
	// drives both the "Customize" table and the bulk-apply purpose columns.
	const purposesInScope = Array.from(new Set(targets.flatMap((t) => requiredScanPurposes(t.kind)))) as CredentialPurpose[];

	const gapsByKey = new Map<string, CredentialBindingGap[]>();
	for (const gap of bindingGapErrors) {
		const key = overrideKey(gap.target_id, gap.purpose as CredentialPurpose);
		const list = gapsByKey.get(key) ?? [];
		list.push(gap);
		gapsByKey.set(key, list);
	}

	return (
		<div className="start-scan-screen__panel">
			<div className="start-scan-screen__panel-title">Credential</div>
			<div className="start-scan-screen__credential-cards">
				<label className="start-scan-screen__credential-card">
					<input type="radio" name="credential-mode" checked={mode === "assigned"} onChange={() => onModeChange("assigned")} />
					<div>
						<div className="start-scan-screen__credential-card-title">Use credentials assigned to each target</div>
						<div className="start-scan-screen__note">Default. Each target's own credential bindings are used.</div>
					</div>
				</label>
				<label className="start-scan-screen__credential-card">
					<input type="radio" name="credential-mode" checked={mode === "override"} onChange={() => onModeChange("override")} />
					<div>
						<div className="start-scan-screen__credential-card-title">Customize per target/purpose</div>
						<div className="start-scan-screen__note">Override specific targets/purposes with a saved or ad hoc credential.</div>
					</div>
				</label>
			</div>

			{credentialOptionsError && <div className="start-scan-screen__error">{credentialOptionsError}</div>}

			{/* Coverage summary — issue #587 AC: shown before submission, never a
			 * generic toast, aria-live so a change while customizing announces. */}
			<div aria-live="polite" className="start-scan-screen__coverage">
				<div className="start-scan-screen__field-title">Coverage</div>
				{coverage.length === 0 && <div className="start-scan-screen__note">Select targets in Scope to see required credentials.</div>}
				{coverage.length > 0 && (
					<table className="start-scan-screen__coverage-table">
						<thead>
							<tr>
								<th>Target</th>
								<th>Purpose</th>
								<th>Source</th>
							</tr>
						</thead>
						<tbody>
							{coverage.map((row) => {
								const key = overrideKey(row.targetId, row.purpose);
								const gaps = gapsByKey.get(key) ?? [];
								return (
									<tr key={key}>
										<td className="mono">{row.targetName}</td>
										<td>{purposeLabel(row.purpose)}</td>
										<td>
											{row.source === "missing" ? (
												<span className="start-scan-screen__error-text">
													Missing required binding —{" "}
													<a href="/config">bind a credential for this target</a>
												</span>
											) : row.source === "override" ? (
												`Override: ${row.credentialName ?? "—"}`
											) : (
												`Assigned: ${row.credentialName ?? "—"}`
											)}
											{gaps.map((gap, i) => (
												<div key={i} className="start-scan-screen__error-text">
													{bindingGapMessage(gap)}
												</div>
											))}
										</td>
									</tr>
								);
							})}
						</tbody>
					</table>
				)}
				{missingCoverage.length > 0 && (
					<div className="start-scan-screen__error" role="status">
						{missingCoverage.length} required credential{missingCoverage.length === 1 ? "" : "s"} missing — resolve before starting the scan.
					</div>
				)}
			</div>

			{mode === "override" && (
				<div className="start-scan-screen__field">
					<div className="start-scan-screen__field-title">Bulk apply a saved credential</div>
					{purposesInScope.map((purpose) => {
						const compatibleTypes = CREDENTIAL_PURPOSE_SATISFYING_TYPES[purpose];
						const compatibleCredentials = credentialOptions.filter((c) => compatibleTypes.includes(c.credential_type as never));
						return (
							<label key={purpose}>
								<span>{purposeLabel(purpose)} (applies to every compatible selected target)</span>
								<select
									aria-label={`Bulk apply ${purposeLabel(purpose)} credential`}
									defaultValue=""
									onChange={(e) => {
										if (e.target.value) {
											onBulkApplySaved(purpose, e.target.value);
											e.target.value = "";
										}
									}}
								>
									<option value="">Select a credential…</option>
									{compatibleCredentials.map((c) => (
										<option key={c.id} value={c.id}>
											{c.name}
										</option>
									))}
								</select>
							</label>
						);
					})}

					<div className="start-scan-screen__field-title">Per-target overrides</div>
					<table className="start-scan-screen__coverage-table">
						<thead>
							<tr>
								<th>Target</th>
								<th>Purpose</th>
								<th>Override</th>
							</tr>
						</thead>
						<tbody>
							{targets.flatMap((target) =>
								requiredScanPurposes(target.kind).map((purpose) => {
									const key = overrideKey(target.id, purpose);
									const override = overrides.get(key);
									const compatibleTypes = CREDENTIAL_PURPOSE_SATISFYING_TYPES[purpose];
									const compatibleCredentials = credentialOptions.filter((c) => compatibleTypes.includes(c.credential_type as never));
									return (
										<tr key={key}>
											<td className="mono">{target.name}</td>
											<td>{purposeLabel(purpose)}</td>
											<td>
												<select
													aria-label={`${target.name} ${purposeLabel(purpose)} saved credential`}
													value={override?.kind === "saved" ? override.credentialId : ""}
													onChange={(e) => {
														if (e.target.value) {
															onSetSavedOverride(target.id, purpose, e.target.value);
														} else if (override?.kind === "saved") {
															onClearOverride(target.id, purpose);
														}
													}}
												>
													<option value="">Use assigned binding</option>
													{compatibleCredentials.map((c) => (
														<option key={c.id} value={c.id}>
															{c.name}
														</option>
													))}
												</select>

												{canUseAdHoc && (
													<AdHocOverrideFields
														targetId={target.id}
														purpose={purpose}
														active={override?.kind === "adhoc"}
														onSet={onSetAdHocOverride}
														onClear={onClearOverride}
													/>
												)}
												{!canUseAdHoc && (
													<span className="start-scan-screen__note" title={adHocGate.title}>
														Ad hoc credentials require Operator or higher.
													</span>
												)}
											</td>
										</tr>
									);
								}),
							)}
						</tbody>
					</table>
				</div>
			)}
		</div>
	);
}

function bindingGapMessage(gap: CredentialBindingGap): string {
	switch (gap.reason) {
		case "missing_binding":
			return "No credential bound for this purpose.";
		case "incompatible_credential_type":
			return "The selected credential's type is not compatible with this purpose.";
		case "credential_not_found":
			return "The selected credential no longer exists.";
		case "target_not_in_scope":
			return "This target is not part of the current scan scope.";
		case "purpose_not_applicable":
			return "This purpose does not apply to this target's kind.";
		case "duplicate_override":
			return "This target/purpose was overridden more than once.";
		default:
			return gap.reason;
	}
}

/**
 * One (target, purpose) row's ad hoc entry — local draft state so a
 * partially-typed username/secret does not commit to the shared override map
 * (and therefore the request body) until both fields are non-empty; clearing
 * either field back to empty clears the override entirely. This component's
 * own `username`/`secret` state is the only place either value lives besides
 * the parent's override map — both are wiped the instant a field goes empty
 * or the wizard's `useScanWizard.submit` clears every ad hoc entry after a
 * successful POST, mirroring the retired personal-credential tier's
 * write-only discipline.
 */
function AdHocOverrideFields({
	targetId,
	purpose,
	active,
	onSet,
	onClear,
}: {
	targetId: string;
	purpose: CredentialPurpose;
	active: boolean;
	onSet: (targetId: string, purpose: CredentialPurpose, username: string, secret: string) => void;
	onClear: (targetId: string, purpose: CredentialPurpose) => void;
}) {
	const [username, setUsername] = useState("");
	const [secret, setSecret] = useState("");

	const commit = (nextUsername: string, nextSecret: string) => {
		if (nextUsername === "" || nextSecret === "") {
			if (active) {
				onClear(targetId, purpose);
			}
			return;
		}
		onSet(targetId, purpose, nextUsername, nextSecret);
	};

	return (
		<span className="start-scan-screen__adhoc-fields">
			<label>
				<span>Ad hoc username</span>
				<input
					aria-label={`${purpose} ad hoc username for this target`}
					autoComplete="off"
					value={username}
					onChange={(e) => {
						setUsername(e.target.value);
						commit(e.target.value, secret);
					}}
				/>
			</label>
			<label>
				<span>Ad hoc secret</span>
				<input
					type="password"
					aria-label={`${purpose} ad hoc secret for this target`}
					autoComplete="new-password"
					placeholder="never stored — used for this run only"
					value={secret}
					onChange={(e) => {
						setSecret(e.target.value);
						commit(username, e.target.value);
					}}
				/>
			</label>
			{active && <span className="start-scan-screen__note">Ad hoc credential set for this pair.</span>}
		</span>
	);
}

export function ScheduleStep() {
	return (
		<div className="start-scan-screen__panel">
			<div className="start-scan-screen__panel-title">Run</div>
			<div className="start-scan-screen__schedule-options">
				<label className="start-scan-screen__credential-card">
					<input type="radio" name="run-when" checked readOnly />
					<div>
						<div className="start-scan-screen__credential-card-title">Run now</div>
					</div>
				</label>
				<label
					className="start-scan-screen__credential-card"
					title="Scheduling ships in M3 — scans are read-only and schedulable, but this build only supports Run now."
				>
					<input type="radio" name="run-when" disabled style={{ opacity: 0.42 }} />
					<div style={{ opacity: 0.42 }}>
						<div className="start-scan-screen__credential-card-title">Schedule…</div>
						<div className="start-scan-screen__note">Coming in a future milestone (M3).</div>
					</div>
				</label>
			</div>
		</div>
	);
}

export function ConfirmStep({
	siteName,
	targetCount,
	totalTargets,
	profileName,
	credentialSummary,
	canConfirm,
	submitting,
	error,
	onConfirm,
}: {
	siteName: string;
	targetCount: number;
	totalTargets: number;
	profileName: string;
	credentialSummary: string;
	canConfirm: boolean;
	submitting: boolean;
	error: string | null;
	onConfirm: () => void;
}) {
	return (
		<div className="start-scan-screen__panel">
			<div className="start-scan-screen__panel-title">Confirm</div>
			<dl className="start-scan-screen__summary">
				<dt>Site</dt>
				<dd className="mono">{siteName || "—"}</dd>
				<dt>Targets</dt>
				<dd className="mono">
					{targetCount} / {totalTargets}
				</dd>
				<dt>Profile</dt>
				<dd className="mono">{profileName || "—"}</dd>
				<dt>Credentials</dt>
				<dd className="mono">{credentialSummary}</dd>
				<dt>Run</dt>
				<dd className="mono">Run now</dd>
			</dl>
			<div aria-live="polite">{error && <div className="start-scan-screen__error">{error}</div>}</div>
			<button type="button" className="start-scan-screen__submit" disabled={!canConfirm || submitting} onClick={onConfirm}>
				{submitting ? "Starting…" : "Start scan"}
			</button>
		</div>
	);
}
