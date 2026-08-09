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
import { useCallback, useEffect, useState } from "react";
import { useAuth } from "../../lib/auth-context";
import { ApiError } from "../../lib/api";
import { roleAtLeast, roleGateProps } from "../../lib/roles";
import {
	connectionHost,
	createTarget,
	deleteTarget,
	fetchTargets,
	formatDiscoveryStatus,
	formatTimestamp,
	TARGET_KINDS,
	updateTarget,
	type CredentialOption,
	type Target,
	type TargetKind,
	type TargetWriteInput,
} from "./sites";
import "./ConfigurationScreen.css";

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
}) {
	const badDiscovery = target.discovery_status === "failed";
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
					{formatDiscoveryStatus(target.discovery_status)}
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
					</td>
				</tr>
			)}
		</>
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
