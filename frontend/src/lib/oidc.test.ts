import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { completeLogin, consumeReturnTo, discoverOidc, endSessionUrl, redirectUri, startLogin } from "./oidc";

const DISCOVERY = {
	authorization_endpoint: "/auth/realms/waypoint/protocol/openid-connect/auth",
	token_endpoint: "/auth/realms/waypoint/protocol/openid-connect/token",
	end_session_endpoint: "/auth/realms/waypoint/protocol/openid-connect/logout",
};

describe("discoverOidc", () => {
	let originalFetch: typeof fetch;

	beforeEach(() => {
		originalFetch = globalThis.fetch;
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
	});

	it("fetches {authority}/.well-known/openid-configuration", async () => {
		const calls: string[] = [];
		globalThis.fetch = vi.fn(async (url: string) => {
			calls.push(url);
			return new Response(JSON.stringify(DISCOVERY), { status: 200, headers: { "Content-Type": "application/json" } });
		}) as unknown as typeof fetch;

		const doc = await discoverOidc("/auth/realms/waypoint");
		expect(calls).toEqual(["/auth/realms/waypoint/.well-known/openid-configuration"]);
		expect(doc).toEqual(DISCOVERY);
	});

	it("strips a trailing slash off the authority before appending the well-known path", async () => {
		const calls: string[] = [];
		globalThis.fetch = vi.fn(async (url: string) => {
			calls.push(url);
			return new Response(JSON.stringify(DISCOVERY), { status: 200, headers: { "Content-Type": "application/json" } });
		}) as unknown as typeof fetch;

		await discoverOidc("/auth/realms/waypoint/");
		expect(calls).toEqual(["/auth/realms/waypoint/.well-known/openid-configuration"]);
	});

	it("throws on a non-ok discovery response", async () => {
		globalThis.fetch = vi.fn(async () => new Response("nope", { status: 503, statusText: "Service Unavailable" })) as unknown as typeof fetch;
		await expect(discoverOidc("/auth/realms/waypoint")).rejects.toThrow(/OIDC discovery failed: 503/);
	});
});

describe("startLogin (PKCE + redirect)", () => {
	let assignedUrl: string | undefined;
	const originalLocation = window.location;

	beforeEach(() => {
		window.sessionStorage.clear();
		assignedUrl = undefined;
		// jsdom's `window.location.assign` is a real navigation call ("Not
		// implemented: navigation" otherwise) on a non-configurable property —
		// neither reassigning it, `vi.spyOn`, nor a `Proxy` wrapping the real
		// `Location` can override just that one method (a `Proxy`'s `get` trap
		// must return the target's own value for a non-configurable,
		// non-writable property, which `assign` is). A plain object snapshot
		// of the properties `oidc.ts` actually reads (`origin`, `pathname`,
		// `search`) plus a stub `assign` sidesteps the invariant entirely —
		// this module never touches any other `Location` member.
		Object.defineProperty(window, "location", {
			configurable: true,
			value: {
				origin: originalLocation.origin,
				// Getters (not snapshotted values) so a test that
				// `history.pushState`s before calling `startLogin` (to exercise
				// the default-returnTo path) is reflected here too.
				get pathname() {
					return originalLocation.pathname;
				},
				get search() {
					return originalLocation.search;
				},
				assign: (url: string) => {
					assignedUrl = url;
				},
			},
		});
	});

	afterEach(() => {
		Object.defineProperty(window, "location", { configurable: true, value: originalLocation });
	});

	it("builds an /authorize redirect with a PKCE S256 challenge, state, and the fixed /oidc/callback redirect_uri", async () => {
		await startLogin(DISCOVERY, "waypoint-frontend");

		expect(assignedUrl).toBeDefined();
		const url = new URL(assignedUrl!, "https://waypoint.example.internal");
		expect(url.pathname).toBe("/auth/realms/waypoint/protocol/openid-connect/auth");
		expect(url.searchParams.get("client_id")).toBe("waypoint-frontend");
		expect(url.searchParams.get("response_type")).toBe("code");
		expect(url.searchParams.get("redirect_uri")).toBe(redirectUri());
		expect(url.searchParams.get("code_challenge_method")).toBe("S256");
		// S256 challenge is base64url: no '+', '/', or '=' padding.
		const challenge = url.searchParams.get("code_challenge");
		expect(challenge).toBeTruthy();
		expect(challenge).not.toMatch(/[+/=]/);
		expect(url.searchParams.get("state")).toBeTruthy();
		// No prompt param on a plain sign-in.
		expect(url.searchParams.has("prompt")).toBe(false);
	});

	it("redirect_uri is /oidc/callback — deliberately outside nginx's /auth/ prefix (which proxies to Keycloak)", () => {
		expect(redirectUri()).toBe(`${window.location.origin}/oidc/callback`);
	});

	it("stashes the PKCE verifier and state in sessionStorage so completeLogin can validate them after the redirect", async () => {
		await startLogin(DISCOVERY, "waypoint-frontend");
		expect(window.sessionStorage.getItem("waypoint.oidc.pkce_verifier")).toBeTruthy();
		expect(window.sessionStorage.getItem("waypoint.oidc.state")).toBeTruthy();
	});

	it("sets prompt=login for step-up re-authentication and stashes the given returnTo", async () => {
		await startLogin(DISCOVERY, "waypoint-frontend", { prompt: "login", returnTo: "/config" });
		const url = new URL(assignedUrl!, "https://waypoint.example.internal");
		expect(url.searchParams.get("prompt")).toBe("login");
		expect(consumeReturnTo()).toBe("/config");
	});

	it("defaults returnTo to the current path when none is given", async () => {
		window.history.pushState(null, "", "/results?foo=bar");
		await startLogin(DISCOVERY, "waypoint-frontend");
		expect(consumeReturnTo()).toBe("/results?foo=bar");
	});
});

describe("completeLogin (code exchange)", () => {
	let originalFetch: typeof fetch;

	beforeEach(() => {
		originalFetch = globalThis.fetch;
		window.sessionStorage.clear();
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
	});

	function seedPkceState(state = "the-state") {
		window.sessionStorage.setItem("waypoint.oidc.pkce_verifier", "the-verifier");
		window.sessionStorage.setItem("waypoint.oidc.state", state);
	}

	it("posts the authorization code + PKCE verifier to the token endpoint and returns the access token/expiry", async () => {
		seedPkceState("the-state");
		let capturedBody: string | undefined;
		globalThis.fetch = vi.fn(async (_url: string, init?: RequestInit) => {
			capturedBody = init?.body as string;
			return new Response(JSON.stringify({ access_token: "tok-1", expires_in: 300, token_type: "Bearer" }), {
				status: 200,
				headers: { "Content-Type": "application/json" },
			});
		}) as unknown as typeof fetch;

		const before = Date.now();
		const result = await completeLogin(
			DISCOVERY,
			"waypoint-frontend",
			"https://waypoint.example.internal/oidc/callback?code=auth-code-123&state=the-state",
		);

		expect(result.accessToken).toBe("tok-1");
		expect(Date.parse(result.expiresAt)).toBeGreaterThanOrEqual(before + 300_000);

		const params = new URLSearchParams(capturedBody);
		expect(params.get("grant_type")).toBe("authorization_code");
		expect(params.get("code")).toBe("auth-code-123");
		expect(params.get("code_verifier")).toBe("the-verifier");
		expect(params.get("client_id")).toBe("waypoint-frontend");
		expect(params.get("redirect_uri")).toBe(redirectUri());
	});

	it("carries the id_token through so logout can send it as id_token_hint (issue #873)", async () => {
		seedPkceState("the-state");
		globalThis.fetch = vi.fn(
			async () =>
				new Response(JSON.stringify({ access_token: "tok-1", expires_in: 300, token_type: "Bearer", id_token: "id-tok-1" }), {
					status: 200,
					headers: { "Content-Type": "application/json" },
				}),
		) as unknown as typeof fetch;

		const result = await completeLogin(DISCOVERY, "waypoint-frontend", "https://waypoint.example.internal/oidc/callback?code=abc&state=the-state");

		expect(result.idToken).toBe("id-tok-1");
	});

	it.each([
		["absent", {}],
		["blank", { id_token: "   " }],
		["not a string", { id_token: 42 }],
	])("still returns a usable session when the token response's id_token is %s", async (_label, extra) => {
		seedPkceState("the-state");
		globalThis.fetch = vi.fn(
			async () =>
				new Response(JSON.stringify({ access_token: "tok-1", expires_in: 300, token_type: "Bearer", ...extra }), {
					status: 200,
					headers: { "Content-Type": "application/json" },
				}),
		) as unknown as typeof fetch;

		const result = await completeLogin(DISCOVERY, "waypoint-frontend", "https://waypoint.example.internal/oidc/callback?code=abc&state=the-state");

		// A missing id_token degrades logout to Keycloak's confirmation page —
		// it must never fail the sign-in itself.
		expect(result.accessToken).toBe("tok-1");
		expect(result.idToken).toBeUndefined();
	});

	it("clears the stashed PKCE verifier/state so a reload of the callback URL cannot replay them", async () => {
		seedPkceState("the-state");
		globalThis.fetch = vi.fn(
			async () =>
				new Response(JSON.stringify({ access_token: "tok-1", expires_in: 300 }), {
					status: 200,
					headers: { "Content-Type": "application/json" },
				}),
		) as unknown as typeof fetch;

		await completeLogin(DISCOVERY, "waypoint-frontend", "https://waypoint.example.internal/oidc/callback?code=abc&state=the-state");

		expect(window.sessionStorage.getItem("waypoint.oidc.pkce_verifier")).toBeNull();
		expect(window.sessionStorage.getItem("waypoint.oidc.state")).toBeNull();
	});

	it("rejects on a state mismatch (CSRF guard) without calling the token endpoint", async () => {
		seedPkceState("expected-state");
		globalThis.fetch = vi.fn(async () => {
			throw new Error("token endpoint should not have been called");
		}) as unknown as typeof fetch;

		await expect(
			completeLogin(DISCOVERY, "waypoint-frontend", "https://waypoint.example.internal/oidc/callback?code=abc&state=wrong-state"),
		).rejects.toThrow(/state mismatch/);
	});

	it("rejects when Keycloak's callback carries an error param instead of a code", async () => {
		seedPkceState("the-state");
		await expect(
			completeLogin(
				DISCOVERY,
				"waypoint-frontend",
				"https://waypoint.example.internal/oidc/callback?error=access_denied&error_description=user+cancelled&state=the-state",
			),
		).rejects.toThrow(/Keycloak returned an error: access_denied/);
	});

	it("rejects when the PKCE verifier was never stashed (e.g. a stale/replayed callback URL)", async () => {
		window.sessionStorage.setItem("waypoint.oidc.state", "the-state");
		await expect(
			completeLogin(DISCOVERY, "waypoint-frontend", "https://waypoint.example.internal/oidc/callback?code=abc&state=the-state"),
		).rejects.toThrow(/Missing authorization code or PKCE verifier/);
	});

	it("rejects on a non-ok token endpoint response", async () => {
		seedPkceState("the-state");
		globalThis.fetch = vi.fn(async () => new Response("bad", { status: 400, statusText: "Bad Request" })) as unknown as typeof fetch;
		await expect(
			completeLogin(DISCOVERY, "waypoint-frontend", "https://waypoint.example.internal/oidc/callback?code=abc&state=the-state"),
		).rejects.toThrow(/Token exchange failed: 400/);
	});
});

describe("endSessionUrl", () => {
	it("builds a post_logout_redirect_uri and client_id pointing at the app origin/client (issue #873)", () => {
		const url = endSessionUrl(DISCOVERY, "waypoint-frontend");
		expect(url).toBeTruthy();
		const parsed = new URL(url!, "https://waypoint.example.internal");
		expect(parsed.pathname).toBe("/auth/realms/waypoint/protocol/openid-connect/logout");
		expect(parsed.searchParams.get("post_logout_redirect_uri")).toBe(window.location.origin);
		expect(parsed.searchParams.get("client_id")).toBe("waypoint-frontend");
	});

	it("includes id_token_hint when provided — the parameter that makes Keycloak 302 instead of rendering a logout-confirm page (issue #873)", () => {
		const url = endSessionUrl(DISCOVERY, "waypoint-frontend", "the-id-token");
		const parsed = new URL(url!, "https://waypoint.example.internal");
		expect(parsed.searchParams.get("id_token_hint")).toBe("the-id-token");
	});

	it("omits id_token_hint entirely when the session carries no ID token", () => {
		const parsed = new URL(endSessionUrl(DISCOVERY, "waypoint-frontend")!, "https://waypoint.example.internal");
		expect(parsed.searchParams.has("id_token_hint")).toBe(false);
		// ...and an empty-string hint is omitted too, rather than sent as a
		// blank parameter Keycloak would reject.
		const blank = new URL(endSessionUrl(DISCOVERY, "waypoint-frontend", "")!, "https://waypoint.example.internal");
		expect(blank.searchParams.has("id_token_hint")).toBe(false);
	});

	it("returns null when the discovery document has no end_session_endpoint", () => {
		expect(endSessionUrl({ authorization_endpoint: "x", token_endpoint: "y" }, "waypoint-frontend")).toBeNull();
	});
});
