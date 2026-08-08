/**
 * Config → Credentials tab (issue #247, epic #13) — docs/ui/prototype
 * README "Credentials" panel (name, owner, type, used-by count, rotation
 * date, status pill), against the #20 backend (`CredentialsController`,
 * PR #267 added `username`). ADR-0011: shared/service credentials only —
 * there is no personal-credential row, form field, or filter anywhere in
 * this tab.
 *
 * The secret field is the sensitive part of this screen: it is write-only
 * (credentials.ts's `CredentialWriteInput.secret`) and is cleared from
 * local component state immediately after a successful submit, alongside
 * the rest of the form, so no lingering closure holds it after the request
 * completes. It is never pre-filled from a GET — there is nothing to
 * pre-fill from, since `Credential` (the GET/list shape) has no secret
 * field to begin with (has_secret is a boolean).
 *
 * Test action (issue #323, reworking #247's stub for #245/PR #322's job-backed
 * `POST /credentials/{id}/test`): the request now returns `202 + {run_id,
 * job_id}` instead of an inline result, so `doTest` follows the job via the
 * same SSE plumbing the Live Run board uses (`lib/events.ts`'s
 * `connectEventStream`, not a fresh EventSource) rather than reading a
 * result that no longer exists on the response. It watches `job.state`
 * events scoped to that `job_id` until a terminal state (`done`/`failed`/
 * `auth-failed`), then re-fetches the credential — its `health` field is the
 * server-side source of truth, flipped by `CredentialTestJobHandler`, not by
 * this call — and reflects both the health pill and the terminal note on the
 * row.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "../../lib/auth";
import { API_BASE, ApiError } from "../../lib/api";
import { connectEventStream } from "../../lib/events";
import { roleAtLeast, roleGateProps } from "../../lib/roles";
import {
	createCredential,
	CREDENTIAL_TYPES,
	deleteCredential,
	fetchCredential,
	fetchCredentials,
	formatHealth,
	testCredential,
	formatTimestamp,
	updateCredential,
	type Credential,
	type CredentialType,
} from "./credentials";
import "./ConfigurationScreen.css";
import "./CredentialsTab.css";

/** `job.state` terminal values (api-contract.md's Simple-job state machine —
 * `credential-test` is a `JobShape.Simple` job: queued -> running ->
 * done|failed|auth-failed). */
const TERMINAL_JOB_STATES = new Set(["done", "failed", "auth-failed"]);

interface JobStateEventData {
	to?: string;
	note?: string;
}

interface CredentialFormState {
	name: string;
	credential_type: CredentialType;
	username: string;
	sudo_enabled: boolean;
	secret: string;
}

const EMPTY_FORM: CredentialFormState = {
	name: "",
	credential_type: "vcenter",
	username: "",
	sudo_enabled: false,
	secret: "",
};

function toFormState(credential: Credential): CredentialFormState {
	const type = CREDENTIAL_TYPES.some((t) => t.value === credential.credential_type)
		? (credential.credential_type as CredentialType)
		: "vcenter";
	return {
		name: credential.name,
		credential_type: type,
		username: credential.username ?? "",
		sudo_enabled: credential.sudo_enabled,
		// Deliberately never seeded from `credential` — there is no secret on
		// the wire to seed it with (see module doc comment).
		secret: "",
	};
}

export function CredentialsTab() {
	const { user, token } = useAuth();
	const [credentials, setCredentials] = useState<Credential[]>([]);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);

	const [creating, setCreating] = useState(false);
	const [createForm, setCreateForm] = useState<CredentialFormState>(EMPTY_FORM);
	const [editingId, setEditingId] = useState<string | null>(null);
	const [editForm, setEditForm] = useState<CredentialFormState>(EMPTY_FORM);
	const [saving, setSaving] = useState(false);
	const [formError, setFormError] = useState<string | null>(null);

	/** id -> in-flight test, so multiple rows can be tested independently and
	 * a stale spinner never lingers on the wrong row. */
	const [testing, setTesting] = useState<Set<string>>(new Set());
	const [testMessage, setTestMessage] = useState<{ id: string; succeeded: boolean; message: string } | null>(null);
	/** Token getter for `connectEventStream`, kept live via a ref so an
	 * in-flight test's stream always reads the current token rather than
	 * one captured by a stale closure. */
	const tokenRef = useRef(token);
	tokenRef.current = token;

	const canWrite = user ? roleAtLeast(user.role, "Admin") : false;
	const writeGate = user ? roleGateProps(user.role, "Admin", `Requires Admin — this action is not available to ${user.role}`) : { disabled: true };

	const load = useCallback(() => {
		setLoading(true);
		setLoadError(null);
		fetchCredentials()
			.then(setCredentials)
			.catch((err: unknown) => setLoadError(err instanceof ApiError ? err.message : "Could not load credentials."))
			.finally(() => setLoading(false));
	}, []);

	useEffect(() => {
		load();
	}, [load]);

	const submitCreate = useCallback(async () => {
		setSaving(true);
		setFormError(null);
		try {
			await createCredential({
				name: createForm.name,
				credential_type: createForm.credential_type,
				username: createForm.username,
				sudo_enabled: createForm.sudo_enabled,
				secret: createForm.secret,
			});
			setCreating(false);
			setCreateForm(EMPTY_FORM);
			load();
		} catch (err) {
			setFormError(err instanceof ApiError ? err.message : "Could not create the credential.");
		} finally {
			setSaving(false);
		}
	}, [createForm, load]);

	const submitEdit = useCallback(
		async (id: string) => {
			setSaving(true);
			setFormError(null);
			try {
				await updateCredential(id, {
					name: editForm.name,
					credential_type: editForm.credential_type,
					username: editForm.username,
					sudo_enabled: editForm.sudo_enabled,
					secret: editForm.secret,
				});
				setEditingId(null);
				setEditForm(EMPTY_FORM);
				load();
			} catch (err) {
				setFormError(err instanceof ApiError ? err.message : "Could not update the credential.");
			} finally {
				setSaving(false);
			}
		},
		[editForm, load],
	);

	const doDelete = useCallback(
		async (id: string, name: string) => {
			if (!window.confirm(`Delete credential "${name}"? This cannot be undone.`)) {
				return;
			}
			setFormError(null);
			try {
				await deleteCredential(id);
				load();
			} catch (err) {
				if (err instanceof ApiError && err.code === "credential_in_use") {
					setFormError(`"${name}" is still referenced by jobs or runs and cannot be deleted until that history is removed.`);
				} else {
					setFormError(err instanceof ApiError ? err.message : "Could not delete the credential.");
				}
			}
		},
		[load],
	);

	const doTest = useCallback(async (id: string) => {
		setTesting((prev) => new Set(prev).add(id));
		setTestMessage(null);

		const stopTesting = () => {
			setTesting((prev) => {
				const next = new Set(prev);
				next.delete(id);
				return next;
			});
		};

		let queued: { run_id: string; job_id: string };
		try {
			queued = await testCredential(id);
		} catch (err) {
			setTestMessage({ id, succeeded: false, message: err instanceof ApiError ? err.message : "Test failed." });
			stopTesting();
			return;
		}

		// Follow the queued job to its terminal state via the same SSE plumbing
		// the Live Run board uses (lib/events.ts's connectEventStream) rather
		// than reading a result inline — the 202 response carries no outcome
		// (issue #323). Once terminal, `credentials.health` is re-fetched
		// because the job handler, not this response, is the source of truth
		// for the pill.
		let settled = false;
		const close = connectEventStream(`${API_BASE}/runs/${queued.run_id}/events`, {
			getToken: () => tokenRef.current,
			onEvent: (event) => {
				if (settled || event.type !== "job.state" || event.job_id !== queued.job_id) {
					return;
				}
				const data = event.data as JobStateEventData;
				if (!data.to || !TERMINAL_JOB_STATES.has(data.to)) {
					return;
				}
				settled = true;
				const succeeded = data.to === "done";
				const message = data.note ?? (succeeded ? "Test succeeded." : "Test failed.");
				setTestMessage({ id, succeeded, message });
				fetchCredential(id)
					.then((refreshed) => setCredentials((prev) => prev.map((c) => (c.id === id ? refreshed : c))))
					.catch(() => {
						// The row keeps its last-known health; the terminal message
						// above already told the operator the outcome.
					})
					.finally(() => {
						stopTesting();
						close();
					});
			},
		});
	}, []);

	return (
		<div className="config-tab config-tab--credentials">
			<div className="config-panel">
				<div className="config-panel__header">
					<div className="config-panel__title">CREDENTIALS</div>
					<div className="config-panel__spacer" />
					<button
						type="button"
						{...writeGate}
						onClick={() => {
							setCreating((v) => !v);
							setEditingId(null);
							setFormError(null);
						}}
					>
						{creating ? "Cancel" : "Add credential"}
					</button>
				</div>

				{loadError && <div className="config-panel__error">{loadError}</div>}
				{formError && <div className="config-panel__error">{formError}</div>}

				{creating && canWrite && (
					<CredentialForm
						title="New credential"
						form={createForm}
						setForm={setCreateForm}
						saving={saving}
						onCancel={() => {
							setCreating(false);
							setCreateForm(EMPTY_FORM);
						}}
						onSubmit={submitCreate}
					/>
				)}

				<table className="config-table">
					<colgroup>
						<col style={{ width: "20%" }} />
						<col style={{ width: "12%" }} />
						<col style={{ width: "12%" }} />
						<col style={{ width: "12%" }} />
						<col style={{ width: "14%" }} />
						<col style={{ width: "15%" }} />
						<col style={{ width: "15%" }} />
					</colgroup>
					<thead>
						<tr>
							<th>NAME</th>
							<th>TYPE</th>
							<th>OWNER</th>
							<th>STATUS</th>
							<th>USED BY</th>
							<th>LAST ROTATED</th>
							<th>ACTIONS</th>
						</tr>
					</thead>
					<tbody>
						{loading && (
							<tr>
								<td colSpan={7} className="config-table__empty">
									Loading credentials…
								</td>
							</tr>
						)}
						{!loading && credentials.length === 0 && (
							<tr>
								<td colSpan={7} className="config-table__empty">
									No credentials yet.
								</td>
							</tr>
						)}
						{!loading &&
							credentials.map((credential) => (
								<CredentialRow
									key={credential.id}
									credential={credential}
									canWrite={canWrite}
									writeGate={writeGate}
									isEditing={editingId === credential.id}
									isTesting={testing.has(credential.id)}
									testMessage={testMessage && testMessage.id === credential.id ? testMessage : null}
									onEdit={() => {
										setEditForm(toFormState(credential));
										setEditingId(credential.id);
										setCreating(false);
										setFormError(null);
									}}
									onCancelEdit={() => {
										setEditingId(null);
										setEditForm(EMPTY_FORM);
									}}
									onDelete={() => doDelete(credential.id, credential.name)}
									onTest={() => doTest(credential.id)}
									editForm={editForm}
									setEditForm={setEditForm}
									saving={saving}
									onSubmitEdit={() => submitEdit(credential.id)}
								/>
							))}
					</tbody>
				</table>
			</div>
		</div>
	);
}

function CredentialRow({
	credential,
	canWrite,
	writeGate,
	isEditing,
	isTesting,
	testMessage,
	onEdit,
	onCancelEdit,
	onDelete,
	onTest,
	editForm,
	setEditForm,
	saving,
	onSubmitEdit,
}: {
	credential: Credential;
	canWrite: boolean;
	writeGate: { disabled: boolean; style?: { opacity: number }; title?: string };
	isEditing: boolean;
	isTesting: boolean;
	testMessage: { succeeded: boolean; message: string } | null;
	onEdit: () => void;
	onCancelEdit: () => void;
	onDelete: () => void;
	onTest: () => void;
	editForm: CredentialFormState;
	setEditForm: (f: CredentialFormState) => void;
	saving: boolean;
	onSubmitEdit: () => void;
}) {
	const healthClass =
		credential.health === "valid"
			? "credentials-tab__health--valid"
			: credential.health === "auth_failing"
				? "credentials-tab__health--bad"
				: "credentials-tab__health--unknown";

	return (
		<>
			<tr className="config-table__row">
				<td className="config-table__truncate mono" title={credential.name}>
					{credential.name}
				</td>
				<td>
					<span className="config-table__kind">{credential.credential_type}</span>
				</td>
				<td className="mono">shared</td>
				<td>
					<span className={`credentials-tab__health ${healthClass}`}>{formatHealth(credential.health)}</span>
				</td>
				<td className="mono">{credential.used_by_job_count}</td>
				<td className="config-table__truncate mono">{formatTimestamp(credential.rotated_at)}</td>
				<td>
					<div className="config-table__row-actions">
						<button type="button" onClick={onTest} disabled={isTesting}>
							{isTesting ? "Testing…" : "Test"}
						</button>
						<button type="button" {...writeGate} onClick={onEdit}>
							{isEditing ? "Close" : "Edit"}
						</button>
						<button type="button" {...writeGate} onClick={onDelete}>
							Delete
						</button>
					</div>
					{testMessage && (
						<div className={testMessage.succeeded ? "credentials-tab__test-ok" : "credentials-tab__test-bad"}>
							{testMessage.message}
						</div>
					)}
				</td>
			</tr>
			{isEditing && canWrite && (
				<tr>
					<td colSpan={7}>
						<CredentialForm
							title={`Edit ${credential.name}`}
							form={editForm}
							setForm={setEditForm}
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

function CredentialForm({
	title,
	form,
	setForm,
	saving,
	onCancel,
	onSubmit,
}: {
	title: string;
	form: CredentialFormState;
	setForm: (f: CredentialFormState) => void;
	saving: boolean;
	onCancel: () => void;
	onSubmit: () => void;
}) {
	const canSubmit = form.name.trim().length > 0;
	const isSsh = form.credential_type === "ssh";

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
						placeholder="e.g. Alpha vCenter service account"
						required
					/>
				</label>
				<label className="config-form__field">
					<span>Type</span>
					<select
						value={form.credential_type}
						onChange={(e) => {
							const credential_type = e.target.value as CredentialType;
							setForm({ ...form, credential_type, sudo_enabled: credential_type === "ssh" ? form.sudo_enabled : false });
						}}
					>
						{CREDENTIAL_TYPES.map((t) => (
							<option key={t.value} value={t.value}>
								{t.label}
							</option>
						))}
					</select>
				</label>
				<label className="config-form__field">
					<span>Username</span>
					<input
						value={form.username}
						onChange={(e) => setForm({ ...form, username: e.target.value })}
						placeholder="e.g. administrator@example.internal"
						autoComplete="off"
					/>
				</label>
				<label className="config-form__field">
					<span>Secret</span>
					<input
						type="password"
						value={form.secret}
						onChange={(e) => setForm({ ...form, secret: e.target.value })}
						placeholder={title.startsWith("Edit") ? "leave blank to keep current secret" : "required to enable Test"}
						autoComplete="new-password"
					/>
				</label>
				{isSsh && (
					<label className="config-form__field config-form__field--checkbox">
						<span>
							<input
								type="checkbox"
								checked={form.sudo_enabled}
								onChange={(e) => setForm({ ...form, sudo_enabled: e.target.checked })}
							/>{" "}
							Sudo enabled
						</span>
					</label>
				)}
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
