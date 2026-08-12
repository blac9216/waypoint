/**
 * Credentials tab row + edit form presentation (issue #418 extraction from
 * CredentialsTab.tsx). Purely presentational — all state and mutations live
 * in useCredentialForms.ts / useCredentialTest.ts; this file only renders.
 */
import { formatHealth, formatTimestamp, CREDENTIAL_TYPES, type Credential, type CredentialType } from "./credentials";
import type { CredentialFormState } from "./useCredentialForms";
import type { CredentialTestMessage } from "./useCredentialTest";

export function CredentialRow({
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
	testMessage: CredentialTestMessage | null;
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
						<button
							type="button"
							onClick={onTest}
							disabled={writeGate.disabled || isTesting}
							style={!isTesting ? writeGate.style : undefined}
							title={!isTesting ? writeGate.title : undefined}
						>
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

export function CredentialForm({
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
	// Issue #385: a credential whose type isn't in the creatable dropdown
	// (currently only depot-token, #383/#384) has no option that can
	// represent it. Rather than force a dropdown selection onto some other
	// type (which submitEdit would never actually send, but which would
	// misdisplay the credential and invite confusion), show the real type as
	// a disabled, read-only control so the operator can still edit
	// name/username/secret without any implication the type is changeable
	// here.
	const isCreatableType = CREDENTIAL_TYPES.some((t) => t.value === form.credential_type);

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
					{isCreatableType ? (
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
					) : (
						<input value={form.credential_type} disabled readOnly title="Type is not editable for this credential" />
					)}
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
