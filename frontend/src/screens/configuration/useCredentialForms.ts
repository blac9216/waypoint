/**
 * Credentials CRUD + form state (issue #418 extraction from CredentialsTab.tsx
 * — see that file's module doc for the write-only secret contract this hook
 * preserves unchanged). Owns the credential list, create/edit form state, and
 * the create/edit/delete mutations; SSE-driven test tracking is a separate
 * concern (useCredentialTest.ts) so this hook has nothing to do with job
 * streams.
 */
import { useCallback, useEffect, useState, type Dispatch, type SetStateAction } from "react";
import { ApiError } from "../../lib/api";
import {
	createCredential,
	deleteCredential,
	fetchCredentials,
	updateCredential,
	type Credential,
	type CredentialType,
} from "./credentials";

export interface CredentialFormState {
	name: string;
	credential_type: CredentialType;
	username: string;
	sudo_enabled: boolean;
	secret: string;
}

export const EMPTY_CREDENTIAL_FORM: CredentialFormState = {
	name: "",
	credential_type: "vcenter",
	username: "",
	sudo_enabled: false,
	secret: "",
};

/** Issue #385: preserve the credential's REAL type rather than falling back
 * to "vcenter" when it's outside the creatable CREDENTIAL_TYPES subset
 * (currently only depot-token, per #383/#384). credential_type is typed as
 * `CredentialType | string` only to tolerate an unrecognized wire value;
 * every value this app itself writes is a real CredentialType, so casting
 * here is safe and — critically — never silently swaps the type to a
 * different, wrong one. CredentialForm renders the type read-only whenever
 * it's not in CREDENTIAL_TYPES, and updateCredential never sends
 * credential_type on PUT, so this value is display-only and can't corrupt
 * the stored type either way. */
export function toFormState(credential: Credential): CredentialFormState {
	return {
		name: credential.name,
		credential_type: credential.credential_type as CredentialType,
		username: credential.username ?? "",
		sudo_enabled: credential.sudo_enabled,
		// Deliberately never seeded from `credential` — there is no secret on
		// the wire to seed it with (see CredentialsTab.tsx's module doc).
		secret: "",
	};
}

export interface UseCredentialFormsResult {
	credentials: Credential[];
	setCredentials: Dispatch<SetStateAction<Credential[]>>;
	loading: boolean;
	loadError: string | null;

	creating: boolean;
	setCreating: Dispatch<SetStateAction<boolean>>;
	createForm: CredentialFormState;
	setCreateForm: Dispatch<SetStateAction<CredentialFormState>>;

	editingId: string | null;
	setEditingId: Dispatch<SetStateAction<string | null>>;
	editForm: CredentialFormState;
	setEditForm: Dispatch<SetStateAction<CredentialFormState>>;

	saving: boolean;
	formError: string | null;
	setFormError: Dispatch<SetStateAction<string | null>>;

	submitCreate: () => Promise<void>;
	submitEdit: (id: string) => Promise<void>;
	doDelete: (id: string, name: string) => Promise<void>;
}

export function useCredentialForms(): UseCredentialFormsResult {
	const [credentials, setCredentials] = useState<Credential[]>([]);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);

	const [creating, setCreating] = useState(false);
	const [createForm, setCreateForm] = useState<CredentialFormState>(EMPTY_CREDENTIAL_FORM);
	const [editingId, setEditingId] = useState<string | null>(null);
	const [editForm, setEditForm] = useState<CredentialFormState>(EMPTY_CREDENTIAL_FORM);
	const [saving, setSaving] = useState(false);
	const [formError, setFormError] = useState<string | null>(null);

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
			setCreateForm(EMPTY_CREDENTIAL_FORM);
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
				setEditForm(EMPTY_CREDENTIAL_FORM);
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

	return {
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
	};
}
