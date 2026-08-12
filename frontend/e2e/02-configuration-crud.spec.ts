import { expect, test } from "@playwright/test";
import { login } from "./helpers";

/**
 * Configuration screen: Sites & Targets + Credentials tabs load and support
 * basic CRUD against the live backend (issue #468). All names/hosts are
 * invented per-run (timestamp-suffixed) — never a real lab identifier
 * (CLAUDE.md).
 */

test.describe("Configuration — Sites, Targets, Credentials", () => {
	test.beforeEach(async ({ page }) => {
		await login(page);
		await page.getByRole("link", { name: "Configuration" }).click();
		await expect(page.getByRole("button", { name: "Sites & Targets" })).toBeVisible();
	});

	test("creates a site, adds a target under it, then deletes both", async ({ page }) => {
		const stamp = Date.now();
		const siteName = `e2e-site-${stamp}`;
		const targetName = `e2e-target-${stamp}`;

		// Sites & Targets tab is the default. The sidebar's site name and the
		// targets panel's "<NAME> · TARGETS" title both contain the site name
		// (uppercased in the panel title) — scope to the sidebar row
		// specifically so this doesn't hit a strict-mode multi-match once a
		// site is selected.
		await page.getByRole("button", { name: "Add site" }).click();
		await page.getByPlaceholder("e.g. Alpha Enclave").fill(siteName);
		await page.getByRole("button", { name: "Save" }).click();

		const sidebarSiteRow = page.locator(".config-sidebar__item", { hasText: siteName });
		await expect(sidebarSiteRow).toBeVisible();
		await sidebarSiteRow.getByRole("button", { name: siteName, exact: false }).first().click();

		await page.getByRole("button", { name: "Add target" }).click();
		await page.getByPlaceholder("e.g. vcsa-01").fill(targetName);
		await page.getByPlaceholder("vcsa-01.example.internal").fill("srg-e2e-target.example.internal");
		await page.getByRole("button", { name: "Save" }).click();

		await expect(page.getByText(targetName)).toBeVisible();

		// Clean up: delete the target row, then the site (site delete requires
		// targets removed first — same rule the smoke script's confirm() dialog
		// enforces server-side).
		page.once("dialog", (dialog) => dialog.accept());
		await page
			.locator("tr", { hasText: targetName })
			.getByRole("button", { name: "Delete" })
			.click();
		await expect(page.getByText(targetName)).toHaveCount(0);

		page.once("dialog", (dialog) => dialog.accept());
		await page
			.locator(".config-sidebar__item", { hasText: siteName })
			.getByRole("button", { name: "Delete" })
			.click();
		await expect(page.locator(".config-sidebar__item", { hasText: siteName })).toHaveCount(0);
	});

	test("creates a shared service credential and deletes it", async ({ page }) => {
		const stamp = Date.now();
		const credName = `e2e-cred-${stamp}`;

		await page.getByRole("button", { name: "Credentials" }).click();
		await page.getByRole("button", { name: "Add credential" }).click();

		await page.getByPlaceholder("e.g. Alpha vCenter service account").fill(credName);
		await page.getByPlaceholder("required to enable Test").fill("invented-e2e-canary-not-a-real-secret");
		await page.getByRole("button", { name: "Save" }).click();

		await expect(page.getByText(credName)).toBeVisible();

		page.once("dialog", (dialog) => dialog.accept());
		await page
			.locator("tr", { hasText: credName })
			.getByRole("button", { name: "Delete" })
			.click();
		await expect(page.getByText(credName)).toHaveCount(0);
	});
});
