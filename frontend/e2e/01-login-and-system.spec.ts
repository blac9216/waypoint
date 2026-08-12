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
	// DISCLOSED GAP (found live by this suite, not fixed here): `lib/system.tsx`'s
	// `SystemProvider` fetches `GET /system` exactly ONCE, in a `useEffect`
	// keyed only on auth `status` becoming "signed-in" — there is no polling
	// interval anywhere in that file. So if compliance-runner's first
	// worker_registry heartbeat has not landed yet at the exact moment that
	// one fetch fires, the Runners indicator freezes at whatever it saw
	// then ("Runners 1/1") for the rest of the session, even though the
	// backend's own state keeps changing underneath it — confirmed directly
	// against a live stack while authoring this suite: `worker_registry`
	// itself gained both rows within seconds of both containers starting,
	// while polling the SAME running page's DOM for 150s straight never
	// moved off "Runners 1/1". A DOM `.poll()` on this indicator was
	// therefore not testing "does the operator eventually see both
	// runners" — it was testing "did the one-shot fetch happen to race
	// ahead of compliance-runner's heartbeat", a coin flip unrelated to any
	// assertion in this file. Fixing `SystemProvider` to poll live is a
	// real product change (shared chrome state, broad blast radius) beyond
	// this issue's test-coverage scope — this test instead reloads the page
	// (forcing a fresh one-shot fetch, the only refresh mechanism that
	// exists today) after giving both runners' backend heartbeats a fixed
	// grace period, which is what an operator would actually have to do
	// today to see current runner status.
	await login(page);

	const indicator = page.locator(".top-bar__stigman", { hasText: /Runners/ });
	await expect(indicator).toBeVisible();

	const complianceSignature = /scan|discover|credential-test|remediate/;
	const downloadSignature = /catalog-index|download|bundle-export|bundle-import|content-library-sync|content-pull|content-import|update/;
	const bothPresent = (title: string) =>
		complianceSignature.test(title) && downloadSignature.test(title) && !/none reporting/i.test(title);

	// Grace period for both runners' heartbeats to land in worker_registry
	// server-side (compliance-runner's healthcheck can report "healthy"
	// before backend finishes migrating — see e2e-playwright.sh's bring-up
	// wait loop — and its dispatcher/heartbeat loops self-heal on their own
	// retry backoff after that).
	await page.waitForTimeout(45_000);
	await page.reload();
	await page.getByText("WAYPOINT", { exact: true }).first().waitFor({ state: "visible" });

	await expect
		.poll(
			async () => {
				const title = (await indicator.getAttribute("title")) ?? "";
				return bothPresent(title);
			},
			{ timeout: 30_000, intervals: [2000] },
		)
		.toBe(true);
});
