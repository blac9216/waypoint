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
import { useAuth } from "../../lib/auth-context";
import { clearStepUpRetry, consumeStepUpRetry, stashStepUpRetry } from "../../lib/stepUpRetry";
import {
	createCredential,
	deleteCredential,
	describeCredentialInUse,
	fetchCredentials,
	updateCredential,
	type Credential,
	type CredentialType,
} from "./credentials";

/** `403 step_up_required` (issue #521/#534): a credential-secret-overwrite PUT whose token's `auth_time` is stale or missing. Distinct from every other `ApiError` this hook can surface — the caller should offer re-authentication, not just show the message. */
function isStepUpRequired(err: unknown): boolean {
	return err instanceof ApiError && err.status === 403 && err.code === "step_up_required";
}

/** `stepUpRetry.ts`'s `kind` discriminator for a credential-edit retry — scopes the stashed intent to this one call site so an unrelated pending retry is never misapplied here. */
const STEP_UP_RETRY_KIND = "credential-edit";

/**
 * The step-up retry stash NEVER carries the plaintext `secret` (issue #537
 * review Finding 1): it would survive the full-page OIDC redirect at rest in
 * `sessionStorage` for exactly the walked-away-session window step-up defends
 * against. We persist only the credential id and the non-secret fields; on
 * resume the edit form re-opens pre-filled with these and the admin re-enters
 * the secret (the form already supports "leave blank to keep current secret").
 */
type CredentialEditRetryFields = Omit<CredentialFormState, "secret">;

interface CredentialEditRetryPayload {
	id: string;
	form: CredentialEditRetryFields;
}

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
	const { stepUpOidcLogin } = useAuth();
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

	/**
	 * Resumes a credential edit that 403'd `step_up_required` before the
	 * page navigated away for re-authentication (issue #521/#534's "the
	 * original request is retried once re-authentication completes" AC).
	 * Runs once per mount: `consumeStepUpRetry` is read-once by design (see
	 * `lib/stepUpRetry.ts`), so a stashed intent can only ever resume the
	 * single edit that triggered it, never replay on an unrelated later
	 * mount. Re-opens the edit form with the original NON-SECRET field values
	 * and an empty secret field (the plaintext secret is never stashed — see
	 * `CredentialEditRetryPayload`), so the admin re-enters the secret rather
	 * than silently re-submitting blind — the freshness window has already been
	 * spent on the redirect round trip by the time this runs, and re-prompting
	 * for the secret is both lower-exposure and cheaper than a second surprise
	 * 403 if anything else about the request has gone stale meanwhile.
	 */
	useEffect(() => {
		const pending = consumeStepUpRetry<CredentialEditRetryPayload>(STEP_UP_RETRY_KIND);
		if (!pending) {
			return;
		}
		setEditForm({ ...pending.form, secret: "" });
		setEditingId(pending.id);
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, []);

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
				if (isStepUpRequired(err)) {
					// Stash the in-flight edit and redirect for fresh auth
					// (`prompt=login`) — `stepUpOidcLogin` navigates the whole page
					// away, so nothing after this call runs; the mount effect above
					// resumes the edit once the callback lands back here.
					// The plaintext `secret` is deliberately excluded from the stash
					// (issue #537 Finding 1): it would otherwise sit at rest in
					// sessionStorage across the redirect. The admin re-enters it on
					// resume.
					const { secret: _secret, ...formWithoutSecret } = editForm;
					stashStepUpRetry<CredentialEditRetryPayload>({
						kind: STEP_UP_RETRY_KIND,
						payload: { id, form: formWithoutSecret },
					});
					setSaving(false);
					try {
						await stepUpOidcLogin();
					} catch (redirectErr) {
						// The redirect never happened, so nothing will resume this
						// stash — drop it rather than leave it to replay on a later
						// mount the user never re-authenticated for.
						clearStepUpRetry();
						setFormError(redirectErr instanceof Error ? redirectErr.message : "Could not start re-authentication.");
					}
					return;
				}
				setFormError(err instanceof ApiError ? err.message : "Could not update the credential.");
			} finally {
				setSaving(false);
			}
		},
		[editForm, load, stepUpOidcLogin],
	);

	const doDelete = useCallback(
		async (id: string, name: string) => {
			// Issue #593: completed run/job history is no longer a reason deletion
			// is refused (the API detaches it), so the confirmation no longer warns
			// about erasing history -- only an actual 409 (live config/active work)
			// does, and with accurate, category-specific guidance (see the catch
			// block below).
			if (!window.confirm(`Delete credential "${name}"? This cannot be undone.`)) {
				return;
			}
			setFormError(null);
			try {
				await deleteCredential(id);
				load();
			} catch (err) {
				if (err instanceof ApiError && err.code === "credential_in_use") {
					setFormError(describeCredentialInUse(name, err.blockers));
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
