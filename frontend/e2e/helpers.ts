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
 * Issue #503: the very first browser login against a freshly-brought-up
 * stack occasionally races a cold-start backend-readiness failure (observed
 * pre-#502 at roughly 1-in-3) even though the seed phase's own
 * `POST /auth/login` a moment earlier, with the same credentials, had
 * already succeeded — i.e. not a credential problem.
 *
 * Crucially, that race surfaces as the **exact same** alert text a genuine
 * wrong password does: if the admin hash isn't resolved/readable yet,
 * `InMemoryLocalAuthenticationService.Authenticate` fails closed and returns
 * `null` (`InMemoryLocalAuthenticationService.cs`), which `AuthController.Login`
 * maps to `401 invalid_credentials` → "Invalid username or password."
 * (`AuthController.cs`), surfaced verbatim by `lib/auth.tsx`. There is no
 * distinct 5xx path for an unresolved hash, so alert text alone cannot
 * separate the cold-start race from a real rejection.
 *
 * Because these are indistinguishable by text, `login()` treats the first
 * few login failures as a warm-up probe: it retries *any* failure with
 * bounded backoff before giving up. This covers the cold-start race while
 * still failing a genuine credential break in seconds (the retries exhaust
 * quickly). The dedicated "rejects a bad password" test inlines its own
 * flow and does not use this helper, so real-rejection coverage is
 * unaffected by the retry.
 */
const LOGIN_RETRY_ATTEMPTS = 4;
const LOGIN_RETRY_DELAY_MS = 1500;

export async function login(page: Page, username = ADMIN_USERNAME, password = adminPassword()): Promise<void> {
	let lastFailure = "";
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
		lastFailure = alertText
			? `alert: "${alertText}"`
			: "no alert shown, wordmark never appeared";

		if (attempt < LOGIN_RETRY_ATTEMPTS) {
			// Any first-login failure is retried as a cold-start warm-up probe —
			// the invalid-credentials alert cannot be told apart from the #503
			// backend-readiness race by text, so we retry it too. A genuine
			// credential break simply exhausts these retries in a few seconds.
			await page.waitForTimeout(LOGIN_RETRY_DELAY_MS);
		}
	}

	throw new Error(`login() did not reach an authenticated screen after ${LOGIN_RETRY_ATTEMPTS} attempts (last: ${lastFailure})`);
}

/** Invented, obviously-fictional hostnames — never a real lab host (CLAUDE.md). */
export const UNREACHABLE_HOST = "srg-e2e-01.example.internal";
