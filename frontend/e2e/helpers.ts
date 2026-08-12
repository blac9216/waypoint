import type { Page } from "@playwright/test";

/**
 * Shared helpers for the live-stack Playwright suite (issue #468).
 * `deploy/scripts/e2e-playwright.sh` provisions the admin account these
 * tests log in as — see that script for how the password is generated
 * (a fresh, invented-per-run value, never hardcoded here or committed).
 */

export const ADMIN_USERNAME = "admin";

export function adminPassword(): string {
	const pw = process.env.E2E_ADMIN_PASSWORD;
	if (!pw) {
		throw new Error(
			"E2E_ADMIN_PASSWORD is not set — run this suite via deploy/scripts/e2e-playwright.sh, which provisions and exports it.",
		);
	}
	return pw;
}

/**
 * The exact `error.message` `lib/auth.tsx`'s `login()` surfaces on a genuine
 * `401 invalid_credentials` from `POST /api/v1/auth/login` (see
 * `AuthController.Login`, `Waypoint.Api/Controllers/AuthController.cs`) —
 * the only alert text that means "the password was actually wrong". Every
 * other alert `login()` can produce (a transient network error, a timeout,
 * an unrecognized role, etc. — see `auth.tsx`'s `toRole`/`toWireText`/
 * `catch` block) carries a different message, because it is constructed
 * from a different `ApiError.status`/`code` or a hardcoded fallback string.
 * That asymmetry is what lets `login()` below retry safely: it is retrying
 * on "the message wasn't this one", never on "the message looked wrong".
 */
const INVALID_CREDENTIALS_MESSAGE = "Invalid username or password.";

/**
 * Issue #503: the very first browser login against a freshly-brought-up
 * stack occasionally raced a transient failure (observed pre-#502 at
 * roughly 1-in-3) even though the seed phase's own `POST /auth/login` a
 * moment earlier, with the same credentials, had already succeeded — i.e.
 * not a credential problem. Retries here are scoped tightly to that shape:
 * only when the alert's text is something *other than* the exact
 * `AuthController`-authored "wrong password" message, meaning `login()`
 * threw on a network hiccup, a timeout, or some other transient `ApiError`
 * rather than a real 401. A genuine bad-password rejection (this repo's
 * own "rejects a bad password" test, or any real auth break) still fails
 * immediately on the first attempt — this never masks that.
 */
const LOGIN_RETRY_ATTEMPTS = 4;
const LOGIN_RETRY_DELAY_MS = 1500;

export async function login(page: Page, username = ADMIN_USERNAME, password = adminPassword()): Promise<void> {
	for (let attempt = 1; attempt <= LOGIN_RETRY_ATTEMPTS; attempt++) {
		await page.goto("/");
		await page.getByLabel("Username").fill(username);
		await page.getByLabel("Password").fill(password);
		await page.getByRole("button", { name: /sign in/i }).click();

		const wordmark = page.getByText("WAYPOINT", { exact: true }).first();
		const alert = page.getByRole("alert");
		const outcome = await Promise.race([
			wordmark.waitFor({ state: "visible", timeout: 15_000 }).then(() => "signed-in" as const),
			alert.waitFor({ state: "visible", timeout: 15_000 }).then(() => "alert" as const),
		]).catch(() => "neither" as const);

		if (outcome === "signed-in") {
			// Chrome only renders once auth + the initial /system fetch resolve —
			// the brand wordmark in the top bar (present on every authenticated
			// screen) is a stable signal that login succeeded and the SPA shell
			// mounted, without coupling to any one screen's own content.
			return;
		}

		const alertText = outcome === "alert" ? ((await alert.textContent()) ?? "").trim() : "";
		if (alertText === INVALID_CREDENTIALS_MESSAGE) {
			// A real credential rejection — never retry this; surface it exactly
			// like a single-attempt login would.
			throw new Error(`login() rejected: server returned "${alertText}"`);
		}

		if (attempt === LOGIN_RETRY_ATTEMPTS) {
			throw new Error(
				`login() did not reach an authenticated screen after ${LOGIN_RETRY_ATTEMPTS} attempts` +
					(alertText ? ` (last alert: "${alertText}")` : " (no alert shown, wordmark never appeared)"),
			);
		}

		await page.waitForTimeout(LOGIN_RETRY_DELAY_MS);
	}
}

/** Invented, obviously-fictional hostnames — never a real lab host (CLAUDE.md). */
export const UNREACHABLE_HOST = "srg-e2e-01.example.internal";
