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
 * Issue #418 split this screen into: form/CRUD state (useCredentialForms.ts),
 * test SSE tracking (useCredentialTest.ts — see that file's doc comment for
 * the job-follow/stream-teardown contract), and presentation (CredentialRow.tsx
 * for the row + form, this file for the panel shell/table). No behavior
 * changed in the split — every flow (create/edit/delete/test, halt status,
 * write-only secret handling, validation, role guards, SSE completion) is
 * identical to the pre-split single-file version.
 */
import { useAuth } from "../../lib/auth-context";
import { roleAtLeast, roleGateProps } from "../../lib/roles";
import { CredentialForm, CredentialRow } from "./CredentialRow";
import { EMPTY_CREDENTIAL_FORM, toFormState, useCredentialForms } from "./useCredentialForms";
import { useCredentialTest } from "./useCredentialTest";
import "./ConfigurationScreen.css";
import "./CredentialsTab.css";

export function CredentialsTab() {
	const { user } = useAuth();
	const {
		credentials,
		setCredentials,
		loading,
		loadError,
		creating,
		setCreating,
		createForm,
		setCreateForm,
		editingId,
		setEditingId,
		editForm,
		setEditForm,
		saving,
		formError,
		setFormError,
		submitCreate,
		submitEdit,
		doDelete,
	} = useCredentialForms();

	const { testing, testMessage, doTest } = useCredentialTest(setCredentials);

	const canWrite = user ? roleAtLeast(user.role, "Admin") : false;
	const writeGate = user ? roleGateProps(user.role, "Admin", `Requires Admin — this action is not available to ${user.role}`) : { disabled: true };

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
							setCreateForm(EMPTY_CREDENTIAL_FORM);
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
										setEditForm(EMPTY_CREDENTIAL_FORM);
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
