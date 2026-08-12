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

export async function login(page: Page, username = ADMIN_USERNAME, password = adminPassword()): Promise<void> {
	await page.goto("/");
	await page.getByLabel("Username").fill(username);
	await page.getByLabel("Password").fill(password);
	await page.getByRole("button", { name: /sign in/i }).click();
	// Chrome only renders once auth + the initial /system fetch resolve —
	// waiting on the brand wordmark in the top bar (present on every
	// authenticated screen) is a stable signal that login succeeded and the
	// SPA shell mounted, without coupling to any one screen's own content.
	await page.getByText("WAYPOINT", { exact: true }).waitFor({ state: "visible" });
}

/** Invented, obviously-fictional hostnames — never a real lab host (CLAUDE.md). */
export const UNREACHABLE_HOST = "srg-e2e-01.example.internal";
