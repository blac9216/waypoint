/**
 * Hand-rolled OIDC authorization-code + PKCE flow (issue #534) — no external
 * OIDC library. This is a public SPA client (`waypoint-frontend`,
 * `deploy/keycloak/realm/waypoint-realm.json`): it cannot hold a client
 * secret, so PKCE (RFC 7636, `S256`) is the confidentiality mechanism, not
 * optional hardening. The whole flow is `fetch` + `crypto.subtle` — every
 * primitive it needs (`crypto.subtle.digest`, `crypto.getRandomValues`,
 * `URLSearchParams`) is already a browser built-in, so pulling in a library
 * (`oidc-client-ts`, `react-oidc-context`, ...) would only add bundle weight
 * an air-gapped build has no CDN to fetch anyway and no real complexity to
 * justify — the whole exchange is two `fetch` calls and a redirect.
 *
 * Discovery is same-origin: nginx proxies `/auth/` to Keycloak with the
 * prefix stripped (`deploy/nginx/conf.d/default.conf`, issue #28), so the
 * browser-facing authority the backend hands back from `GET /auth/config`
 * (`Oidc:PublicAuthority`, default `/auth/realms/waypoint`) resolves without
 * any hardcoded host — required for an appliance with no fixed public
 * hostname (ADR-0004, air-gapped instances).
 */

const PKCE_VERIFIER_KEY = "waypoint.oidc.pkce_verifier";
const OIDC_STATE_KEY = "waypoint.oidc.state";
const OIDC_RETURN_TO_KEY = "waypoint.oidc.return_to";

export interface OidcDiscoveryDocument {
	authorization_endpoint: string;
	token_endpoint: string;
	end_session_endpoint?: string;
}

/** `{Authority}/.well-known/openid-configuration` — the one well-known OIDC path every issuer serves (RFC 8414 / OIDC Discovery 1.0). */
export async function discoverOidc(authority: string): Promise<OidcDiscoveryDocument> {
	const base = authority.endsWith("/") ? authority.slice(0, -1) : authority;
	const response = await fetch(`${base}/.well-known/openid-configuration`, {
		headers: { Accept: "application/json" },
	});
	if (!response.ok) {
		throw new Error(`OIDC discovery failed: ${response.status} ${response.statusText}`);
	}
	return (await response.json()) as OidcDiscoveryDocument;
}

function base64UrlEncode(bytes: Uint8Array): string {
	let binary = "";
	for (const byte of bytes) {
		binary += String.fromCharCode(byte);
	}
	return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function randomString(byteLength: number): string {
	const bytes = new Uint8Array(byteLength);
	crypto.getRandomValues(bytes);
	return base64UrlEncode(bytes);
}

/** RFC 7636 `S256`: `BASE64URL(SHA256(ASCII(code_verifier)))`. */
async function pkceChallengeFromVerifier(verifier: string): Promise<string> {
	const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier));
	return base64UrlEncode(new Uint8Array(digest));
}

/**
 * `window.location.origin` + this app's fixed callback route — same path
 * for every deployment, no configuration needed per site. Deliberately
 * `/oidc/callback`, not `/auth/callback`: nginx's `location /auth/`
 * (`deploy/nginx/conf.d/default.conf`, issue #28) proxies that whole prefix
 * straight to Keycloak with the prefix stripped, so a same-origin SPA route
 * under `/auth/` would never reach the React app at all — it would 404 (or
 * worse, silently hit Keycloak) at the edge before `index.html` was ever
 * served. `/oidc/` has no nginx location of its own, so it falls through to
 * the catch-all `location /` (`try_files ... /index.html`) exactly like
 * every other client-side route (`/dashboard`, `/live-run`, ...).
 */
export function redirectUri(): string {
	return `${window.location.origin}/oidc/callback`;
}

export interface StartLoginOptions {
	/** `prompt=login` (step-up re-auth, issue #521/#534) forces a fresh credential prompt even with an active Keycloak SSO session. */
	prompt?: "login";
	/** Path (router-relative) to return to once the callback completes — defaults to the current location. */
	returnTo?: string;
}

/**
 * Builds the PKCE pair + `state`, stashes them in `sessionStorage` (survives
 * the full-page redirect; a page navigation away and back is exactly what
 * this flow is), and navigates the browser to Keycloak's `/authorize`
 * endpoint. There is nothing to await after this call on the happy path —
 * the page is about to unload.
 */
export async function startLogin(discovery: OidcDiscoveryDocument, clientId: string, options: StartLoginOptions = {}): Promise<void> {
	const verifier = randomString(32);
	const challenge = await pkceChallengeFromVerifier(verifier);
	const state = randomString(16);

	window.sessionStorage.setItem(PKCE_VERIFIER_KEY, verifier);
	window.sessionStorage.setItem(OIDC_STATE_KEY, state);
	window.sessionStorage.setItem(OIDC_RETURN_TO_KEY, options.returnTo ?? `${window.location.pathname}${window.location.search}`);

	const params = new URLSearchParams({
		client_id: clientId,
		redirect_uri: redirectUri(),
		response_type: "code",
		scope: "openid profile",
		state,
		code_challenge: challenge,
		code_challenge_method: "S256",
	});
	if (options.prompt) {
		params.set("prompt", options.prompt);
	}

	window.location.assign(`${discovery.authorization_endpoint}?${params.toString()}`);
}

export interface TokenResponseWire {
	access_token: unknown;
	expires_in: unknown;
	token_type: unknown;
}

export interface ExchangedTokens {
	accessToken: string;
	expiresAt: string;
}

/**
 * Reads `code`/`state` off the current URL (the callback landing after
 * `startLogin`'s redirect), validates `state` against the value stashed
 * before the redirect (CSRF protection — RFC 6749 §10.12), and exchanges
 * the code + PKCE verifier for tokens. Clears the stashed PKCE/state values
 * either way so a reload of the callback URL can't replay them.
 */
export async function completeLogin(discovery: OidcDiscoveryDocument, clientId: string, callbackUrl: string): Promise<ExchangedTokens> {
	const verifier = window.sessionStorage.getItem(PKCE_VERIFIER_KEY);
	const expectedState = window.sessionStorage.getItem(OIDC_STATE_KEY);
	window.sessionStorage.removeItem(PKCE_VERIFIER_KEY);
	window.sessionStorage.removeItem(OIDC_STATE_KEY);

	const url = new URL(callbackUrl);
	const error = url.searchParams.get("error");
	if (error) {
		throw new Error(`Keycloak returned an error: ${error} — ${url.searchParams.get("error_description") ?? "no description"}.`);
	}
	const code = url.searchParams.get("code");
	const state = url.searchParams.get("state");
	if (!code || !verifier) {
		throw new Error("Missing authorization code or PKCE verifier — the sign-in redirect did not complete correctly.");
	}
	if (!state || state !== expectedState) {
		throw new Error("OIDC state mismatch — the sign-in redirect may have been tampered with or replayed. Please sign in again.");
	}

	const response = await fetch(discovery.token_endpoint, {
		method: "POST",
		headers: { "Content-Type": "application/x-www-form-urlencoded" },
		body: new URLSearchParams({
			grant_type: "authorization_code",
			code,
			redirect_uri: redirectUri(),
			client_id: clientId,
			code_verifier: verifier,
		}).toString(),
	});
	if (!response.ok) {
		throw new Error(`Token exchange failed: ${response.status} ${response.statusText}`);
	}
	const wire = (await response.json()) as TokenResponseWire;
	if (typeof wire.access_token !== "string" || wire.access_token.trim() === "") {
		throw new Error("Token endpoint returned no usable access_token.");
	}
	if (typeof wire.expires_in !== "number" || !Number.isFinite(wire.expires_in)) {
		throw new Error("Token endpoint returned no usable expires_in.");
	}
	return {
		accessToken: wire.access_token,
		expiresAt: new Date(Date.now() + wire.expires_in * 1000).toISOString(),
	};
}

/** The router-relative path `startLogin` was called from, so the app can restore it after the callback round trip. Read-once (consumed). */
export function consumeReturnTo(): string {
	const value = window.sessionStorage.getItem(OIDC_RETURN_TO_KEY) ?? "/";
	window.sessionStorage.removeItem(OIDC_RETURN_TO_KEY);
	return value;
}

/**
 * Keycloak end-session redirect (RP-Initiated Logout, OIDC RP-Initiated
 * Logout 1.0). `client_id` is required: `waypoint-frontend`'s
 * `post.logout.redirect.uris` attribute (`deploy/keycloak/realm/waypoint-realm.json`)
 * is registered per-client, so without `client_id` Keycloak has no client
 * to validate `post_logout_redirect_uri` against and rejects the request
 * with 400 (issue #873) regardless of how well-formed the rest of the
 * query string is. Falls back to a same-origin reload when the discovery
 * document has no `end_session_endpoint` (not every issuer publishes one).
 */
export function endSessionUrl(discovery: OidcDiscoveryDocument, clientId: string, idTokenHint?: string): string | null {
	if (!discovery.end_session_endpoint) {
		return null;
	}
	const params = new URLSearchParams({
		client_id: clientId,
		post_logout_redirect_uri: window.location.origin,
	});
	if (idTokenHint) {
		params.set("id_token_hint", idTokenHint);
	}
	return `${discovery.end_session_endpoint}?${params.toString()}`;
}
