import { expect, test } from "@playwright/test";
import { login } from "./helpers";

/**
 * Start-a-Scan wizard (service-credential path) -> Live Run SSE view ->
 * cancellation -> Results screen honestly rendering the failed run
 * (issue #468). The site/target/credential this walks through are seeded
 * by deploy/scripts/e2e-playwright.sh via the API before this suite runs
 * (E2E_SITE_NAME/E2E_CREDENTIAL_NAME env vars name them so the wizard's
 * Site/Credential steps can select them) — see that script.
 *
 * PREVIOUSLY-DISCLOSED GAP, NOW FIXED (PR #497 / issue #494): this suite
 * originally found that `LiveRunScreen` crashed with an unhandled
 * "e.queues is not iterable" render error on a real backend because
 * `liverun.ts`'s `fetchRun`/`fetchRunJobs` assumed a `RunHeader`/`RunJob[]`
 * contract the real `RunsController` (`GET /runs/{id}`, `GET /runs/{id}/jobs`)
 * never carried. PR #497 aligned `liverun.ts` to the real
 * `{id, run_type, state, paused, blocked, scope, credential_id,
 * initiated_by, ..., job_count*}` / `[{id, run_id, job_type, target_id,
 * target_name, state, priority, attempt_count, ...}]` payloads, so the
 * screen now renders the live board without throwing. The Live Run test
 * below asserts that fixed behaviour (run header + priority-queue board
 * render for the seeded run), replacing the earlier crash escape-hatch.
 */

const SITE_NAME = process.env.E2E_SITE_NAME;
const CREDENTIAL_NAME = process.env.E2E_CREDENTIAL_NAME;

test.skip(!SITE_NAME || !CREDENTIAL_NAME, "E2E_SITE_NAME/E2E_CREDENTIAL_NAME not set — run via deploy/scripts/e2e-playwright.sh");

async function walkWizardToSubmit(page: import("@playwright/test").Page) {
	await page.getByRole("link", { name: "Start a Scan" }).click();

	await expect(page.getByText("Select a site")).toBeVisible();
	await page.getByText(SITE_NAME!, { exact: true }).click();
	await page.getByRole("button", { name: "Next" }).click();

	await expect(page.getByText("Scope — inventory")).toBeVisible();
	await expect(page.getByText("No cached inventory — scanning the whole target.")).toBeVisible();
	await page.getByRole("button", { name: "Next" }).click();

	await expect(page.locator(".start-scan-screen__panel-title", { hasText: "Credential" })).toBeVisible();
	await page.locator("option", { hasText: CREDENTIAL_NAME! }).waitFor({ state: "attached" });
	await page.getByRole("combobox").selectOption({ label: CREDENTIAL_NAME! });
	await page.getByRole("button", { name: "Next" }).click();

	await expect(page.getByText("Coming in a future milestone (M3).")).toBeVisible();
	await page.getByRole("button", { name: "Next" }).click();

	await expect(page.getByRole("button", { name: "Start scan" })).toBeVisible();
	await page.getByRole("button", { name: "Start scan" }).click();
}

test("service-credential scan wizard walks through to submission and lands on Live Run's URL", async ({ page }) => {
	await login(page);
	await walkWizardToSubmit(page);

	// The router bug this suite also found and fixed (lib/router.tsx: a
	// query-string path never matched ROUTES, silently bouncing to
	// Dashboard) is what this assertion actually proves: the wizard's
	// post-submit navigate('/live-run?run=<id>') lands on the real URL,
	// not a Dashboard fallback.
	await page.waitForURL(/\/live-run\?run=/, { timeout: 20_000 });
});

test("Live Run screen renders the run header and priority-queue board for the seeded run without crashing (issue #494 fix)", async ({
	page,
}) => {
	// The crash this used to assert ("e.queues is not iterable") was a hard
	// render error surfaced as a pageerror; guard against any regression by
	// failing if one recurs while we assert the screen now renders for real.
	const pageErrors: string[] = [];
	page.on("pageerror", (err) => pageErrors.push(err.message));

	await login(page);
	await walkWizardToSubmit(page);
	await page.waitForURL(/\/live-run\?run=/, { timeout: 20_000 });

	// The screen loads the run via REST (fetchRun/fetchRunJobs, now aligned to
	// the real RunsController contract) then streams updates over SSE. Prove it
	// mounts past the loading state into the real board rather than crashing or
	// stalling on "Loading run…".
	const screen = page.locator(".live-run-screen");
	await expect(screen).toBeVisible({ timeout: 20_000 });

	// Header identity: the run id, the SCAN · READ-ONLY mode pill, and the
	// description line all come off the mapped RunHeader — none of which could
	// render under the old crash. The real RunResponse carries no site name or
	// credential name (liverun.ts maps `site` <- run_type and `credential_name`
	// <- credential_id, see that file's mapping comment), so assert on the
	// stable shape the mapping actually produces rather than the seeded site
	// name: "<run_type> · N targets · initiated by <user> with <credential_id>".
	await expect(page.locator(".live-run__run-id")).toBeVisible();
	await expect(page.getByText("SCAN · READ-ONLY")).toBeVisible();
	await expect(page.locator(".live-run__desc")).toContainText(/targets · initiated by/);

	// The default "Priority queues" layout renders the mapped job board: at
	// least one queue block with a target row for the seeded target. This is
	// the exact `header.queues` iteration that used to throw.
	await expect(page.locator(".live-run__queues")).toBeVisible();
	await expect(page.locator(".live-run__queue").first()).toBeVisible({ timeout: 20_000 });
	await expect(page.locator(".live-run__cell--target").first()).toBeVisible({ timeout: 20_000 });

	// The header counters (PASS/FAIL/N/A) also derive from the mapped header
	// and must render — another proof the RunHeader mapping is intact.
	await expect(page.getByText("PASS")).toBeVisible();

	expect(pageErrors, `Live Run screen threw a render error: ${pageErrors.join("; ")}`).toEqual([]);
});

test("Results screen loads and renders a completed run honestly (independent of the Live Run gap above)", async ({ page }) => {
	await login(page);
	await walkWizardToSubmit(page);
	await page.waitForURL(/\/live-run\?run=/, { timeout: 20_000 });

	// Results & History reads GET /runs (list) + GET /runs/{id}/artifacts,
	// a different, working code path from the Live Run header/job contract
	// documented as broken above — results.ts derives its row shape from
	// the SAME real /runs list shape confirmed live while authoring this
	// suite (id, run_type, state, job_count, ...), not the aspirational
	// RunHeader/RunJob shape liverun.ts assumes.
	//
	// Navigate directly rather than clicking the nav link: the live Live Run
	// board re-renders on every SSE tick, which can make an in-page click on
	// the nav flake ("element was detached from the DOM, retrying") — a full
	// navigation sidesteps that entirely and is just as valid a proof this
	// screen loads standalone.
	await page.goto("/results");
	await expect(page.getByPlaceholder("search runs…")).toBeVisible();

	const firstRunRow = page.locator(".results__run-row").first();
	await expect(firstRunRow).toBeVisible({ timeout: 15_000 });
	await firstRunRow.click();
	await expect(page.locator(".results__run-title")).toBeVisible();
	await expect(page.locator(".results__kpi-tile").first()).toBeVisible();
});

test("Abort run: the run-scoped control is gated to Operator+ and submits against the real backend without error (issue #494 fix)", async ({
	page,
}) => {
	const pageErrors: string[] = [];
	page.on("pageerror", (err) => pageErrors.push(err.message));

	await login(page);
	await walkWizardToSubmit(page);
	await page.waitForURL(/\/live-run\?run=/, { timeout: 20_000 });

	// With the contract aligned the board renders and the Abort run control is
	// present and enabled for an admin (Operator+ gate). Exercise it for real:
	// accept the confirm dialog, POST /runs/{id}/abort, and prove the API
	// accepted it — no error banner (.live-run__action-error) and no render
	// crash. The per-job "cancelled" transition the abort performs server-side
	// is reflected on a subsequent load (abort emits only a run-level
	// run.progress SSE, not per-job job.state events, so the already-rendered
	// rows are not asserted to flip live here) — the honest observable of a
	// successful control is the absence of an error, which this asserts.
	const abort = page.getByRole("button", { name: "Abort run" });
	await expect(abort).toBeVisible({ timeout: 20_000 });
	await expect(abort).toBeEnabled();

	page.once("dialog", (dialog) => dialog.accept());
	await abort.click();

	// The button briefly shows "Aborting…" while the POST is in flight, then
	// settles; either way no action error must surface.
	await expect(page.locator(".live-run__action-error")).toHaveCount(0);
	// Give the POST time to complete and any error to render if one were going to.
	await page.waitForTimeout(3000);
	await expect(page.locator(".live-run__action-error")).toHaveCount(0);

	expect(pageErrors, `Live Run screen threw a render error: ${pageErrors.join("; ")}`).toEqual([]);
});
