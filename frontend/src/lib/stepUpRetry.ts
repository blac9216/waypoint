/**
 * Persists a step-up-gated write's replay intent across the full-page OIDC
 * redirect (issue #521/#534) — `stepUpOidcLogin()` navigates the browser
 * away entirely, so nothing in the calling component's JS context survives
 * to "just retry" once it returns. `sessionStorage` (not React state) is
 * the only thing that does: the callback lands as a fresh page load,
 * `AuthProvider` restores the session, and whoever re-mounts on the
 * restored route reads this back to know a write is waiting to be replayed.
 *
 * Deliberately generic (`kind` + opaque `payload`), not credential-specific:
 * issue #521's own text names remediation/update-apply as later step-up call
 * sites, and this shape costs nothing extra to keep them from reinventing
 * the same stash.
 */
const STEP_UP_RETRY_KEY = "waypoint.stepup.retry";

export interface StepUpRetryIntent<TPayload> {
	kind: string;
	payload: TPayload;
}

export function stashStepUpRetry<TPayload>(intent: StepUpRetryIntent<TPayload>): void {
	try {
		window.sessionStorage.setItem(STEP_UP_RETRY_KEY, JSON.stringify(intent));
	} catch {
		// best-effort — worst case the user re-does the edit by hand after re-auth.
	}
}

/** Read-once (consumed): returns the stashed intent only when `kind` matches, so an unrelated stashed retry from a different flow is never misapplied. */
export function consumeStepUpRetry<TPayload>(kind: string): TPayload | null {
	try {
		const raw = window.sessionStorage.getItem(STEP_UP_RETRY_KEY);
		if (!raw) {
			return null;
		}
		window.sessionStorage.removeItem(STEP_UP_RETRY_KEY);
		const parsed = JSON.parse(raw) as StepUpRetryIntent<TPayload>;
		return parsed.kind === kind ? parsed.payload : null;
	} catch {
		return null;
	}
}
