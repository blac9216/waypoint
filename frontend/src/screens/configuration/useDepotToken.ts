/**
 * Depot-token credential lifecycle for the Config → Depot & Tokens tab (issue
 * #571, completing #560's frontend half). Deliberately reuses
 * credentials.ts's generic create/update/delete/test functions rather than a
 * parallel depot-specific REST surface — the backend has exactly one
 * `depot-token` credential type (`CredentialTypes.DepotToken`,
 * `CatalogOptions.DepotTokenCredentialType`), resolved by
 * `CredentialRepository.FindByTypeAsync` server-side, so this hook's job is
 * only to find that one row (if any) among `GET /credentials`'s full list and
 * present create-vs-replace accordingly.
 *
 * Step-up: a depot-token replace is a secret-overwrite PUT exactly like any
 * other credential edit, so it goes through the same
 * `stashStepUpRetry`/`consumeStepUpRetry` machinery as
 * `useCredentialForms.ts`, under its own retry `kind` so an unrelated pending
 * credential-edit retry is never misapplied to the depot token or vice versa.
 * The plaintext secret is never stashed, matching that hook's Finding-1
 * fix — the operator re-enters it after the re-auth redirect.
 *
 * Test: identical SSE-follow contract to `useCredentialTest.ts` (`202 +
 * {run_id, job_id}`, followed via `lib/events.ts` to a terminal `job.state`,
 * then a refetch for the authoritative `health`) — this hook wraps that same
 * hook rather than re-implementing the stream teardown/duplicate-guard
 * contract a second time.
 */
import { useCallback, useEffect, useMemo, useState } from "react";
import { ApiError } from "../../lib/api";
import { useAuth } from "../../lib/auth-context";
import { clearStepUpRetry, consumeStepUpRetry, stashStepUpRetry } from "../../lib/stepUpRetry";
import { createCredential, fetchCredentials, updateCredential, type Credential } from "./credentials";
import { useCredentialTest } from "./useCredentialTest";

const DEPOT_TOKEN_TYPE = "depot-token";
const STEP_UP_RETRY_KIND = "depot-token-replace";

export interface DepotTokenFormState {
	name: string;
	username: string;
	secret: string;
}

export const EMPTY_DEPOT_TOKEN_FORM: DepotTokenFormState = { name: "Broadcom Support Portal", username: "", secret: "" };

/** Never seeds `secret` from a GET — there is nothing to seed it with (no secret field exists on the wire). */
function toFormState(credential: Credential): DepotTokenFormState {
	return { name: credential.name, username: credential.username ?? "", secret: "" };
}

/** Non-secret fields only — mirrors useCredentialForms.ts's `CredentialEditRetryFields` (issue #537 Finding 1). */
type DepotTokenRetryPayload = Omit<DepotTokenFormState, "secret">;

export interface UseDepotTokenResult {
	credential: Credential | null;
	loading: boolean;
	loadError: string | null;
	reload: () => void;

	editing: boolean;
	setEditing: (v: boolean) => void;
	form: DepotTokenFormState;
	setForm: (f: DepotTokenFormState) => void;
	saving: boolean;
	formError: string | null;

	submit: () => Promise<void>;

	testing: boolean;
	testMessage: { succeeded: boolean; message: string } | null;
	doTest: () => Promise<void>;
}

export function useDepotToken(): UseDepotTokenResult {
	const { stepUpOidcLogin } = useAuth();
	const [credentials, setCredentials] = useState<Credential[]>([]);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);

	const [editing, setEditing] = useState(false);
	const [form, setForm] = useState<DepotTokenFormState>(EMPTY_DEPOT_TOKEN_FORM);
	const [saving, setSaving] = useState(false);
	const [formError, setFormError] = useState<string | null>(null);

	const credential = useMemo(() => credentials.find((c) => c.credential_type === DEPOT_TOKEN_TYPE) ?? null, [credentials]);

	const load = useCallback(() => {
		setLoading(true);
		setLoadError(null);
		fetchCredentials()
			.then(setCredentials)
			.catch((err: unknown) => setLoadError(err instanceof ApiError ? err.message : "Could not load the depot-token credential."))
			.finally(() => setLoading(false));
	}, []);

	useEffect(() => {
		load();
	}, [load]);

	// Resume a replace that 403'd step_up_required before the OIDC redirect —
	// mirrors useCredentialForms.ts's identical mount-effect resume.
	useEffect(() => {
		const pending = consumeStepUpRetry<DepotTokenRetryPayload>(STEP_UP_RETRY_KIND);
		if (!pending) {
			return;
		}
		setForm({ ...pending, secret: "" });
		setEditing(true);
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, []);

	const submit = useCallback(async () => {
		setSaving(true);
		setFormError(null);
		try {
			if (credential) {
				await updateCredential(credential.id, {
					name: form.name,
					credential_type: DEPOT_TOKEN_TYPE as never,
					username: form.username,
					sudo_enabled: false,
					secret: form.secret,
				});
			} else {
				await createCredential({
					name: form.name,
					credential_type: DEPOT_TOKEN_TYPE as never,
					username: form.username,
					sudo_enabled: false,
					secret: form.secret,
				});
			}
			setEditing(false);
			setForm(EMPTY_DEPOT_TOKEN_FORM);
			load();
		} catch (err) {
			if (err instanceof ApiError && err.status === 403 && err.code === "step_up_required") {
				const { secret: _secret, ...formWithoutSecret } = form;
				stashStepUpRetry<DepotTokenRetryPayload>({ kind: STEP_UP_RETRY_KIND, payload: formWithoutSecret });
				setSaving(false);
				try {
					await stepUpOidcLogin();
				} catch (redirectErr) {
					clearStepUpRetry();
					setFormError(redirectErr instanceof Error ? redirectErr.message : "Could not start re-authentication.");
				}
				return;
			}
			setFormError(err instanceof ApiError ? err.message : "Could not save the depot-token credential.");
		} finally {
			setSaving(false);
		}
	}, [credential, form, load, stepUpOidcLogin]);

	const { testing: testingIds, testMessage: rowTestMessage, doTest: doTestById } = useCredentialTest(setCredentials);

	const testing = credential ? testingIds.has(credential.id) : false;
	const testMessage = rowTestMessage && credential && rowTestMessage.id === credential.id ? rowTestMessage : null;

	const doTest = useCallback(async () => {
		if (!credential) {
			return;
		}
		await doTestById(credential.id);
	}, [credential, doTestById]);

	return {
		credential,
		loading,
		loadError,
		reload: load,
		editing,
		setEditing,
		form,
		setForm,
		saving,
		formError,
		submit,
		testing,
		testMessage: testMessage ? { succeeded: testMessage.succeeded, message: testMessage.message } : null,
		doTest,
	};
}

export { toFormState as toDepotTokenFormState };
