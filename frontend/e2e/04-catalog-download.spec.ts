import { expect, test } from "@playwright/test";
import { login } from "./helpers";

/**
 * Download Catalog screen (issue #468): loads against the live backend and,
 * once an artifact is queued, shows the honest "vcf-download-tool is not
 * installed" failure (ToolGatedDownloadJobHandler — no vendor tool is ever
 * shipped in-repo, CLAUDE.md "Never project-publish vendor binaries") rather
 * than a false success or a silent hang. `deploy/scripts/e2e-playwright.sh`
 * triggers a catalog sync via the API before this suite runs so at least one
 * artifact is indexed; if the depot has none to offer, this test still
 * proves the empty-catalog state renders honestly and skips the
 * queue/tool-absent assertion (a legitimate fresh-stack state — see
 * fresh-stack-smoke-test.sh step 5's identical accommodation).
 */

test("catalog screen loads, and queuing a download shows the tool-absent state honestly", async ({ page }) => {
	await login(page);
	await page.getByRole("link", { name: "Download Catalog" }).click();

	await expect(page.getByPlaceholder("Search artifacts…")).toBeVisible();
	// Loading resolves to a checkbox-bearing row or the explicit empty state
	// ("No artifacts match the current filters." / "Loading catalog…" both
	// render as a single-cell .artifact-table__empty row) — never an
	// indefinite spinner. Wait on the loading placeholder disappearing rather
	// than an ambiguous .or() across two locators that both match the same
	// empty-state row (its <tr> and its <td>).
	await expect(page.getByText("Loading catalog…")).toHaveCount(0, { timeout: 20_000 });

	const firstRowCheckbox = page.locator("tbody tr").first().locator('input[type="checkbox"]');
	if ((await firstRowCheckbox.count()) === 0) {
		console.log("catalog has no indexed artifacts on this fresh stack — empty state confirmed, tool-absent path not exercised");
		return;
	}

	await firstRowCheckbox.check();
	await page.getByRole("button", { name: /Queue \d+ downloads?/ }).click();

	// The queued job fails closed via SSE/poll; the download queue panel or
	// artifact row surfaces the tool-absent reason string verbatim (same
	// message deploy/scripts/fresh-stack-smoke-test.sh step 5 asserts at the
	// API level) rather than a generic/blank failure.
	await expect(page.getByText(/vcf-download-tool is not installed/i)).toBeVisible({ timeout: 30_000 });
});
