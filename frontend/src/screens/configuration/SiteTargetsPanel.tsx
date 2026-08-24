/**
 * One site's target table + the create/edit forms for its targets — the
 * "TARGETS" panel from docs/ui/prototype/README.md's Sites & Targets tab
 * (target 32% / kind 16% / credential 18% / discovery 17% / last refreshed
 * 17%, `table-layout:fixed`). Mutations are Admin-only; a lower role still
 * sees every button, just disabled with a reason (roles.ts `roleGateProps`
 * — domain-model.md's "visible but disabled" convention), never hidden.
 *
 * Issue #258, closing sub-issue of the #237 split (see SitesTargetsTab.tsx's
 * doc comment). This is the security-sensitive slice — the only place in the
 * UI that constructs the `connection`/`credential_ref` payload sent to
 * targets — kept as its own focused PR per the review that requested the
 * split.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "../../lib/auth-context";
import { ApiError } from "../../lib/api";
import { roleAtLeast, roleGateProps } from "../../lib/roles";
import {
	applicablePurposes,
	CREDENTIAL_PURPOSE_SATISFYING_TYPES,
	purposeLabel,
	requiredPurposes,
	type CredentialPurpose,
} from "./credential-purposes";
import {
	clearTargetCredentialBinding,
	connectionHost,
	createTarget,
	deleteTarget,
	fetchDiscoveryRun,
	fetchTargets,
	formatDiscoveryStatus,
	formatTimestamp,
	isInventoryCapable,
	isTerminalRunState,
	queueDiscover,
	setTargetCredentialBinding,
	TARGET_KINDS,
	updateTarget,
	type CredentialOption,
	type Target,
	type TargetKind,
	type TargetWriteInput,
} from "./sites";
import "./ConfigurationScreen.css";

/**
 * Per-target "Refresh Inventory" state (issue #557) — local, not persisted:
 * the target's own `discovery_status` has no `queued` value (the issue's
 * Risks/Considerations note), and a discover job can fail before it ever
 * updates `discovery_status` (#556), so this local run/job tracking is the
 * more reliable feedback source while a discovery is in flight or has just
 * finished. Cleared once the row's next `load()` picks up a fresh
 * `discovery_status` that already reflects the outcome — but kept for a
 * failure so the error/retry affordance survives a background refetch.
 */
interface DiscoveryUiState {
	runId: string;
	jobId: string;
	/** `null` while polling; set once the run reaches a terminal state. */
	outcome: "failed" | null;
}

interface TargetFormState {
	kind: TargetKind;
	name: string;
	host: string;
	credential_ref: string;
}

const EMPTY_FORM: TargetFormState = { kind: "vsphere", name: "", host: "", credential_ref: "" };

function toFormState(target: Target): TargetFormState {
	return {
		kind: (TARGET_KINDS.some((k) => k.value === target.kind) ? target.kind : "vsphere") as TargetKind,
		name: target.name,
		host: connectionHost(target.connection),
		credential_ref: target.credential_ref ?? "",
	};
}

export function SiteTargetsPanel({
	siteId,
	siteName,
	credentials,
	onTargetsChanged,
}: {
	siteId: string;
	siteName: string;
	credentials: CredentialOption[];
	/** Called after a create/update/delete succeeds so the parent can keep the
	 * sidebar's per-site target count in sync with this table. */
	onTargetsChanged?: () => void;
}) {
	const { user } = useAuth();
	const [targets, setTargets] = useState<Target[]>([]);
	const [loading, setLoading] = useState(true);
	const [error, setError] = useState<string | null>(null);

	const [creating, setCreating] = useState(false);
	const [createForm, setCreateForm] = useState<TargetFormState>(EMPTY_FORM);
	const [editingId, setEditingId] = useState<string | null>(null);
	const [editForm, setEditForm] = useState<TargetFormState>(EMPTY_FORM);
	const [saving, setSaving] = useState(false);
	const [formError, setFormError] = useState<string | null>(null);

	const canWrite = user ? roleAtLeast(user.role, "Admin") : false;
	const writeGate = user ? roleGateProps(user.role, "Admin", `Requires Admin — this action is not available to ${user.role}`) : { disabled: true };

	const load = useCallback(() => {
		setLoading(true);
		setError(null);
		fetchTargets(siteId)
			.then(setTargets)
			.catch((err: unknown) => setError(err instanceof ApiError ? err.message : "Could not load targets."))
			.finally(() => setLoading(false));
	}, [siteId]);

	useEffect(() => {
		load();
	}, [load]);

	// Refresh Inventory (issue #557), keyed by target id. See DiscoveryUiState's
	// doc comment for why this rides alongside `targets` rather than being
	// derived from `discovery_status` alone.
	const [discovery, setDiscovery] = useState<Record<string, DiscoveryUiState>>({});
	// Poll timer handles per target id, so unmount/terminal cleanup can clear
	// exactly the timer(s) in flight without clobbering other targets' polls.
	const pollTimers = useRef<Record<string, ReturnType<typeof setTimeout>>>({});
	const pollGeneration = useRef<Record<string, number>>({});

	useEffect(() => {
		const timers = pollTimers.current;
		return () => {
			Object.values(timers).forEach(clearTimeout);
		};
	}, []);

	const stopPolling = useCallback((targetId: string) => {
		const timer = pollTimers.current[targetId];
		if (timer) {
			clearTimeout(timer);
			delete pollTimers.current[targetId];
		}
	}, []);

	const pollDiscoveryRun = useCallback(
		(targetId: string, runId: string, jobId: string, generation: number) => {
			const POLL_INTERVAL_MS = 3000;
			const tick = async () => {
				// A newer discover click (new generation) or unmount superseded this
				// poll loop — stop rather than racing state updates onto a stale run.
				if (pollGeneration.current[targetId] !== generation) {
					return;
				}
				try {
					const run = await fetchDiscoveryRun(runId);
					if (pollGeneration.current[targetId] !== generation) {
						return;
					}
					if (isTerminalRunState(run.state)) {
						const failed = run.state !== "completed" || run.job_count_failed > 0 || run.job_count_blocked > 0;
						setDiscovery((prev) => ({ ...prev, [targetId]: { runId, jobId, outcome: failed ? "failed" : null } }));
						if (!failed) {
							// Success: drop local state once a fresh load() reflects the
							// real discovery_status/last_refreshed from the server.
							load();
							setDiscovery((prev) => {
								const next = { ...prev };
								delete next[targetId];
								return next;
							});
						}
						stopPolling(targetId);
						return;
					}
					pollTimers.current[targetId] = setTimeout(() => void tick(), POLL_INTERVAL_MS);
				} catch {
					// A transient poll failure must not abandon the queued/running
					// affordance — keep polling until the run itself resolves.
					pollTimers.current[targetId] = setTimeout(() => void tick(), POLL_INTERVAL_MS);
				}
			};
			void tick();
		},
		[load, stopPolling],
	);

	const startDiscover = useCallback(
		async (targetId: string) => {
			// Duplicate-click prevention: bail if this target already has a
			// non-terminal (no `outcome`) discovery in flight.
			if (discovery[targetId] && discovery[targetId].outcome === null) {
				return;
			}
			setFormError(null);
			const generation = (pollGeneration.current[targetId] ?? 0) + 1;
			pollGeneration.current[targetId] = generation;
			try {
				const queued = await queueDiscover(targetId);
				setDiscovery((prev) => ({ ...prev, [targetId]: { runId: queued.run_id, jobId: queued.job_id, outcome: null } }));
				pollDiscoveryRun(targetId, queued.run_id, queued.job_id, generation);
			} catch (err) {
				setFormError(err instanceof ApiError ? err.message : "Could not queue inventory discovery.");
			}
		},
		[discovery, pollDiscoveryRun],
	);

	const credentialName = (id: string | null) => {
		if (!id) return "—";
		return credentials.find((c) => c.id === id)?.name ?? id;
	};

	const submitCreate = useCallback(
		async (input: TargetWriteInput) => {
			setSaving(true);
			setFormError(null);
			try {
				await createTarget(siteId, input);
				setCreating(false);
				setCreateForm(EMPTY_FORM);
				load();
				onTargetsChanged?.();
			} catch (err) {
				setFormError(err instanceof ApiError ? err.message : "Could not create the target.");
			} finally {
				setSaving(false);
			}
		},
		[siteId, load, onTargetsChanged],
	);

	const submitEdit = useCallback(
		async (id: string, input: TargetWriteInput) => {
			setSaving(true);
			setFormError(null);
			try {
				await updateTarget(id, input);
				setEditingId(null);
				load();
			} catch (err) {
				setFormError(err instanceof ApiError ? err.message : "Could not update the target.");
			} finally {
				setSaving(false);
			}
		},
		[load],
	);

	const doDelete = useCallback(
		async (id: string, name: string) => {
			if (!window.confirm(`Delete target "${name}"? This cannot be undone.`)) {
				return;
			}
			setFormError(null);
			try {
				await deleteTarget(id);
				load();
				onTargetsChanged?.();
			} catch (err) {
				setFormError(err instanceof ApiError ? err.message : "Could not delete the target.");
			}
		},
		[load, onTargetsChanged],
	);

	// Issue #584: purpose-specific credential binding management, per target
	// row. Bindings are saved immediately (no separate form "Save" step) since
	// each purpose is independently overridable (ADR-0021 §4) — matches how
	// "Refresh Inventory" above is also an immediate action, not a form field.
	const [bindingError, setBindingError] = useState<string | null>(null);
	const [savingBinding, setSavingBinding] = useState<string | null>(null);

	const doSetBinding = useCallback(
		async (targetId: string, purpose: CredentialPurpose, credentialId: string) => {
			setBindingError(null);
			setSavingBinding(`${targetId}:${purpose}`);
			try {
				await setTargetCredentialBinding(targetId, purpose, credentialId);
				load();
			} catch (err) {
				setBindingError(err instanceof ApiError ? err.message : "Could not save the credential binding.");
			} finally {
				setSavingBinding(null);
			}
		},
		[load],
	);

	const doClearBinding = useCallback(
		async (targetId: string, purpose: CredentialPurpose) => {
			setBindingError(null);
			setSavingBinding(`${targetId}:${purpose}`);
			try {
				await clearTargetCredentialBinding(targetId, purpose);
				load();
			} catch (err) {
				setBindingError(err instanceof ApiError ? err.message : "Could not clear the credential binding.");
			} finally {
				setSavingBinding(null);
			}
		},
		[load],
	);

	return (
		<div className="config-panel">
			<div className="config-panel__header">
				<div className="config-panel__title">{siteName.toUpperCase()} · TARGETS</div>
				<div className="config-panel__spacer" />
				<button
					type="button"
					{...writeGate}
					onClick={() => {
						setCreating((v) => !v);
						setFormError(null);
					}}
				>
					{creating ? "Cancel" : "Add target"}
				</button>
			</div>

			{error && <div className="config-panel__error">{error}</div>}
			{formError && <div className="config-panel__error">{formError}</div>}
			{bindingError && <div className="config-panel__error">{bindingError}</div>}

			{creating && canWrite && (
				<TargetForm
					title="New target"
					form={createForm}
					setForm={setCreateForm}
					credentials={credentials}
					saving={saving}
					onCancel={() => setCreating(false)}
					onSubmit={() =>
						submitCreate({
							kind: createForm.kind,
							name: createForm.name,
							host: createForm.host,
							credential_ref: createForm.credential_ref || null,
						})
					}
				/>
			)}

			<table className="config-table">
				<colgroup>
					<col style={{ width: "32%" }} />
					<col style={{ width: "16%" }} />
					<col style={{ width: "18%" }} />
					<col style={{ width: "17%" }} />
					<col style={{ width: "17%" }} />
				</colgroup>
				<thead>
					<tr>
						<th>TARGET</th>
						<th>KIND</th>
						<th>CREDENTIAL</th>
						<th>DISCOVERY</th>
						<th>LAST REFRESHED</th>
					</tr>
				</thead>
				<tbody>
					{loading && (
						<tr>
							<td colSpan={5} className="config-table__empty">
								Loading targets…
							</td>
						</tr>
					)}
					{!loading && targets.length === 0 && (
						<tr>
							<td colSpan={5} className="config-table__empty">
								No targets under this site yet.
							</td>
						</tr>
					)}
					{!loading &&
						targets.map((target) => (
							<TargetRow
								key={target.id}
								target={target}
								credentialName={credentialName(target.credential_ref)}
								canWrite={canWrite}
								writeGate={writeGate}
								isEditing={editingId === target.id}
								onEdit={() => {
									setEditForm(toFormState(target));
									setEditingId(target.id);
									setFormError(null);
								}}
								onCancelEdit={() => setEditingId(null)}
								onDelete={() => doDelete(target.id, target.name)}
								editForm={editForm}
								setEditForm={setEditForm}
								credentials={credentials}
								saving={saving}
								onSubmitEdit={() =>
									submitEdit(target.id, {
										kind: editForm.kind,
										name: editForm.name,
										host: editForm.host,
										credential_ref: editForm.credential_ref || null,
									})
								}
							discoveryState={discovery[target.id] ?? null}
							onRefreshInventory={() => void startDiscover(target.id)}
							onSetBinding={(purpose, credentialId) => void doSetBinding(target.id, purpose, credentialId)}
							onClearBinding={(purpose) => void doClearBinding(target.id, purpose)}
							savingBindingKey={savingBinding}
						/>
					))}
				</tbody>
			</table>
		</div>
	);
}

function TargetRow({
	target,
	credentialName,
	canWrite,
	writeGate,
	isEditing,
	onEdit,
	onCancelEdit,
	onDelete,
	editForm,
	setEditForm,
	credentials,
	saving,
	onSubmitEdit,
	discoveryState,
	onRefreshInventory,
	onSetBinding,
	onClearBinding,
	savingBindingKey,
}: {
	target: Target;
	credentialName: string;
	canWrite: boolean;
	writeGate: { disabled: boolean; style?: { opacity: number }; title?: string };
	isEditing: boolean;
	onEdit: () => void;
	onCancelEdit: () => void;
	onDelete: () => void;
	editForm: TargetFormState;
	setEditForm: (f: TargetFormState) => void;
	credentials: CredentialOption[];
	saving: boolean;
	onSubmitEdit: () => void;
	discoveryState: DiscoveryUiState | null;
	onRefreshInventory: () => void;
	onSetBinding: (purpose: CredentialPurpose, credentialId: string) => void;
	onClearBinding: (purpose: CredentialPurpose) => void;
	savingBindingKey: string | null;
}) {
	const badDiscovery = target.discovery_status === "failed" || discoveryState?.outcome === "failed";
	const inFlight = discoveryState !== null && discoveryState.outcome === null;
	const capable = isInventoryCapable(target.kind);

	// aria-live status text — never color-only (issue #557 AC): the local
	// in-flight/failed state takes precedence over the server's last-known
	// `discovery_status` since it is the more current/reliable source while a
	// discover run is active or has just failed (see DiscoveryUiState doc).
	const statusText = inFlight ? "Refreshing inventory…" : formatDiscoveryStatus(target.discovery_status);

	return (
		<>
			<tr className="config-table__row">
				<td className="config-table__truncate mono" title={target.name}>
					{target.name}
				</td>
				<td>
					<span className="config-table__kind">{target.kind}</span>
				</td>
				<td className="config-table__truncate mono" title={credentialName}>
					{credentialName}
				</td>
				<td className={badDiscovery ? "config-table__discovery--bad" : "config-table__discovery"}>
					<span aria-live="polite">{statusText}</span>
					{discoveryState?.outcome === "failed" && (
						<div className="config-table__discovery-detail">
							Discovery failed.{" "}
							<a href={`/live-jobs?run=${discoveryState.runId}`} className="config-table__discovery-link">
								View run details
							</a>
						</div>
					)}
					{capable && (
						<div className="config-table__row-actions">
							<button
								type="button"
								{...writeGate}
								disabled={writeGate.disabled || inFlight}
								title={
									writeGate.disabled
										? writeGate.title
										: inFlight
											? "Inventory discovery is already queued or running for this target"
											: undefined
								}
								onClick={onRefreshInventory}
							>
								{discoveryState?.outcome === "failed" ? "Retry" : inFlight ? "Refreshing…" : "Refresh Inventory"}
							</button>
						</div>
					)}
				</td>
				<td className="config-table__truncate mono">
					{formatTimestamp(target.last_refreshed)}
					<div className="config-table__row-actions">
						<button type="button" {...writeGate} onClick={onEdit}>
							{isEditing ? "Close" : "Edit"}
						</button>
						<button type="button" {...writeGate} onClick={onDelete}>
							Delete
						</button>
					</div>
				</td>
			</tr>
			{isEditing && canWrite && (
				<tr>
					<td colSpan={5}>
						<TargetForm
							title={`Edit ${target.name}`}
							form={editForm}
							setForm={setEditForm}
							credentials={credentials}
							saving={saving}
							onCancel={onCancelEdit}
							onSubmit={onSubmitEdit}
						/>
						<TargetCredentialBindingsPanel
							target={target}
							credentials={credentials}
							onSetBinding={onSetBinding}
							onClearBinding={onClearBinding}
							savingBindingKey={savingBindingKey}
						/>
					</td>
				</tr>
			)}
		</>
	);
}

/**
 * Issue #584 (ADR-0021): manage this target's purpose-specific credential
 * bindings — one row per purpose APPLICABLE to the target's kind (the shared
 * matrix, `applicablePurposes`), each with its own picker filtered to
 * compatible credential types (`CREDENTIAL_PURPOSE_SATISFYING_TYPES`) and its
 * own immediate save/clear. A required purpose with no bound credential is
 * flagged as "missing" (coverage display, issue #584 AC: "explain missing
 * required bindings") — never silently absent.
 */
function TargetCredentialBindingsPanel({
	target,
	credentials,
	onSetBinding,
	onClearBinding,
	savingBindingKey,
}: {
	target: Target;
	credentials: CredentialOption[];
	onSetBinding: (purpose: CredentialPurpose, credentialId: string) => void;
	onClearBinding: (purpose: CredentialPurpose) => void;
	savingBindingKey: string | null;
}) {
	const purposes = applicablePurposes(target.kind);
	if (purposes.length === 0) {
		return null;
	}

	const required = new Set(requiredPurposes(target.kind));
	const bindingsByPurpose = new Map(target.bindings.map((b) => [b.purpose, b]));

	return (
		<div className="config-form" aria-label={`Credential bindings for ${target.name}`}>
			<div className="config-form__title">Credential bindings</div>
			<table className="config-table">
				<thead>
					<tr>
						<th>PURPOSE</th>
						<th>CREDENTIAL</th>
						<th>COVERAGE</th>
					</tr>
				</thead>
				<tbody>
					{purposes.map((purpose) => {
						const binding = bindingsByPurpose.get(purpose);
						const compatibleTypes = CREDENTIAL_PURPOSE_SATISFYING_TYPES[purpose];
						const compatibleCredentials = credentials.filter((c) => compatibleTypes.includes(c.credential_type as never));
						const isSaving = savingBindingKey === `${target.id}:${purpose}`;
						const missing = required.has(purpose) && !binding;

						return (
							<tr key={purpose} className="config-table__row">
								<td>{purposeLabel(purpose)}</td>
								<td>
									<select
										aria-label={`${purposeLabel(purpose)} credential`}
										value={binding?.credential_ref ?? ""}
										disabled={isSaving}
										onChange={(e) => {
											if (e.target.value) {
												onSetBinding(purpose, e.target.value);
											} else {
												onClearBinding(purpose);
											}
										}}
									>
										<option value="">No credential</option>
										{compatibleCredentials.map((c) => (
											<option key={c.id} value={c.id}>
												{c.name}
											</option>
										))}
									</select>
								</td>
								<td>
									{missing ? (
										<span className="config-table__discovery--bad">Missing required binding</span>
									) : binding ? (
										"Bound"
									) : (
										"Optional — not bound"
									)}
								</td>
							</tr>
						);
					})}
				</tbody>
			</table>
		</div>
	);
}

function TargetForm({
	title,
	form,
	setForm,
	credentials,
	saving,
	onCancel,
	onSubmit,
}: {
	title: string;
	form: TargetFormState;
	setForm: (f: TargetFormState) => void;
	credentials: CredentialOption[];
	saving: boolean;
	onCancel: () => void;
	onSubmit: () => void;
}) {
	const canSubmit = form.name.trim().length > 0 && form.host.trim().length > 0;
	return (
		<form
			className="config-form"
			onSubmit={(e) => {
				e.preventDefault();
				if (canSubmit) onSubmit();
			}}
		>
			<div className="config-form__title">{title}</div>
			<div className="config-form__grid">
				<label className="config-form__field">
					<span>Name</span>
					<input
						value={form.name}
						onChange={(e) => setForm({ ...form, name: e.target.value })}
						placeholder="e.g. vcsa-01"
						required
					/>
				</label>
				<label className="config-form__field">
					<span>Kind</span>
					<select value={form.kind} onChange={(e) => setForm({ ...form, kind: e.target.value as TargetKind })}>
						{TARGET_KINDS.map((k) => (
							<option key={k.value} value={k.value}>
								{k.label}
							</option>
						))}
					</select>
				</label>
				<label className="config-form__field">
					<span>Host</span>
					<input
						value={form.host}
						onChange={(e) => setForm({ ...form, host: e.target.value })}
						placeholder="vcsa-01.example.internal"
						required
					/>
				</label>
				<label className="config-form__field">
					<span>Credential</span>
					<select value={form.credential_ref} onChange={(e) => setForm({ ...form, credential_ref: e.target.value })}>
						<option value="">No credential</option>
						{credentials.map((c) => (
							<option key={c.id} value={c.id}>
								{c.name}
							</option>
						))}
					</select>
				</label>
			</div>
			<div className="config-form__actions">
				<button type="button" onClick={onCancel} disabled={saving}>
					Cancel
				</button>
				<button type="submit" className="config-form__submit" disabled={saving || !canSubmit}>
					{saving ? "Saving…" : "Save"}
				</button>
			</div>
		</form>
	);
}
