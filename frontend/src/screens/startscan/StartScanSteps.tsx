/**
 * StartScanSteps — the five step-panel components for the Start-a-Scan
 * wizard (issue #419 extraction from StartScanScreen.tsx, no behavior
 * change). Each step is a pure presentation component driven by props from
 * useScanWizard; StartScanScreen.tsx wires them together.
 */
import type { CredentialOption, Site } from "../configuration/sites";
import type { InventoryItem, ProfileOption } from "./startscan";
import type { CredentialMode, TargetSelection } from "./useScanWizard";

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
}) {
	return (
		<div className="start-scan-screen__panel">
			<div className="start-scan-screen__panel-title">Scope — inventory</div>

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
						{sel.loadingInventory && <div className="start-scan-screen__note start-scan-screen__tree-indent">Loading inventory…</div>}
						{!sel.loadingInventory && (sel.inventory === null || sel.inventory.length === 0) && (
							<div className="start-scan-screen__note start-scan-screen__tree-indent">
								No cached inventory — scanning the whole target.
							</div>
						)}
						{!sel.loadingInventory && sel.inventory !== null && sel.inventory.length > 0 && (
							<div className="start-scan-screen__tree-indent">
								{sel.inventory.map((item) => (
									<InventoryNode key={item.id} item={item} targetId={sel.target.id} selectedIds={sel.selectedItemIds} onToggle={onToggleItem} />
								))}
							</div>
						)}
					</div>
				))}
			</div>
		</div>
	);
}

function InventoryNode({
	item,
	targetId,
	selectedIds,
	onToggle,
}: {
	item: InventoryItem;
	targetId: string;
	selectedIds: Set<string>;
	onToggle: (targetId: string, itemId: string, on: boolean) => void;
}) {
	const checked = selectedIds.has(item.id);
	return (
		<div className="start-scan-screen__tree-node">
			<label className="start-scan-screen__tree-row">
				<input type="checkbox" checked={checked} onChange={(e) => onToggle(targetId, item.id, e.target.checked)} />
				<span className="mono">{item.name}</span>
				{item.build && <span className="start-scan-screen__tree-build">{item.build}</span>}
				{item.maintenance_mode && <span className="start-scan-screen__tree-maint">maintenance mode</span>}
			</label>
			{item.children.length > 0 && (
				<div className="start-scan-screen__tree-indent">
					{item.children.map((child) => (
						<InventoryNode key={child.id} item={child} targetId={targetId} selectedIds={selectedIds} onToggle={onToggle} />
					))}
				</div>
			)}
		</div>
	);
}

export function CredentialStep({
	mode,
	onModeChange,
	canUsePersonal,
	personalGate,
	credentialOptions,
	credentialOptionsError,
	serviceCredentialId,
	onServiceCredentialChange,
	personalUsername,
	onPersonalUsernameChange,
	personalSecret,
	onPersonalSecretChange,
}: {
	mode: CredentialMode;
	onModeChange: (m: CredentialMode) => void;
	canUsePersonal: boolean;
	personalGate: { disabled: boolean; style?: { opacity: number }; title?: string };
	credentialOptions: CredentialOption[];
	credentialOptionsError: string | null;
	serviceCredentialId: string;
	onServiceCredentialChange: (id: string) => void;
	personalUsername: string;
	onPersonalUsernameChange: (v: string) => void;
	personalSecret: string;
	onPersonalSecretChange: (v: string) => void;
}) {
	return (
		<div className="start-scan-screen__panel">
			<div className="start-scan-screen__panel-title">Credential</div>
			<div className="start-scan-screen__credential-cards">
				<label className="start-scan-screen__credential-card">
					<input type="radio" name="credential-mode" checked={mode === "service"} onChange={() => onModeChange("service")} />
					<div>
						<div className="start-scan-screen__credential-card-title">Service credential</div>
						<div className="start-scan-screen__note">A shared credential stored in Configuration.</div>
					</div>
				</label>
				<label className="start-scan-screen__credential-card" title={personalGate.title}>
					<input
						type="radio"
						name="credential-mode"
						checked={mode === "personal"}
						disabled={!canUsePersonal}
						onChange={() => onModeChange("personal")}
					/>
					<div style={personalGate.style}>
						<div className="start-scan-screen__credential-card-title">My credentials</div>
						<div className="start-scan-screen__note">
							Enter your own username/password now. Never stored — write-only for this run only.
						</div>
					</div>
				</label>
			</div>

			{mode === "service" && (
				<div className="start-scan-screen__field">
					{credentialOptionsError && <div className="start-scan-screen__error">{credentialOptionsError}</div>}
					<label>
						<span>Credential</span>
						<select value={serviceCredentialId} onChange={(e) => onServiceCredentialChange(e.target.value)}>
							<option value="">Select a credential…</option>
							{credentialOptions.map((c) => (
								<option key={c.id} value={c.id}>
									{c.name}
								</option>
							))}
						</select>
					</label>
				</div>
			)}

			{mode === "personal" && canUsePersonal && (
				<div className="start-scan-screen__field">
					<label>
						<span>Username</span>
						<input value={personalUsername} onChange={(e) => onPersonalUsernameChange(e.target.value)} autoComplete="off" />
					</label>
					<label>
						<span>Secret</span>
						<input
							type="password"
							value={personalSecret}
							onChange={(e) => onPersonalSecretChange(e.target.value)}
							autoComplete="new-password"
							placeholder="never stored — used for this run only"
						/>
					</label>
				</div>
			)}
		</div>
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
	credentialMode,
	credentialName,
	canConfirm,
	submitting,
	error,
	onConfirm,
}: {
	siteName: string;
	targetCount: number;
	totalTargets: number;
	profileName: string;
	credentialMode: CredentialMode;
	credentialName: string;
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
				<dt>Credential</dt>
				<dd className="mono">{credentialMode === "service" ? credentialName || "—" : credentialName}</dd>
				<dt>Run</dt>
				<dd className="mono">Run now</dd>
			</dl>
			{error && <div className="start-scan-screen__error">{error}</div>}
			<button type="button" className="start-scan-screen__submit" disabled={!canConfirm || submitting} onClick={onConfirm}>
				{submitting ? "Starting…" : "Start scan"}
			</button>
		</div>
	);
}
