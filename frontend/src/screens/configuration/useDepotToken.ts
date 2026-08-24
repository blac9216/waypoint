/**
 * Depot credential lifecycle for the Config → Depot & Tokens tab (issue #571,
 * completing #560's frontend half; issue #690 splits the single depot-token
 * concept into two independent, non-interchangeable credentials). Deliberately
 * reuses credentials.ts's generic create/update/delete/test functions rather
 * than a parallel depot-specific REST surface — the backend has exactly one
 * well-known credential row per depot type (`CredentialTypes.DepotActivationCode`
 * / `CredentialTypes.LegacyDownloadToken`), resolved by
 * `CredentialRepository.FindByTypeAsync` server-side, so this hook's job is
 * only to find that one row (if any) among `GET /credentials`'s full list and
 * present create-vs-replace accordingly. `useDepotActivationCode` and
 * `useLegacyDownloadToken` are thin, independently-typed wrappers around one
 * shared factory (`useDepotCredential`) parameterized by credential_type — the
 * two credentials share this lifecycle shape exactly but must never be
 * conflated (issue #690 AC: cross-purpose values are never selected).
 *
 * Step-up: a replace is a secret-overwrite PUT exactly like any other
 * credential edit, so it goes through the same
 * `stashStepUpRetry`/`consumeStepUpRetry` machinery as
 * `useCredentialForms.ts`, under its own retry `kind` per credential type so
 * an unrelated pending credential-edit retry (or the other depot credential's
 * retry) is never misapplied. The plaintext secret is never stashed, matching
 * that hook's Finding-1 fix — the operator re-enters it after the re-auth
 * redirect.
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
import { createCredential, fetchCredentials, updateCredential, type Credential, type CredentialType } from "./credentials";
import { useCredentialTest } from "./useCredentialTest";

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

/**
 * Shared factory (issue #690): `credentialType` pins which of the two closed,
 * non-interchangeable depot credential rows this instance finds/creates/tests
 * — a caller can never accidentally cross the two, since each hook instance
 * is bound to exactly one `credential_type` for its whole lifetime.
 */
function useDepotCredential(credentialType: CredentialType, notFoundLabel: string): UseDepotTokenResult {
	const stepUpRetryKind = `${credentialType}-replace`;
	const { stepUpOidcLogin } = useAuth();
	const [credentials, setCredentials] = useState<Credential[]>([]);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);

	const [editing, setEditing] = useState(false);
	const [form, setForm] = useState<DepotTokenFormState>(EMPTY_DEPOT_TOKEN_FORM);
	const [saving, setSaving] = useState(false);
	const [formError, setFormError] = useState<string | null>(null);

	const credential = useMemo(() => credentials.find((c) => c.credential_type === credentialType) ?? null, [credentials, credentialType]);

	const load = useCallback(() => {
		setLoading(true);
		setLoadError(null);
		fetchCredentials()
			.then(setCredentials)
			.catch((err: unknown) => setLoadError(err instanceof ApiError ? err.message : `Could not load the ${notFoundLabel} credential.`))
			.finally(() => setLoading(false));
	}, [notFoundLabel]);

	useEffect(() => {
		load();
	}, [load]);

	// Resume a replace that 403'd step_up_required before the OIDC redirect —
	// mirrors useCredentialForms.ts's identical mount-effect resume.
	useEffect(() => {
		const pending = consumeStepUpRetry<DepotTokenRetryPayload>(stepUpRetryKind);
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
					credential_type: credentialType,
					username: form.username,
					sudo_enabled: false,
					secret: form.secret,
				});
			} else {
				await createCredential({
					name: form.name,
					credential_type: credentialType,
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
				stashStepUpRetry<DepotTokenRetryPayload>({ kind: stepUpRetryKind, payload: formWithoutSecret });
				setSaving(false);
				try {
					await stepUpOidcLogin();
				} catch (redirectErr) {
					clearStepUpRetry();
					setFormError(redirectErr instanceof Error ? redirectErr.message : "Could not start re-authentication.");
				}
				return;
			}
			setFormError(err instanceof ApiError ? err.message : `Could not save the ${notFoundLabel} credential.`);
		} finally {
			setSaving(false);
		}
	}, [credential, credentialType, form, load, notFoundLabel, stepUpOidcLogin, stepUpRetryKind]);

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

/** VCF 9.1 Software Depot Activation Code (issue #690): authenticates `vcf-download-tool` metadata/binary commands. */
export function useDepotActivationCode(): UseDepotTokenResult {
	return useDepotCredential("depot-activation-code", "depot Activation Code");
}

/** Legacy Broadcom Download Token (issue #690): substituted into `dl.broadcom.com` URL templates for UMDS/older flows. Cannot authenticate VCF 9.1 `vcf-download-tool` commands. */
export function useLegacyDownloadToken(): UseDepotTokenResult {
	return useDepotCredential("legacy-download-token", "legacy Download Token");
}

export { toFormState as toDepotTokenFormState };
