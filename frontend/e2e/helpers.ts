import type { Page } from "@playwright/test";

/**
 * Shared helpers for the live-stack Playwright suite (issue #468, PKCE
 * rewrite issue #848). `deploy/scripts/e2e-playwright.sh` brings up an
 * isolated stack with the keycloak-dev-admin service (epic #841 issue #846)
 * provisioning a persistent Keycloak-realm Admin user, and exports that
 * user's username/password here as E2E_ADMIN_USERNAME/E2E_ADMIN_PASSWORD —
 * see that script for exactly how (never hardcoded here or committed).
 *
 * `login()` below drives the REAL Keycloak authorization-code/PKCE flow
 * (`src/lib/oidc.ts`, `src/components/auth/LoginScreen.tsx`) through nginx,
 * not the dev-flag local-auth form — issue #848's whole point is that a
 * browser suite exercising local auth cannot catch Keycloak
 * hostname/`/auth`-prefix/callback regressions that a headless stack can
 * still start "healthy" with. The API-seeding curl calls in
 * e2e-playwright.sh are shell-only (never drive a browser) and may keep
 * using local auth — that overlay's scope is unchanged by this file.
 */

export function keycloakUsername(): string {
	return process.env.E2E_ADMIN_USERNAME ?? "developer";
}

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
 * The one browser-facing origin this whole run is allowed to touch — derived
 * from Playwright's own `baseURL` (`playwright.config.ts`, itself
 * `E2E_BASE_URL`), never hardcoded. Every origin-discipline assertion below
 * compares against this, not a literal `localhost`/`127.0.0.1` — the
 * configured origin legitimately varies per run (a devcontainer's own
 * namespace sometimes cannot reach the published host port at all, so
 * e2e-playwright.sh joins its own edge network and points Playwright at
 * `https://nginx` instead — see that script's "Playwright base URL
 * reachability" section). What must NEVER vary mid-flow is that this exact
 * origin is the only one ever navigated to.
 */
export function configuredOrigin(): string {
	const base = process.env.E2E_BASE_URL;
	if (!base) {
		throw new Error("E2E_BASE_URL is not set — run this suite via deploy/scripts/e2e-playwright.sh.");
	}
	return new URL(base).origin;
}

/**
 * Issue #848's origin-discipline acceptance criterion, enforced as a single
 * reusable check rather than duplicated ad hoc per assertion site: a URL
 * reached anywhere in the PKCE flow (the app itself, the Keycloak login
 * form, the `/oidc/callback` landing) must be same-origin with
 * `configuredOrigin()`, must never resolve to a bare container service name
 * (`keycloak:8080` — reachable only from other containers on the stack's
 * `internal` network, never from a real browser), and any Keycloak realm
 * path must carry nginx's `/auth` proxy prefix (`deploy/nginx/conf.d/
 * default.conf`'s `location /auth/`) — a bare `/realms/...` means the prefix
 * was dropped somewhere and the browser is talking to Keycloak's own root
 * context, exactly the #534 regression this suite exists to catch.
 */
export function assertOnConfiguredOrigin(url: string, where: string): void {
	const origin = configuredOrigin();
	if (url !== origin && !url.startsWith(`${origin}/`)) {
		throw new Error(`${where}: expected an URL on the configured origin ${origin}, got ${url}`);
	}
	if (/keycloak:8080/i.test(url)) {
		throw new Error(`${where}: URL resolved to the internal container service name, not the public origin: ${url}`);
	}
	if (/\/realms\//.test(url) && !/\/auth\/realms\//.test(url)) {
		throw new Error(`${where}: URL carries a bare /realms/ path missing the /auth proxy prefix: ${url}`);
	}
}

/**
 * Drives the real Keycloak authorization-code/PKCE flow through nginx:
 * clicks "Sign in with Keycloak" (an EXACT name match — the bug this test
 * suite's own validation found (#847 orchestrator comment): the previous
 * `getByRole('button', { name: /sign in/i })` regex also matched the
 * dev-flag local-auth form's "Sign in (local)" button, a strict-mode
 * violation with two legitimately-different, correctly-labeled buttons on
 * screen at once — not a duplicate-accessible-name defect in the app, just
 * an over-broad test selector), fills the Keycloak login form via its
 * stable default-theme selectors (`#username`/`#password`/`#kc-login` —
 * issue #848's own risk note), and waits for the `/oidc/callback` round trip
 * to land back on an authenticated screen.
 */
const LOGIN_RETRY_ATTEMPTS = 4;
const LOGIN_RETRY_DELAY_MS = 1500;

export async function login(page: Page, username = keycloakUsername(), password = adminPassword()): Promise<void> {
	let lastFailure = "";
	for (let attempt = 1; attempt <= LOGIN_RETRY_ATTEMPTS; attempt++) {
		await page.goto("/");
		assertOnConfiguredOrigin(page.url(), "login() initial navigation");

		await page.getByRole("button", { name: "Sign in with Keycloak", exact: true }).click();
		await page.waitForURL(/\/auth\/realms\/waypoint\//, { timeout: 15_000 }).catch(() => {});
		assertOnConfiguredOrigin(page.url(), "login() Keycloak redirect");

		await page.locator("#username").fill(username);
		await page.locator("#password").fill(password);
		await page.locator("#kc-login").click();

		// NOT the "WAYPOINT" brand wordmark: `App.tsx`'s `AppShell` renders
		// `<LoginScreen />` (which shows that exact same wordmark on its own
		// card) for every status OTHER than "signed-in" — including the
		// transient "signing-in" render `/oidc/callback` mounts WHILE
		// `completeOidcCallback()` is still awaiting discovery/token-exchange,
		// before `persistSession()` has run. Waiting on the wordmark alone is
		// a genuine, intermittent race: it can resolve on THAT transient
		// LoginScreen render, before the session is actually persisted to
		// `sessionStorage` — found as a real flake (roughly 1-in-3) while
		// validating this suite's own `GET /api/v1/auth/me` check, which reads
		// `sessionStorage` immediately after `login()` returns. `Chrome` (and
		// the "Dashboard" nav link inside it) only mounts once
		// `status === "signed-in" && user` — the same condition
		// `completeOidcCallback` sets synchronously alongside
		// `persistSession()`, so there is no window where this link is visible
		// but the session isn't yet written.
		const dashboardLink = page.getByRole("link", { name: "Dashboard" });
		const alert = page.getByRole("alert");
		const outcome = await Promise.race([
			dashboardLink.waitFor({ state: "visible", timeout: 15_000 }).then(() => "signed-in" as const),
			alert.waitFor({ state: "visible", timeout: 15_000 }).then(() => "alert" as const),
		]).catch(() => "neither" as const);

		if (outcome === "signed-in") {
			// The callback round trip (`/oidc/callback`) replaces the URL via
			// `history.replaceState` before this resolves — same-origin proof
			// that the whole flow stayed on the configured origin throughout.
			assertOnConfiguredOrigin(page.url(), "login() post-callback landing");
			return;
		}

		const alertText = outcome === "alert" ? ((await alert.textContent()) ?? "").trim() : "";
		lastFailure = alertText ? `alert: "${alertText}"` : "no alert shown, Dashboard nav link never appeared";

		if (attempt < LOGIN_RETRY_ATTEMPTS) {
			// Same cold-start warm-up rationale issue #503 documented for the
			// old local-auth flow: the very first request against a freshly
			// healthy backend/Keycloak can still race readiness.
			await page.waitForTimeout(LOGIN_RETRY_DELAY_MS);
		}
	}

	throw new Error(`login() did not reach an authenticated screen after ${LOGIN_RETRY_ATTEMPTS} attempts (last: ${lastFailure})`);
}

/**
 * Reads one field off the app's own `sessionStorage` session
 * (`lib/auth.tsx`'s `waypoint.session`, `STORAGE_KEY`) — used to
 * independently verify server-side behavior (`GET /api/v1/auth/me`, real
 * RP-Initiated Logout) rather than trusting only the SPA's own rendered
 * chrome.
 */
async function readSessionField(page: Page, field: "token" | "idToken"): Promise<string | undefined> {
	return page.evaluate((f) => {
		const raw = window.sessionStorage.getItem("waypoint.session");
		if (!raw) {
			return undefined;
		}
		try {
			const value = (JSON.parse(raw) as Record<string, unknown>)[f];
			return typeof value === "string" && value.trim() !== "" ? value : undefined;
		} catch {
			return undefined;
		}
	}, field);
}

/** The bearer access token — required; throws if `login()` wasn't called first. */
export async function currentSessionToken(page: Page): Promise<string> {
	const token = await readSessionField(page, "token");
	if (!token) {
		throw new Error("currentSessionToken(): no usable token in sessionStorage — was login() called first?");
	}
	return token;
}

/**
 * The raw Keycloak ID token (`StoredSession.idToken`, issue #873's fix) —
 * present whenever the token response carried one (it does here: `scope`
 * includes `openid`). Needed to replay as `id_token_hint` for a one-hop
 * RP-Initiated Logout, exactly as `lib/auth.tsx`'s real `logout()` does —
 * required (not optional) here because this suite exists to prove the real
 * one-hop behavior, not the confirmation-page fallback for a session that
 * happens to lack one.
 */
export async function currentSessionIdToken(page: Page): Promise<string> {
	const idToken = await readSessionField(page, "idToken");
	if (!idToken) {
		throw new Error("currentSessionIdToken(): no usable idToken in sessionStorage — was login() called first?");
	}
	return idToken;
}

/**
 * `GET /api/v1/auth/config` (same endpoint `lib/auth.tsx`'s `fetchAuthConfig`
 * calls) — the OIDC client id needed to build a same-origin, same-client
 * end-session URL exactly as the app's own `logout()` would, since issue
 * #868 (no sign-out control anywhere in the rendered UI) means this suite
 * cannot simply click a button.
 */
export async function oidcClientId(page: Page, origin: string): Promise<string> {
	const response = await page.request.get(`${origin}/api/v1/auth/config`);
	const body = (await response.json()) as { oidc_client_id?: unknown };
	if (typeof body.oidc_client_id !== "string" || body.oidc_client_id.trim() === "") {
		throw new Error("oidcClientId(): GET /api/v1/auth/config returned no usable oidc_client_id.");
	}
	return body.oidc_client_id;
}

/** Invented, obviously-fictional hostnames — never a real lab host (CLAUDE.md). */
export const UNREACHABLE_HOST = "srg-e2e-01.example.internal";
