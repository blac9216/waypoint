import { expect, test } from "@playwright/test";
import {
	assertOnConfiguredOrigin,
	configuredOrigin,
	currentSessionIdToken,
	currentSessionToken,
	keycloakUsername,
	login,
	oidcClientId,
} from "./helpers";

/**
 * Login + the global chrome's Runners indicator (issue #465), rewritten for
 * issue #848 to exercise the REAL Keycloak authorization-code/PKCE flow
 * (epic #841) instead of the dev-flag local-auth form. A headless stack can
 * start "healthy" while Keycloak still advertises `localhost`, has lost the
 * `/auth` proxy prefix, or rejects the SPA's `/oidc/callback` redirect —
 * local auth cannot see any of that; this suite drives the browser through
 * nginx exactly the way a real user's IdP redirect does.
 */

test("completes the Keycloak PKCE flow and lands on an authenticated screen", async ({ page }) => {
	await login(page);
	await expect(page.getByText("WAYPOINT", { exact: true }).first()).toBeVisible();
	// TopBar shows "<username> · <role>" once auth resolves — the configured
	// Keycloak dev-admin identity (deploy/keycloak-dev-admin, issue #846),
	// not the local-auth "admin" account.
	await expect(page.getByText(new RegExp(`${keycloakUsername()}\\s*·\\s*Admin`, "i"))).toBeVisible();
});

test("PKCE login stays on the configured public origin and the proxied /auth path throughout", async ({ page }) => {
	await page.goto("/");
	assertOnConfiguredOrigin(page.url(), "initial app load");

	await page.getByRole("button", { name: "Sign in with Keycloak", exact: true }).click();
	await page.waitForURL(/\/auth\/realms\/waypoint\//, { timeout: 15_000 });
	// The Keycloak login form itself must be reached under THIS origin's
	// /auth proxy prefix — never a bare /realms/ path (the #534 regression:
	// nginx's proxy_pass dropping the prefix, Keycloak rendering
	// self-referential URLs against its own unprefixed root context instead)
	// and never the internal container hostname (a real browser can never
	// reach `keycloak:8080` — only other containers on the stack's
	// `internal` network can).
	assertOnConfiguredOrigin(page.url(), "Keycloak login form");
	expect(page.url()).toContain("/auth/realms/waypoint/");
	expect(page.url()).not.toContain("keycloak:8080");

	await page.locator("#username").fill(keycloakUsername());
	await page.locator("#password").fill(process.env.E2E_ADMIN_PASSWORD ?? "");
	await page.locator("#kc-login").click();

	// Not the "WAYPOINT" wordmark — `LoginScreen` (unauthenticated) shows the
	// identical text, including transiently while `/oidc/callback` is still
	// exchanging the code (see `helpers.ts`'s `login()` for the full
	// explanation of this race, found live while validating this suite).
	// The "Dashboard" nav link only mounts once `Chrome` does, i.e. once the
	// session is actually persisted.
	await expect(page.getByRole("link", { name: "Dashboard" })).toBeVisible({ timeout: 15_000 });
	// `/oidc/callback` (src/lib/oidc.ts's redirectUri()) replaces the URL via
	// history.replaceState before the app finishes rendering — this proves
	// the landing point, after the full round trip, is still same-origin.
	assertOnConfiguredOrigin(page.url(), "post-callback landing");
});

test("rejects a bad Keycloak password without leaving the Keycloak login form", async ({ page }) => {
	await page.goto("/");
	await page.getByRole("button", { name: "Sign in with Keycloak", exact: true }).click();
	await page.waitForURL(/\/auth\/realms\/waypoint\//, { timeout: 15_000 });

	await page.locator("#username").fill(keycloakUsername());
	await page.locator("#password").fill("definitely-wrong-password");
	await page.locator("#kc-login").click();

	// A rejected credential re-renders Keycloak's OWN login form (its exact
	// error markup varies by theme/version, so the stable, version-proof
	// signal is that the browser never left the Keycloak realm's /auth/
	// path for /oidc/callback) — never silently completes.
	await page.waitForTimeout(1000);
	assertOnConfiguredOrigin(page.url(), "rejected Keycloak login");
	expect(page.url()).toContain("/auth/realms/waypoint/");
	await expect(page.locator("#username")).toBeVisible();
});

test("issuer/discovery document is served same-origin under /auth/realms/waypoint", async ({ page }) => {
	const origin = configuredOrigin();
	const response = await page.request.get(`${origin}/auth/realms/waypoint/.well-known/openid-configuration`);
	expect(response.ok()).toBe(true);
	const body = (await response.json()) as { issuer?: unknown; authorization_endpoint?: unknown; token_endpoint?: unknown };
	for (const value of [body.issuer, body.authorization_endpoint, body.token_endpoint]) {
		expect(typeof value).toBe("string");
		assertOnConfiguredOrigin(value as string, "discovery document endpoint");
		expect(value as string).toContain("/auth/realms/waypoint");
	}
});

test("GET /api/v1/auth/me reflects the configured Keycloak Admin identity after PKCE login", async ({ page }) => {
	await login(page);
	const token = await currentSessionToken(page);
	const origin = configuredOrigin();
	const response = await page.request.get(`${origin}/api/v1/auth/me`, {
		headers: { Authorization: `Bearer ${token}` },
	});
	expect(response.ok()).toBe(true);
	const body = (await response.json()) as { username?: unknown; role?: unknown };
	expect(body.username).toBe(keycloakUsername());
	expect(body.role).toBe("Admin");
});

/**
 * Issue #848's "verify logout" acceptance criterion, exercised at the
 * mechanism level: the rendered app has NO sign-out control anywhere in its
 * chrome (`AuthContext.logout` is wired in `lib/auth.tsx` but never invoked
 * from any component — tracked separately by #868; out of this issue's
 * frontend/e2e + e2e-playwright.sh file surface to fix here). This test
 * therefore drives the exact same steps the real `logout()` performs for an
 * OIDC session (issue #873's fix, live in `main`): discover the end-session
 * endpoint, replay `client_id` + the real `id_token_hint` — and proves the
 * fixed one-hop behavior: no Keycloak confirmation interstitial, the
 * redirect lands directly back on the configured origin, and the SSO
 * session is actually dead (a subsequent login is challenged for
 * credentials again, not silently re-authenticated).
 */
test("ending the Keycloak SSO session is a one-hop redirect that actually ends the SSO session", async ({ page }) => {
	await login(page);
	const origin = configuredOrigin();

	const discovery = (await (await page.request.get(`${origin}/auth/realms/waypoint/.well-known/openid-configuration`)).json()) as {
		end_session_endpoint?: string;
	};
	expect(typeof discovery.end_session_endpoint).toBe("string");
	assertOnConfiguredOrigin(discovery.end_session_endpoint as string, "end_session_endpoint");

	const clientId = await oidcClientId(page, origin);
	const idTokenHint = await currentSessionIdToken(page);
	await page.evaluate(() => window.sessionStorage.removeItem("waypoint.session"));

	const params = new URLSearchParams({
		client_id: clientId,
		post_logout_redirect_uri: origin,
		id_token_hint: idTokenHint,
	});
	await page.goto(`${discovery.end_session_endpoint}?${params.toString()}`);

	// Issue #873's fix: `client_id` + `id_token_hint` together end the
	// session in ONE hop — no "Do you want to log out?" confirmation page.
	// Assert its absence explicitly rather than just tolerating it: a
	// regression back to the confirmation page (e.g. a future change that
	// drops `id_token_hint`) must fail this test, not silently pass by
	// falling through an `if (visible) click()` escape hatch.
	await expect(page.getByRole("button", { name: /logout|log out/i })).toHaveCount(0);
	assertOnConfiguredOrigin(page.url(), "post-logout redirect");
	expect(page.url().replace(/\/$/, "")).toBe(origin);

	await page.goto("/");
	await expect(page.getByRole("button", { name: "Sign in with Keycloak", exact: true })).toBeVisible({ timeout: 15_000 });
	assertOnConfiguredOrigin(page.url(), "post-logout app landing");

	// The real proof the SSO cookie is gone, not just this app's own
	// sessionStorage: signing in again must show the Keycloak credential
	// form, not silently complete via a still-live SSO session.
	await page.getByRole("button", { name: "Sign in with Keycloak", exact: true }).click();
	await page.waitForURL(/\/auth\/realms\/waypoint\//, { timeout: 15_000 });
	await expect(page.locator("#username")).toBeVisible({ timeout: 15_000 });
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
