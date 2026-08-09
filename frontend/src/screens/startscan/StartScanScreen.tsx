/**
 * Start a Scan — five-step wizard (docs/ui/prototype/README.md screen 3;
 * issue #284, second sub-issue of the #26 split; PR #288's Live Run view is
 * the destination after confirm, PR #285's run controls are out of scope
 * here).
 *
 * Steps: site -> scope (inventory checkbox tree, target-level fallback) ->
 * credential (service picker, Cyber+, or ADR-0011 enter-now personal,
 * Operator+) -> run/schedule stub (disabled; scheduling is M3) -> confirm
 * (summary + POST /runs -> navigate to Live Run with the new run id).
 *
 * Role gate: the whole flow is Cyber+ (README "Roles & Permissions" —
 * "Cyber + initiate scans using assigned service credentials"). A Viewer
 * sees the entry rendered visible-but-disabled by the router's screen-level
 * guard (redirects to Dashboard) plus this screen's own gate for anyone who
 * lands here directly with an insufficient role (e.g. a stale tab after a
 * role downgrade).
 */
import { useCallback, useEffect, useMemo, useState } from "react";
import { useAuth } from "../../lib/auth-context";
import { ApiError } from "../../lib/api";
import { roleAtLeast, roleGateProps } from "../../lib/roles";
import { useRouter } from "../../lib/router-context";
import { fetchSites, fetchTargets, type Site, type Target } from "../configuration/sites";
import { fetchCredentialOptions, type CredentialOption } from "../configuration/sites";
import { createScanRun, fetchTargetInventory, flattenInventory, type InventoryItem } from "./startscan";
import "./StartScanScreen.css";

type StepKey = "site" | "scope" | "credential" | "schedule" | "confirm";

const STEPS: { key: StepKey; label: string }[] = [
	{ key: "site", label: "Site" },
	{ key: "scope", label: "Scope" },
	{ key: "credential", label: "Credential" },
	{ key: "schedule", label: "Schedule" },
	{ key: "confirm", label: "Confirm" },
];

type CredentialMode = "service" | "personal";

/** One target's selection state for the scope step. `"all"` means every
 * inventory item under the target is included (also the state a target with
 * empty inventory falls back to when the whole target is toggled on),
 * `"partial"` drives the tri-state parent checkboxes, `"none"` excludes it. */
interface TargetSelection {
	target: Target;
	inventory: InventoryItem[] | null;
	loadingInventory: boolean;
	/** Selected inventory item ids; ignored (target-level fallback) when
	 * `inventory` is an empty array. */
	selectedItemIds: Set<string>;
	targetSelected: boolean;
}

export function StartScanScreen() {
	const { user } = useAuth();
	const { navigate } = useRouter();
	const [step, setStep] = useState<StepKey>("site");

	const allowed = user ? roleAtLeast(user.role, "Cyber") : false;
	const gate = user ? roleGateProps(user.role, "Cyber", "Requires Cyber or higher — scans are not available to your role") : { disabled: true };

	// -- step 1: site --------------------------------------------------
	const [sites, setSites] = useState<Site[]>([]);
	const [sitesLoading, setSitesLoading] = useState(true);
	const [sitesError, setSitesError] = useState<string | null>(null);
	const [siteId, setSiteId] = useState<string>("");

	useEffect(() => {
		if (!allowed) {
			return;
		}
		setSitesLoading(true);
		fetchSites()
			.then(setSites)
			.catch((err: unknown) => setSitesError(err instanceof ApiError ? err.message : "Could not load sites."))
			.finally(() => setSitesLoading(false));
	}, [allowed]);

	// -- step 2: scope ---------------------------------------------------
	const [selections, setSelections] = useState<TargetSelection[]>([]);
	const [scopeLoading, setScopeLoading] = useState(false);
	const [scopeError, setScopeError] = useState<string | null>(null);

	const loadScope = useCallback((site: string) => {
		setScopeLoading(true);
		setScopeError(null);
		fetchTargets(site)
			.then(async (targets) => {
				const next: TargetSelection[] = targets.map((target) => ({
					target,
					inventory: null,
					loadingInventory: true,
					selectedItemIds: new Set<string>(),
					targetSelected: true,
				}));
				setSelections(next);
				await Promise.all(
					targets.map(async (target, index) => {
						try {
							const inv = await fetchTargetInventory(target.id);
							const flat = flattenInventory(inv.items);
							setSelections((prev) => {
								const copy = [...prev];
								if (copy[index]?.target.id === target.id) {
									copy[index] = {
										...copy[index],
										inventory: inv.items,
										loadingInventory: false,
										selectedItemIds: new Set(flat.map((item) => item.id)),
									};
								}
								return copy;
							});
						} catch {
							// Inventory fetch failure for one target falls back to the
							// target-level checkbox, same as an empty inventory — a single
							// target's discovery gap must not block scoping the rest.
							setSelections((prev) => {
								const copy = [...prev];
								if (copy[index]?.target.id === target.id) {
									copy[index] = { ...copy[index], inventory: [], loadingInventory: false };
								}
								return copy;
							});
						}
					}),
				);
			})
			.catch((err: unknown) => setScopeError(err instanceof ApiError ? err.message : "Could not load targets."))
			.finally(() => setScopeLoading(false));
	}, []);

	const selectSite = useCallback(
		(id: string) => {
			setSiteId(id);
			setSelections([]);
			if (id) {
				loadScope(id);
			}
		},
		[loadScope],
	);

	const toggleTarget = useCallback((targetId: string, on: boolean) => {
		setSelections((prev) =>
			prev.map((sel) =>
				sel.target.id === targetId
					? { ...sel, targetSelected: on, selectedItemIds: on ? new Set(flattenInventory(sel.inventory ?? []).map((i) => i.id)) : new Set() }
					: sel,
			),
		);
	}, []);

	const toggleInventoryItem = useCallback((targetId: string, itemId: string, on: boolean) => {
		setSelections((prev) =>
			prev.map((sel) => {
				if (sel.target.id !== targetId) {
					return sel;
				}
				const next = new Set(sel.selectedItemIds);
				if (on) {
					next.add(itemId);
				} else {
					next.delete(itemId);
				}
				const total = flattenInventory(sel.inventory ?? []).length;
				return { ...sel, selectedItemIds: next, targetSelected: next.size > 0 || total === 0 };
			}),
		);
	}, []);

	const selectedTargetIds = useMemo(
		() => selections.filter((s) => s.targetSelected && (s.inventory === null || s.inventory.length === 0 || s.selectedItemIds.size > 0)).map((s) => s.target.id),
		[selections],
	);

	// -- step 3: credential ----------------------------------------------
	const [credentialMode, setCredentialMode] = useState<CredentialMode>("service");
	const [credentialOptions, setCredentialOptions] = useState<CredentialOption[]>([]);
	const [credentialOptionsError, setCredentialOptionsError] = useState<string | null>(null);
	const [serviceCredentialId, setServiceCredentialId] = useState("");
	const [personalUsername, setPersonalUsername] = useState("");
	const [personalSecret, setPersonalSecret] = useState("");

	const canUsePersonal = user ? roleAtLeast(user.role, "Operator") : false;
	const personalGate = user
		? roleGateProps(user.role, "Operator", "Requires Operator or higher — ad hoc personal credentials are not available to Cyber")
		: { disabled: true };

	useEffect(() => {
		if (!allowed || step !== "credential") {
			return;
		}
		fetchCredentialOptions()
			.then(setCredentialOptions)
			.catch((err: unknown) => setCredentialOptionsError(err instanceof ApiError ? err.message : "Could not load credentials."));
	}, [allowed, step]);

	/** Clears the write-only personal secret from component state — same
	 * convention as CredentialsTab.tsx's form reset after submit, so nothing
	 * lingers in a closure after the request is done with it. */
	const clearPersonalSecret = useCallback(() => setPersonalSecret(""), []);

	// -- step 5: confirm ---------------------------------------------------
	const [submitting, setSubmitting] = useState(false);
	const [submitError, setSubmitError] = useState<string | null>(null);

	const canConfirm = siteId !== "" && (credentialMode === "service" ? serviceCredentialId !== "" : personalUsername !== "" && personalSecret !== "");

	const submit = useCallback(async () => {
		setSubmitting(true);
		setSubmitError(null);
		try {
			const scope = selectedTargetIds.length > 0 && selectedTargetIds.length < selections.length
				? { site_id: siteId, target_ids: selectedTargetIds }
				: { site_id: siteId };
			const result = await createScanRun({
				scope,
				credential_id: credentialMode === "service" ? serviceCredentialId : undefined,
				credential: credentialMode === "personal" ? { kind: "personal", username: personalUsername, secret: personalSecret } : undefined,
			});
			clearPersonalSecret();
			navigate(`/live-run?run=${result.run_id}`);
		} catch (err) {
			setSubmitError(err instanceof ApiError ? err.message : "Could not start the scan.");
		} finally {
			setSubmitting(false);
		}
	}, [selectedTargetIds, selections.length, siteId, credentialMode, serviceCredentialId, personalUsername, personalSecret, clearPersonalSecret, navigate]);

	if (!user) {
		return null;
	}

	const siteName = sites.find((s) => s.id === siteId)?.name ?? "";
	const selectedCredentialName =
		credentialMode === "service" ? (credentialOptions.find((c) => c.id === serviceCredentialId)?.name ?? "") : `${personalUsername} (personal, not stored)`;

	const stepIndex = STEPS.findIndex((s) => s.key === step);
	const canAdvance = (key: StepKey): boolean => {
		if (key === "scope" || key === "credential" || key === "schedule" || key === "confirm") {
			return siteId !== "";
		}
		return true;
	};

	return (
		<div className="start-scan-screen">
			<div className="start-scan-screen__stepper">
				{STEPS.map((s, index) => (
					<button
						key={s.key}
						type="button"
						className={`start-scan-screen__step${step === s.key ? " is-active" : ""}`}
						disabled={!allowed || (index > stepIndex && !canAdvance(s.key))}
						onClick={() => setStep(s.key)}
					>
						<span className="start-scan-screen__step-index">{index + 1}</span>
						<span>{s.label}</span>
					</button>
				))}
			</div>

			{!allowed && (
				<div className="start-scan-screen__gate">
					<p {...gate}>Starting a scan requires Cyber or higher.</p>
				</div>
			)}

			{allowed && (
				<div className="start-scan-screen__body">
					{step === "site" && (
						<SiteStep sites={sites} loading={sitesLoading} error={sitesError} siteId={siteId} onSelect={selectSite} />
					)}

					{step === "scope" && (
						<ScopeStep
							selections={selections}
							loading={scopeLoading}
							error={scopeError}
							onToggleTarget={toggleTarget}
							onToggleItem={toggleInventoryItem}
						/>
					)}

					{step === "credential" && (
						<CredentialStep
							mode={credentialMode}
							onModeChange={setCredentialMode}
							canUsePersonal={canUsePersonal}
							personalGate={personalGate}
							credentialOptions={credentialOptions}
							credentialOptionsError={credentialOptionsError}
							serviceCredentialId={serviceCredentialId}
							onServiceCredentialChange={setServiceCredentialId}
							personalUsername={personalUsername}
							onPersonalUsernameChange={setPersonalUsername}
							personalSecret={personalSecret}
							onPersonalSecretChange={setPersonalSecret}
						/>
					)}

					{step === "schedule" && <ScheduleStep />}

					{step === "confirm" && (
						<ConfirmStep
							siteName={siteName}
							targetCount={selectedTargetIds.length || selections.length}
							totalTargets={selections.length}
							credentialMode={credentialMode}
							credentialName={selectedCredentialName}
							canConfirm={canConfirm}
							submitting={submitting}
							error={submitError}
							onConfirm={submit}
						/>
					)}

					<div className="start-scan-screen__nav">
						<button type="button" disabled={stepIndex === 0} onClick={() => setStep(STEPS[stepIndex - 1].key)}>
							Back
						</button>
						{step !== "confirm" && (
							<button type="button" disabled={!canAdvance(STEPS[stepIndex + 1]?.key)} onClick={() => setStep(STEPS[stepIndex + 1].key)}>
								Next
							</button>
						)}
					</div>
				</div>
			)}
		</div>
	);
}

function SiteStep({
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

function ScopeStep({
	selections,
	loading,
	error,
	onToggleTarget,
	onToggleItem,
}: {
	selections: TargetSelection[];
	loading: boolean;
	error: string | null;
	onToggleTarget: (targetId: string, on: boolean) => void;
	onToggleItem: (targetId: string, itemId: string, on: boolean) => void;
}) {
	return (
		<div className="start-scan-screen__panel">
			<div className="start-scan-screen__panel-title">Scope — inventory</div>
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

function CredentialStep({
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

function ScheduleStep() {
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

function ConfirmStep({
	siteName,
	targetCount,
	totalTargets,
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
