import { defineConfig, devices } from "@playwright/test";

/**
 * Live-stack Playwright coverage (issue #468, closing the disclosed gap in
 * docs/testing.md's "Fresh-stack M1/M2 parity matrix", issue #444). These
 * tests do NOT start their own server — they assume a fully brought-up
 * Waypoint stack (nginx + backend + both runners + postgres, per
 * deploy/docker-compose.yml) is already reachable at `E2E_BASE_URL`.
 * `deploy/scripts/e2e-playwright.sh` brings that stack up in isolation,
 * seeds it, runs this suite, and tears it down — see that script and
 * docs/testing.md before running these tests directly.
 *
 * Browser binaries are a devDependency-managed local install
 * (`npx playwright install chromium`), never committed and never fetched at
 * appliance runtime — this is a development/CI-time test tool, not part of
 * the air-gapped frontend build (ADR-0007). `npm run build`'s external-asset
 * guard never scans this directory; `dist/` never contains it.
 */
export default defineConfig({
	testDir: "./e2e",
	timeout: 60_000,
	expect: { timeout: 15_000 },
	fullyParallel: false,
	forbidOnly: !!process.env.CI,
	retries: 0,
	workers: 1,
	reporter: [["list"]],
	use: {
		baseURL: process.env.E2E_BASE_URL ?? "https://127.0.0.1:19543",
		ignoreHTTPSErrors: true,
		trace: "retain-on-failure",
		screenshot: "only-on-failure",
	},
	projects: [
		{
			name: "chromium",
			use: { ...devices["Desktop Chrome"] },
		},
	],
});
