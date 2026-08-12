import { expect, test } from "@playwright/test";
import { login } from "./helpers";

/**
 * Login + the global chrome's Runners indicator (issue #465) — the
 * operator-visible proof that both compliance-runner and download-runner
 * are up on the runner topology (ADRs 0013/0014), replacing the
 * "runner-topology parity" disclosed gap in docs/testing.md.
 */

test("logs in via local auth and lands on an authenticated screen", async ({ page }) => {
	await login(page);
	await expect(page.getByText("WAYPOINT", { exact: true }).first()).toBeVisible();
	// TopBar shows "<username> · <role>" once auth resolves.
	await expect(page.getByText(/admin\s*·\s*Admin/i)).toBeVisible();
});

test("rejects a bad password with a visible error, no crash", async ({ page }) => {
	await page.goto("/");
	await page.getByLabel("Username").fill("admin");
	await page.getByLabel("Password").fill("definitely-wrong-password");
	await page.getByRole("button", { name: /sign in/i }).click();
	await expect(page.getByRole("alert")).toBeVisible();
});

test("top bar Runners indicator reports both compliance and download runners available", async ({ page }) => {
	// Since #496, `SystemProvider` (lib/system.tsx) fetches `GET /system`
	// immediately on sign-in AND re-polls live (SYSTEM_POLL_INTERVAL_MS,
	// plus a focus/visibility re-check) — no reload needed to observe
	// current runner status. `worker_registry` typically gains both
	// compliance-runner and download-runner rows within seconds of the
	// stack coming up (compliance-runner's healthcheck can report "healthy"
	// before its dispatcher/heartbeat loop finishes settling — see
	// e2e-playwright.sh's bring-up wait loop), so this polls the indicator's
	// title attribute directly rather than asserting on the very first
	// fetch, giving both runners' heartbeats room to land without relying on
	// a manual reload.
	await login(page);

	const indicator = page.locator(".top-bar__stigman", { hasText: /Runners/ });
	await expect(indicator).toBeVisible();

	const complianceSignature = /scan|discover|credential-test|remediate/;
	const downloadSignature = /catalog-index|download|bundle-export|bundle-import|content-library-sync|content-pull|content-import|update/;
	const bothPresent = (title: string) =>
		complianceSignature.test(title) && downloadSignature.test(title) && !/none reporting/i.test(title);

	await expect
		.poll(
			async () => {
				const title = (await indicator.getAttribute("title")) ?? "";
				return bothPresent(title);
			},
			{ timeout: 60_000, intervals: [2000] },
		)
		.toBe(true);
});
