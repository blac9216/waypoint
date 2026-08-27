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
 *
 * WIZARD REALIGNMENT (issue #960): the target_scope wizard rework (epic #726
 * area, #874/#888/#900-adjacent changes) added a Preview step between
 * Schedule and Confirm, reworded the Scope step from "inventory" to
 * "components" copy, and moved the profile picker onto the Scope step
 * (`useScanWizard.ts`'s `canConfirm` still requires a `profile_id` on the
 * legacy target_ids path a never-discovered seed target takes — see issue
 * #900 for the parallel target_scope-only gap). `walkWizardToSubmit` below
 * walks the six real steps (Site -> Scope -> Credential -> Schedule ->
 * Preview -> Confirm) and selects a profile on Scope, matching
 * `StartScanScreen.test.tsx`'s own `goToScope`/`goToConfirm` helpers.
 *
 * KNOWN GAP (issue #980): `deploy/scripts/e2e-playwright.sh` does not seed a
 * `profiles` row (unlike `fresh-stack-smoke-test.sh`'s "6b" step), so
 * `E2E_PROFILE_NAME` is unset on a stack brought up via the documented e2e
 * recipe today and this whole file skips until that script exports it.
 */

const SITE_NAME = process.env.E2E_SITE_NAME;
const CREDENTIAL_NAME = process.env.E2E_CREDENTIAL_NAME;
const PROFILE_NAME = process.env.E2E_PROFILE_NAME;

test.skip(
	!SITE_NAME || !CREDENTIAL_NAME || !PROFILE_NAME,
	"E2E_SITE_NAME/E2E_CREDENTIAL_NAME/E2E_PROFILE_NAME not set — run via deploy/scripts/e2e-playwright.sh (issue #980: that script does not yet seed a profile row, so E2E_PROFILE_NAME is unset today)",
);

async function walkWizardToSubmit(page: import("@playwright/test").Page) {
	await page.getByRole("link", { name: "Start a Scan" }).click();

	await expect(page.getByText("Select a site")).toBeVisible();
	await page.getByText(SITE_NAME!, { exact: true }).click();
	await page.getByRole("button", { name: "Next" }).click();

	await expect(page.getByText("Scope — components")).toBeVisible();
	await expect(page.getByText("No cached components — scanning the whole target.")).toBeVisible();
	// The rendered option label is "<name> (<version>)" when the profile has a
	// version, so match on the seeded name as a substring rather than an exact
	// label (issue #980's future seed row's version, if any, is not asserted here).
	const profileOption = page.locator("option", { hasText: PROFILE_NAME! });
	await profileOption.waitFor({ state: "attached" });
	const profileValue = await profileOption.getAttribute("value");
	await page.getByRole("combobox").first().selectOption(profileValue!);
	await page.getByRole("button", { name: "Next" }).click();

	// Default "assigned" mode reads coverage straight off the target's own
	// bindings — the seeded target has none (deploy/scripts/e2e-playwright.sh
	// creates the site/target/credential but never binds them), so coverage
	// shows "Missing required binding" here. Switch to "Customize per
	// target/purpose" and apply the seeded credential as a per-target
	// override — the same override-mode path StartScanScreen.test.tsx's own
	// credential-gap tests exercise.
	await expect(page.locator(".start-scan-screen__panel-title", { hasText: "Credential" })).toBeVisible();
	await expect(page.getByText(/Missing required binding/)).toBeVisible();
	await page.getByText("Customize per target/purpose").click();
	// The per-target override table has one "<target> <purpose> saved
	// credential" combobox per (target, purpose) row — exactly one row exists
	// for the single seeded ssh target's single required "SRG SSH" purpose.
	// The same credential also appears in the "Bulk apply" column's own
	// select above it, so the option lookup is scoped to this row's select
	// rather than a bare page-wide `option` locator, which would resolve to
	// both and trip Playwright's strict-mode ambiguity check.
	const perTargetSelect = page.getByLabel(/saved credential$/);
	const credentialOption = perTargetSelect.locator("option", { hasText: CREDENTIAL_NAME! });
	await credentialOption.waitFor({ state: "attached" });
	const credentialValue = await credentialOption.getAttribute("value");
	await perTargetSelect.selectOption(credentialValue!);
	await expect(page.getByText(/^Override: /)).toBeVisible();
	await page.getByRole("button", { name: "Next" }).click();

	await expect(page.getByText("Coming in a future milestone (M3).")).toBeVisible();
	await page.getByRole("button", { name: "Next" }).click(); // -> preview

	await expect(page.getByText("Preview — would-be plan")).toBeVisible();
	await expect(page.getByText("Previewing the plan…")).toHaveCount(0);
	await page.getByRole("button", { name: "Next" }).click(); // -> confirm

	await expect(page.getByRole("button", { name: "Start scan" })).toBeVisible();
	await expect(page.getByRole("button", { name: "Start scan" })).toBeEnabled();
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
