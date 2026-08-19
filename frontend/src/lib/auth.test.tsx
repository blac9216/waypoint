import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider, __resetAuthConfigCacheForTests } from "./auth";
import { useAuth } from "./auth-context";

const STORAGE_KEY = "waypoint.session";

function Probe() {
	const { user, status, token } = useAuth();
	if (status === "restoring") {
		return <div>restoring</div>;
	}
	return <div>{user ? `signed in as ${user.username} (${user.role}) token=${token}` : "signed out"}</div>;
}

/**
 * Mock `fetch` for one login round trip. `loginBody`/`meBody` are sent
 * verbatim so a test can post a role the closed set does not contain — which
 * is the whole point: these are the bytes an untrusted server can put on the
 * wire, not a value that has already been through the `Role` type.
 */
function mockAuthFetch(loginBody: unknown, meBody: unknown): void {
	globalThis.fetch = vi.fn(async (url: string) => {
		if (url === "/api/v1/auth/config") {
			// AuthProvider always probes this on mount (issue #534) — every
			// caller of this helper exercises the local-auth login() path, so
			// local_auth_enabled: true keeps that feature-detect from becoming
			// a second thing each test has to know about.
			return new Response(
				JSON.stringify({ local_auth_enabled: true, oidc_authority: "/auth/realms/waypoint", oidc_client_id: "waypoint-frontend" }),
				{ status: 200, headers: { "Content-Type": "application/json" } },
			);
		}
		if (url === "/api/v1/auth/login") {
			return new Response(JSON.stringify(loginBody), {
				status: 200,
				headers: { "Content-Type": "application/json" },
			});
		}
		if (url === "/api/v1/auth/me") {
			return new Response(JSON.stringify(meBody), {
				status: 200,
				headers: { "Content-Type": "application/json" },
			});
		}
		throw new Error(`unexpected fetch: ${url}`);
	}) as unknown as typeof fetch;
}

/** Renders a button that runs `login()` and swallows the rejection, so a
 * refused sign-in doesn't surface as an unhandled promise rejection. The
 * refusal is asserted through `Probe`/`sessionStorage`, not the throw. */
function LoginTrigger({ user = "someone", password = "pw" }: { user?: string; password?: string }) {
	const { login } = useAuth();
	return (
		<button
			type="button"
			onClick={() => {
				login(user, password).catch(() => {});
			}}
		>
			go
		</button>
	);
}

describe("AuthProvider session restore (issue #64 — login/refresh contract)", () => {
	let originalFetch: typeof fetch;

	beforeEach(() => {
		__resetAuthConfigCacheForTests();
		originalFetch = globalThis.fetch;
		window.sessionStorage.clear();
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
	});

	it("restores a session written in the current {token, role, expiresAt, username} shape on mount (page refresh)", async () => {
		// This is the exact object `login()` persists — a real page refresh
		// re-mounts AuthProvider and must read this back as signed-in, with
		// the full chrome able to render (no bare placeholder card).
		window.sessionStorage.setItem(
			STORAGE_KEY,
			JSON.stringify({
				token: "tok-restored",
				username: "admin",
				role: "Admin",
				expiresAt: new Date(Date.now() + 60_000).toISOString(),
			}),
		);

		render(
			<AuthProvider>
				<Probe />
			</AuthProvider>,
		);

		await waitFor(() =>
			expect(screen.getByText("signed in as admin (Admin) token=tok-restored")).toBeInTheDocument(),
		);
	});

	it("rejects a session in the old, buggy {token, user} shape instead of silently signing in with a broken user", async () => {
		// Regression guard for the exact defect in issue #64: the old shape
		// serialized fine for `token` but never matched `StoredSession`, so a
		// naive "does it have a token" check would pass while `user` stays
		// undefined. A correct readStoredSession() must reject this outright.
		window.sessionStorage.setItem(
			STORAGE_KEY,
			JSON.stringify({ token: "tok-old-shape", user: { username: "admin", role: "Admin" } }),
		);

		render(
			<AuthProvider>
				<Probe />
			</AuthProvider>,
		);

		await waitFor(() => expect(screen.getByText("signed out")).toBeInTheDocument());
	});

	it("rejects an expired stored session rather than treating it as signed-in", async () => {
		window.sessionStorage.setItem(
			STORAGE_KEY,
			JSON.stringify({
				token: "tok-expired",
				username: "admin",
				role: "Admin",
				expiresAt: new Date(Date.now() - 60_000).toISOString(),
			}),
		);

		render(
			<AuthProvider>
				<Probe />
			</AuthProvider>,
		);

		await waitFor(() => expect(screen.getByText("signed out")).toBeInTheDocument());
	});

	it("rejects a stored session with an invalid role value", async () => {
		window.sessionStorage.setItem(
			STORAGE_KEY,
			JSON.stringify({
				token: "tok-bad-role",
				username: "admin",
				role: "admin", // lowercase — not one of the closed PascalCase values
				expiresAt: new Date(Date.now() + 60_000).toISOString(),
			}),
		);

		render(
			<AuthProvider>
				<Probe />
			</AuthProvider>,
		);

		await waitFor(() => expect(screen.getByText("signed out")).toBeInTheDocument());
	});

	it("login() persists a session that a fresh mount (simulated refresh) reads back correctly", async () => {
		const futureExpiry = new Date(Date.now() + 60 * 60 * 1000).toISOString();
		globalThis.fetch = vi.fn(async (url: string) => {
			if (url === "/api/v1/auth/config") {
				return new Response(
					JSON.stringify({ local_auth_enabled: true, oidc_authority: "/auth/realms/waypoint", oidc_client_id: "waypoint-frontend" }),
					{ status: 200, headers: { "Content-Type": "application/json" } },
				);
			}
			if (url === "/api/v1/auth/login") {
				return new Response(
					JSON.stringify({ token: "tok-roundtrip", role: "Operator", expires_at: futureExpiry }),
					{ status: 200, headers: { "Content-Type": "application/json" } },
				);
			}
			if (url === "/api/v1/auth/me") {
				return new Response(JSON.stringify({ username: "opuser", role: "Operator" }), {
					status: 200,
					headers: { "Content-Type": "application/json" },
				});
			}
			throw new Error(`unexpected fetch: ${url}`);
		}) as unknown as typeof fetch;

		function LoginTrigger() {
			const { login } = useAuth();
			return (
				<button type="button" onClick={() => void login("opuser", "pw")}>
					go
				</button>
			);
		}

		const { unmount } = render(
			<AuthProvider>
				<LoginTrigger />
				<Probe />
			</AuthProvider>,
		);

		screen.getByText("go").click();

		await waitFor(() =>
			expect(screen.getByText("signed in as opuser (Operator) token=tok-roundtrip")).toBeInTheDocument(),
		);

		// Simulate a page refresh: unmount (sessionStorage survives — it isn't
		// component state) and re-mount a brand new AuthProvider tree with no
		// further network calls available.
		unmount();
		globalThis.fetch = vi.fn(() => {
			throw new Error("no network call expected on restore");
		}) as unknown as typeof fetch;

		render(
			<AuthProvider>
				<Probe />
			</AuthProvider>,
		);

		await waitFor(() =>
			expect(screen.getByText("signed in as opuser (Operator) token=tok-roundtrip")).toBeInTheDocument(),
		);
	});
});

/**
 * Wire-path role validation (PR #93 review, finding 2).
 *
 * `isRole()` guarded the `sessionStorage` restore path from the start, but the
 * login response — the only one of the two an attacker or a drifting server can
 * actually influence — was an unchecked cast. These cases pin the fix: an
 * unrecognized role from *either* endpoint refuses the sign-in outright rather
 * than half-accepting it (signed in, every guard denying, logged out again on
 * the next refresh).
 */
describe("AuthProvider wire-path role validation (issue #64 — fail closed on an unknown role)", () => {
	let originalFetch: typeof fetch;
	const futureExpiry = () => new Date(Date.now() + 60 * 60 * 1000).toISOString();

	beforeEach(() => {
		__resetAuthConfigCacheForTests();
		originalFetch = globalThis.fetch;
		window.sessionStorage.clear();
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
	});

	async function attemptLogin(loginBody: unknown, meBody: unknown) {
		mockAuthFetch(loginBody, meBody);
		render(
			<AuthProvider>
				<LoginTrigger />
				<Probe />
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("signed out")).toBeInTheDocument());
		screen.getByText("go").click();
	}

	/** Nothing signed in, and — the part that matters — nothing persisted, so a
	 * refresh cannot resurrect a session the login itself refused. */
	async function expectRefused() {
		await waitFor(() => expect(screen.getByText("signed out")).toBeInTheDocument());
		expect(window.sessionStorage.getItem(STORAGE_KEY)).toBeNull();
	}

	it.each([
		["lowercase (the backend never emits this casing)", "admin"],
		["outside the closed set", "SuperAdmin"],
		["empty string", ""],
		["a number", 4],
		["null", null],
		["an object", { name: "Admin" }],
	])("refuses a login whose /auth/login role is %s", async (_label, role) => {
		await attemptLogin(
			{ token: "tok-bad-login-role", role, expires_at: futureExpiry() },
			{ username: "admin", role: "Admin" },
		);
		await expectRefused();
	});

	it("refuses a login when /auth/login omits role entirely", async () => {
		await attemptLogin({ token: "tok-no-role", expires_at: futureExpiry() }, { username: "admin", role: "Admin" });
		await expectRefused();
	});

	it("refuses a login whose /auth/me role is not in the closed set", async () => {
		// The login response is impeccable here — only `me` is malformed. Before
		// the fix `me.role` was fetched and discarded, so this signed in cleanly.
		await attemptLogin(
			{ token: "tok-bad-me-role", role: "Admin", expires_at: futureExpiry() },
			{ username: "admin", role: "administrator" },
		);
		await expectRefused();
	});

	it("refuses a login when /auth/login and /auth/me report different valid roles", async () => {
		// Both values are in the closed set, so neither is individually
		// suspicious — it is the disagreement that is the contract violation.
		// Neither endpoint overrides the other; the sign-in is refused.
		await attemptLogin(
			{ token: "tok-divergent", role: "Admin", expires_at: futureExpiry() },
			{ username: "admin", role: "Viewer" },
		);
		await expectRefused();
	});

	it("surfaces a refused sign-in as an error the login screen can render, not a silent failure", async () => {
		function ErrorProbe() {
			const { error } = useAuth();
			return <div data-testid="err">{error ?? "none"}</div>;
		}
		mockAuthFetch(
			{ token: "tok-bad", role: "root", expires_at: futureExpiry() },
			{ username: "admin", role: "Admin" },
		);
		render(
			<AuthProvider>
				<LoginTrigger />
				<ErrorProbe />
			</AuthProvider>,
		);
		screen.getByText("go").click();

		await waitFor(() => expect(screen.getByTestId("err")).not.toHaveTextContent("none"));
		// The message names the offending endpoint; the rejected value itself
		// stays out of it (it is unbounded server-controlled text) and rides in
		// ApiError.detail instead.
		expect(screen.getByTestId("err").textContent).toContain("/auth/login");
		expect(screen.getByTestId("err").textContent).not.toContain("root");
	});

	it("still signs in normally when both endpoints agree on a valid role", async () => {
		// The guard must not be so eager that it breaks the happy path.
		await attemptLogin(
			{ token: "tok-good", role: "Cyber", expires_at: futureExpiry() },
			{ username: "cyberuser", role: "Cyber" },
		);
		await waitFor(() =>
			expect(screen.getByText("signed in as cyberuser (Cyber) token=tok-good")).toBeInTheDocument(),
		);
		expect(window.sessionStorage.getItem(STORAGE_KEY)).toContain('"role":"Cyber"');
	});
});

/**
 * Wire-path validation of the remaining session fields (PR #93 round 3).
 *
 * `role` was hardened first; `token`, `expires_at` and `username` were still
 * unchecked casts in the same function. Leaving them that way meant the next
 * reader could not tell which fields in `LoginResponseWire` were guaranteed and
 * which were merely asserted — the ambiguity that produced this bug originally.
 * These cases pin the same fail-closed treatment for all of them.
 *
 * Scope note: none of this touches `readStoredSession()`. The restore path's
 * looser handling of empty strings and odd date formats is #98 and stays there.
 */
describe("AuthProvider wire-path field validation (issue #64 — token, expires_at, username)", () => {
	let originalFetch: typeof fetch;
	const futureExpiry = () => new Date(Date.now() + 60 * 60 * 1000).toISOString();
	const goodMe = { username: "admin", role: "Admin" };

	beforeEach(() => {
		__resetAuthConfigCacheForTests();
		originalFetch = globalThis.fetch;
		window.sessionStorage.clear();
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
	});

	async function attemptLogin(loginBody: unknown, meBody: unknown) {
		mockAuthFetch(loginBody, meBody);
		render(
			<AuthProvider>
				<LoginTrigger />
				<Probe />
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("signed out")).toBeInTheDocument());
		screen.getByText("go").click();
	}

	async function expectRefused() {
		await waitFor(() => expect(screen.getByText("signed out")).toBeInTheDocument());
		expect(window.sessionStorage.getItem(STORAGE_KEY)).toBeNull();
	}

	it.each([
		["empty string", ""],
		["whitespace only", "   "],
		["a number", 12345],
		["null", null],
		["an object", { value: "tok" }],
	])("refuses a login whose token is %s", async (_label, token) => {
		// An empty/unusable token is the classic half-accepted session: it looks
		// signed in, carries no usable credential, and only unwinds when some
		// later request happens to 401.
		await attemptLogin({ token, role: "Admin", expires_at: futureExpiry() }, goodMe);
		await expectRefused();
	});

	it("refuses a login when token is absent entirely", async () => {
		await attemptLogin({ role: "Admin", expires_at: futureExpiry() }, goodMe);
		await expectRefused();
	});

	it.each([
		["not a parseable date", "whenever"],
		["an empty string", ""],
		["a number", 1767225600000],
		["null", null],
	])("refuses a login whose expires_at is %s", async (_label, expires_at) => {
		// Unchecked, Date.parse gives NaN, every `NaN <= now` test is false, the
		// session is persisted as if valid, and the *next* restore rejects it —
		// login succeeds, refresh signs you out.
		await attemptLogin({ token: "tok", role: "Admin", expires_at }, goodMe);
		await expectRefused();
	});

	it("refuses a login when expires_at is absent entirely", async () => {
		await attemptLogin({ token: "tok", role: "Admin" }, goodMe);
		await expectRefused();
	});

	it("still accepts an already-past expires_at from the wire (clock skew is the server's call)", async () => {
		// Deliberately NOT rejected here: the server owns session lifetime and the
		// client's clock may be skewed. Expiry is enforced on restore instead, so
		// this signs in now and is declined by readStoredSession() later.
		await attemptLogin(
			{ token: "tok-past", role: "Admin", expires_at: new Date(Date.now() - 60_000).toISOString() },
			goodMe,
		);
		await waitFor(() => expect(screen.getByText("signed in as admin (Admin) token=tok-past")).toBeInTheDocument());
	});

	it.each([
		["empty string", ""],
		["whitespace only", "  "],
		["a number", 7],
		["null", null],
	])("refuses a login whose /auth/me username is %s", async (_label, username) => {
		await attemptLogin({ token: "tok", role: "Admin", expires_at: futureExpiry() }, { username, role: "Admin" });
		await expectRefused();
	});

	it("names the offending endpoint and field without echoing the rejected value", async () => {
		function ErrorProbe() {
			const { error } = useAuth();
			return <div data-testid="err">{error ?? "none"}</div>;
		}
		mockAuthFetch({ token: "sekrit-looking-garbage", role: "Admin", expires_at: "not-a-date" }, goodMe);
		render(
			<AuthProvider>
				<LoginTrigger />
				<ErrorProbe />
			</AuthProvider>,
		);
		screen.getByText("go").click();

		await waitFor(() => expect(screen.getByTestId("err")).not.toHaveTextContent("none"));
		const message = screen.getByTestId("err").textContent ?? "";
		expect(message).toContain("/auth/login");
		expect(message).toContain("expires_at");
		expect(message).not.toContain("not-a-date");
	});

	it("does not present an unvalidated token as a bearer credential", async () => {
		// The token is narrowed before /auth/me is called, so a malformed one
		// costs no round trip and is never put in an Authorization header.
		const calls: string[] = [];
		globalThis.fetch = vi.fn(async (url: string) => {
			calls.push(url);
			if (url === "/api/v1/auth/config") {
				return new Response(
					JSON.stringify({ local_auth_enabled: true, oidc_authority: "/auth/realms/waypoint", oidc_client_id: "waypoint-frontend" }),
					{ status: 200, headers: { "Content-Type": "application/json" } },
				);
			}
			if (url === "/api/v1/auth/login") {
				return new Response(JSON.stringify({ token: "", role: "Admin", expires_at: futureExpiry() }), {
					status: 200,
					headers: { "Content-Type": "application/json" },
				});
			}
			throw new Error(`unexpected fetch: ${url}`);
		}) as unknown as typeof fetch;

		render(
			<AuthProvider>
				<LoginTrigger />
				<Probe />
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("signed out")).toBeInTheDocument());
		screen.getByText("go").click();

		await waitFor(() => expect(window.sessionStorage.getItem(STORAGE_KEY)).toBeNull());
		expect(calls).toEqual(["/api/v1/auth/config", "/api/v1/auth/login"]);
	});
});

/**
 * Restore-path validation hardening (issue #98, found in PR #93 review).
 *
 * `readStoredSession()` already rejected an unrecognized `role` strictly via
 * `isRole()`; `token`, `username`, and `expiresAt` were only loosely checked
 * (`typeof === "string"`, and whatever `Date.parse` tolerates). These cases
 * pin the fix: blank token/username and a non-ISO-8601 `expiresAt` are
 * refused, matching the strictness already applied to `role` and to the
 * wire path's `toWireText`/`toWireInstant`.
 */
describe("AuthProvider restored-session field validation (issue #98)", () => {
	beforeEach(() => {
		__resetAuthConfigCacheForTests();
		window.sessionStorage.clear();
	});

	const futureExpiry = () => new Date(Date.now() + 60 * 60 * 1000).toISOString();

	function seedStoredSession(overrides: Record<string, unknown>): void {
		window.sessionStorage.setItem(
			STORAGE_KEY,
			JSON.stringify({
				token: "tok-restored",
				username: "admin",
				role: "Admin",
				expiresAt: futureExpiry(),
				...overrides,
			}),
		);
	}

	it.each([
		["empty string", ""],
		["whitespace only", "   "],
	])("rejects a restored session whose token is %s", async (_label, token) => {
		seedStoredSession({ token });
		render(
			<AuthProvider>
				<Probe />
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("signed out")).toBeInTheDocument());
		// A rejected restore does not leave the bad session behind for the next mount.
		expect(window.sessionStorage.getItem(STORAGE_KEY)).toBeNull();
	});

	it.each([
		["empty string", ""],
		["whitespace only", "   "],
	])("rejects a restored session whose username is %s", async (_label, username) => {
		seedStoredSession({ username });
		render(
			<AuthProvider>
				<Probe />
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("signed out")).toBeInTheDocument());
	});

	it("rejects a restored session with a non-ISO but Date.parse-able expiresAt", async () => {
		// "December 31, 2099" is exactly the repro from issue #98: Date.parse
		// accepts it, but it is not a shape the backend's DateTimeOffset
		// serializer can ever emit.
		seedStoredSession({ expiresAt: "December 31, 2099" });
		render(
			<AuthProvider>
				<Probe />
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("signed out")).toBeInTheDocument());
	});

	it("accepts a restored session with a well-formed ISO-8601 expiresAt", async () => {
		seedStoredSession({});
		render(
			<AuthProvider>
				<Probe />
			</AuthProvider>,
		);
		await waitFor(() =>
			expect(screen.getByText("signed in as admin (Admin) token=tok-restored")).toBeInTheDocument(),
		);
	});
});

/**
 * Client-side expiry enforcement while the tab stays open (issue #97, found
 * in PR #93 review). Before this fix, `expiresAt` was only checked inside
 * `readStoredSession()` on mount — once signed in, nothing revisited it, so
 * an expired session kept rendering full chrome until some request happened
 * to 401.
 */
describe("AuthProvider expiry enforcement while mounted (issue #97)", () => {
	beforeEach(() => {
		__resetAuthConfigCacheForTests();
		window.sessionStorage.clear();
		vi.useFakeTimers();
	});

	afterEach(() => {
		vi.useRealTimers();
	});

	function seedStoredSession(expiresInMs: number): void {
		window.sessionStorage.setItem(
			STORAGE_KEY,
			JSON.stringify({
				token: "tok-live",
				username: "admin",
				role: "Admin",
				expiresAt: new Date(Date.now() + expiresInMs).toISOString(),
			}),
		);
	}

	it("drops to the login screen when the session expires while mounted, via its scheduled timer", async () => {
		seedStoredSession(5_000);

		render(
			<AuthProvider>
				<Probe />
			</AuthProvider>,
		);

		await vi.waitFor(() =>
			expect(screen.getByText("signed in as admin (Admin) token=tok-live")).toBeInTheDocument(),
		);

		// Advance past expiry — no user interaction, no failed request.
		await vi.advanceTimersByTimeAsync(5_001);

		expect(screen.getByText("signed out")).toBeInTheDocument();
		// The stale session is not left behind in storage either.
		expect(window.sessionStorage.getItem(STORAGE_KEY)).toBeNull();
	});

	it("re-checks expiry on visibilitychange/focus and drops to login if already past expiry", async () => {
		// A backgrounded tab's timers are throttled by the browser, so this
		// pins the second enforcement path independently of the setTimeout:
		// advance the clock without letting the fake timer queue run the
		// scheduled callback, then simulate the tab regaining focus.
		seedStoredSession(5_000);

		render(
			<AuthProvider>
				<Probe />
			</AuthProvider>,
		);

		await vi.waitFor(() =>
			expect(screen.getByText("signed in as admin (Admin) token=tok-live")).toBeInTheDocument(),
		);

		// Move the clock past expiry without advancing fake timers (simulates a
		// throttled/backgrounded tab where the setTimeout hasn't fired yet).
		vi.setSystemTime(Date.now() + 10_000);
		Object.defineProperty(document, "visibilityState", { value: "visible", configurable: true });
		document.dispatchEvent(new Event("visibilitychange"));

		await vi.waitFor(() => expect(screen.getByText("signed out")).toBeInTheDocument());
	});

	it("does not leak the expiry timer or visibility/focus listeners on unmount", async () => {
		seedStoredSession(5_000);

		const addSpy = vi.spyOn(document, "addEventListener");
		const removeSpy = vi.spyOn(document, "removeEventListener");
		const winAddSpy = vi.spyOn(window, "addEventListener");
		const winRemoveSpy = vi.spyOn(window, "removeEventListener");

		const { unmount } = render(
			<AuthProvider>
				<Probe />
			</AuthProvider>,
		);

		await vi.waitFor(() =>
			expect(screen.getByText("signed in as admin (Admin) token=tok-live")).toBeInTheDocument(),
		);

		const visibilityAdds = addSpy.mock.calls.filter((call) => call[0] === "visibilitychange").length;
		const focusAdds = winAddSpy.mock.calls.filter((call) => call[0] === "focus").length;
		expect(visibilityAdds).toBeGreaterThan(0);
		expect(focusAdds).toBeGreaterThan(0);

		unmount();

		const visibilityRemoves = removeSpy.mock.calls.filter((call) => call[0] === "visibilitychange").length;
		const focusRemoves = winRemoveSpy.mock.calls.filter((call) => call[0] === "focus").length;
		expect(visibilityRemoves).toBe(visibilityAdds);
		expect(focusRemoves).toBe(focusAdds);

		// And the now-orphaned timer, if it were still live, would throw or act
		// on unmounted state; advancing well past expiry after unmount must be
		// silent.
		await vi.advanceTimersByTimeAsync(10_000);

		addSpy.mockRestore();
		removeSpy.mockRestore();
		winAddSpy.mockRestore();
		winRemoveSpy.mockRestore();
	});
});

/**
 * OIDC redirect flow (issue #534): the callback landing that mints a session
 * from the access token's own claims, the end-session redirect on logout of an
 * OIDC session, and the two authorize-redirect entry points. These paths were
 * the coverage gap flagged in PR #537 round 2 — `auth.tsx`'s OIDC branches and
 * `jwt.ts`'s `decodeJwtPayload`.
 */

/** Fixed placeholder OIDC config, discovery, and endpoints — all invented,
 * same-origin/example values (CLAUDE.md sanitization). */
const OIDC_CONFIG_WIRE = {
	local_auth_enabled: false,
	oidc_authority: "/auth/realms/waypoint",
	oidc_client_id: "waypoint-frontend",
};
const DISCOVERY_URL = "/auth/realms/waypoint/.well-known/openid-configuration";
const TOKEN_ENDPOINT = "https://oidc.example.internal/token";
const AUTHORIZE_ENDPOINT = "https://oidc.example.internal/authorize";
const END_SESSION_ENDPOINT = "https://oidc.example.internal/logout";

const DISCOVERY_DOC = {
	authorization_endpoint: AUTHORIZE_ENDPOINT,
	token_endpoint: TOKEN_ENDPOINT,
	end_session_endpoint: END_SESSION_ENDPOINT,
};

/** base64url-encode a JSON object into a JWT payload segment (mirror of jwt.ts's decode). */
function encodeJwtSegment(payload: unknown): string {
	const json = JSON.stringify(payload);
	const utf8 = encodeURIComponent(json).replace(/%([0-9A-F]{2})/g, (_m, hex: string) =>
		String.fromCharCode(parseInt(hex, 16)),
	);
	return btoa(utf8).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

/** Assemble a three-segment invented access token carrying the given claims. */
function fakeAccessToken(claims: Record<string, unknown>): string {
	return `eyJhbGciOiJub25lIn0.${encodeJwtSegment(claims)}.sig`;
}

/** Point jsdom's location at a path (+ optional query) without a real navigation. */
function setLocation(path: string): void {
	window.history.replaceState(null, "", path);
}

describe("AuthProvider OIDC callback (issue #534 — mint a session from token claims)", () => {
	let originalFetch: typeof fetch;

	beforeEach(() => {
		__resetAuthConfigCacheForTests();
		originalFetch = globalThis.fetch;
		window.sessionStorage.clear();
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
		setLocation("/");
	});

	/** Mocks config + discovery + token exchange for the callback landing. The
	 * token body is returned verbatim so a test can supply a malformed token. */
	function mockCallbackFetch(tokenBody: unknown): void {
		globalThis.fetch = vi.fn(async (url: string) => {
			if (url === "/api/v1/auth/config") {
				return new Response(JSON.stringify(OIDC_CONFIG_WIRE), {
					status: 200,
					headers: { "Content-Type": "application/json" },
				});
			}
			if (url === DISCOVERY_URL) {
				return new Response(JSON.stringify(DISCOVERY_DOC), {
					status: 200,
					headers: { "Content-Type": "application/json" },
				});
			}
			if (url === TOKEN_ENDPOINT) {
				return new Response(JSON.stringify(tokenBody), {
					status: 200,
					headers: { "Content-Type": "application/json" },
				});
			}
			throw new Error(`unexpected fetch: ${url}`);
		}) as unknown as typeof fetch;
	}

	/** Seed the PKCE verifier + state that startLogin would have stashed before
	 * the redirect, plus the code/state query params on the callback URL, so
	 * completeLogin's CSRF check passes. */
	function landOnCallback(returnTo = "/dashboard"): void {
		window.sessionStorage.setItem("waypoint.oidc.pkce_verifier", "verifier-fixture");
		window.sessionStorage.setItem("waypoint.oidc.state", "state-fixture");
		window.sessionStorage.setItem("waypoint.oidc.return_to", returnTo);
		setLocation("/oidc/callback?code=auth-code-fixture&state=state-fixture");
	}

	it("mints a signed-in session from the access token's claims and returns to the stashed path", async () => {
		mockCallbackFetch({
			access_token: fakeAccessToken({ role: "Operator", preferred_username: "opuser", sub: "s-1", exp: 1893456000 }),
			expires_in: 3600,
			token_type: "Bearer",
		});
		landOnCallback("/dashboard");

		render(
			<AuthProvider>
				<Probe />
			</AuthProvider>,
		);

		await waitFor(() =>
			expect(screen.getByText(/signed in as opuser \(Operator\)/)).toBeInTheDocument(),
		);
		// Persisted as an OIDC session (drives the end-session logout path).
		expect(window.sessionStorage.getItem(STORAGE_KEY)).toContain('"kind":"oidc"');
		// The code/state query params are stripped and the app is returned to the
		// path startLogin was called from.
		expect(window.location.pathname).toBe("/dashboard");
		expect(window.location.search).toBe("");
	});

	it("falls back to `sub` for the username when the token has no preferred_username", async () => {
		mockCallbackFetch({
			access_token: fakeAccessToken({ role: "Admin", sub: "subject-42", exp: 1893456000 }),
			expires_in: 3600,
			token_type: "Bearer",
		});
		landOnCallback();

		render(
			<AuthProvider>
				<Probe />
			</AuthProvider>,
		);

		await waitFor(() => expect(screen.getByText(/signed in as subject-42 \(Admin\)/)).toBeInTheDocument());
	});

	it("refuses the callback when the access token carries a role outside the closed set", async () => {
		mockCallbackFetch({
			access_token: fakeAccessToken({ role: "root", preferred_username: "opuser", exp: 1893456000 }),
			expires_in: 3600,
			token_type: "Bearer",
		});
		landOnCallback();

		function ErrorProbe() {
			const { error, status } = useAuth();
			return <div data-testid="err">{status === "restoring" ? "restoring" : (error ?? "none")}</div>;
		}

		render(
			<AuthProvider>
				<ErrorProbe />
			</AuthProvider>,
		);

		await waitFor(() => expect(screen.getByTestId("err")).not.toHaveTextContent("none"));
		expect(screen.getByTestId("err").textContent).toContain("Keycloak access token");
		expect(window.sessionStorage.getItem(STORAGE_KEY)).toBeNull();
	});

	it("surfaces an error when the issued access token cannot be decoded into claims", async () => {
		mockCallbackFetch({
			access_token: "not-a-jwt", // one segment — decodeJwtPayload returns null
			expires_in: 3600,
			token_type: "Bearer",
		});
		landOnCallback();

		function ErrorProbe() {
			const { error } = useAuth();
			return <div data-testid="err">{error ?? "none"}</div>;
		}

		render(
			<AuthProvider>
				<ErrorProbe />
			</AuthProvider>,
		);

		await waitFor(() => expect(screen.getByTestId("err")).not.toHaveTextContent("none"));
		expect(screen.getByTestId("err").textContent).toContain("claims");
		expect(window.sessionStorage.getItem(STORAGE_KEY)).toBeNull();
	});

	it("surfaces the OIDC state-mismatch error and does not sign in", async () => {
		mockCallbackFetch({
			access_token: fakeAccessToken({ role: "Admin", preferred_username: "a", exp: 1893456000 }),
			expires_in: 3600,
			token_type: "Bearer",
		});
		// Stash a state that does not match the callback URL's state param.
		window.sessionStorage.setItem("waypoint.oidc.pkce_verifier", "verifier-fixture");
		window.sessionStorage.setItem("waypoint.oidc.state", "the-expected-state");
		setLocation("/oidc/callback?code=auth-code-fixture&state=a-different-state");

		function ErrorProbe() {
			const { error } = useAuth();
			return <div data-testid="err">{error ?? "none"}</div>;
		}

		render(
			<AuthProvider>
				<ErrorProbe />
			</AuthProvider>,
		);

		await waitFor(() => expect(screen.getByTestId("err")).not.toHaveTextContent("none"));
		expect(screen.getByTestId("err").textContent).toContain("state mismatch");
		expect(window.sessionStorage.getItem(STORAGE_KEY)).toBeNull();
	});
});

describe("AuthProvider OIDC logout + authorize redirects (issue #534)", () => {
	let originalFetch: typeof fetch;
	let originalLocation: Location;
	let assignMock: ReturnType<typeof vi.fn>;

	beforeEach(() => {
		__resetAuthConfigCacheForTests();
		originalFetch = globalThis.fetch;
		window.sessionStorage.clear();
		// jsdom's window.location.assign is a non-configurable native stub that
		// throws "Not implemented", and the property itself can't be redefined.
		// Replace the whole `location` object with a stand-in that carries a
		// spy `assign` plus the fields these paths read (origin/pathname/…), so
		// the redirect targets can be asserted.
		originalLocation = window.location;
		assignMock = vi.fn();
		const stub = {
			origin: originalLocation.origin,
			href: originalLocation.href,
			pathname: originalLocation.pathname,
			search: originalLocation.search,
			assign: assignMock,
			replace: vi.fn(),
			reload: vi.fn(),
		};
		Object.defineProperty(window, "location", {
			configurable: true,
			writable: true,
			value: stub as unknown as Location,
		});
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
		Object.defineProperty(window, "location", {
			configurable: true,
			writable: true,
			value: originalLocation,
		});
		setLocation("/");
	});

	function mockConfigAndDiscovery(): void {
		globalThis.fetch = vi.fn(async (url: string) => {
			if (url === "/api/v1/auth/config") {
				return new Response(JSON.stringify(OIDC_CONFIG_WIRE), {
					status: 200,
					headers: { "Content-Type": "application/json" },
				});
			}
			if (url === DISCOVERY_URL) {
				return new Response(JSON.stringify(DISCOVERY_DOC), {
					status: 200,
					headers: { "Content-Type": "application/json" },
				});
			}
			throw new Error(`unexpected fetch: ${url}`);
		}) as unknown as typeof fetch;
	}

	/** Seeds a restored OIDC-kind session so logout() takes the end-session path. */
	function seedOidcSession(): void {
		window.sessionStorage.setItem(
			STORAGE_KEY,
			JSON.stringify({
				token: "tok-oidc",
				username: "opuser",
				role: "Operator",
				expiresAt: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
				kind: "oidc",
			}),
		);
	}

	function LogoutTrigger() {
		const { logout } = useAuth();
		return (
			<button type="button" onClick={() => logout()}>
				logout
			</button>
		);
	}

	it("redirects to Keycloak's end-session endpoint when an OIDC session logs out", async () => {
		mockConfigAndDiscovery();
		seedOidcSession();

		render(
			<AuthProvider>
				<LogoutTrigger />
				<Probe />
			</AuthProvider>,
		);

		await waitFor(() => expect(screen.getByText(/signed in as opuser/)).toBeInTheDocument());
		screen.getByText("logout").click();

		// Local session is dropped immediately (chrome returns to signed-out)...
		await waitFor(() => expect(screen.getByText("signed out")).toBeInTheDocument());
		// ...and the browser is sent to the RP-initiated logout URL.
		await waitFor(() => expect(assignMock).toHaveBeenCalledTimes(1));
		expect(String(assignMock.mock.calls[0][0])).toContain(END_SESSION_ENDPOINT);
		expect(window.sessionStorage.getItem(STORAGE_KEY)).toBeNull();
	});

	it("does not redirect on logout of a local session (no IdP session to end)", async () => {
		mockConfigAndDiscovery();
		window.sessionStorage.setItem(
			STORAGE_KEY,
			JSON.stringify({
				token: "tok-local",
				username: "admin",
				role: "Admin",
				expiresAt: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
				kind: "local",
			}),
		);

		render(
			<AuthProvider>
				<LogoutTrigger />
				<Probe />
			</AuthProvider>,
		);

		await waitFor(() => expect(screen.getByText(/signed in as admin/)).toBeInTheDocument());
		screen.getByText("logout").click();

		await waitFor(() => expect(screen.getByText("signed out")).toBeInTheDocument());
		expect(assignMock).not.toHaveBeenCalled();
	});

	it("startOidcLogin redirects the browser to the authorize endpoint and stashes PKCE state", async () => {
		mockConfigAndDiscovery();

		function StartTrigger() {
			const { startOidcLogin } = useAuth();
			return (
				<button type="button" onClick={() => void startOidcLogin()}>
					start
				</button>
			);
		}

		render(
			<AuthProvider>
				<StartTrigger />
				<Probe />
			</AuthProvider>,
		);

		await waitFor(() => expect(screen.getByText("signed out")).toBeInTheDocument());
		screen.getByText("start").click();

		await waitFor(() => expect(assignMock).toHaveBeenCalledTimes(1));
		const target = String(assignMock.mock.calls[0][0]);
		expect(target).toContain(AUTHORIZE_ENDPOINT);
		expect(target).toContain("code_challenge_method=S256");
		expect(target).not.toContain("prompt=login");
		// PKCE verifier + state were stashed for the eventual callback.
		expect(window.sessionStorage.getItem("waypoint.oidc.pkce_verifier")).not.toBeNull();
		expect(window.sessionStorage.getItem("waypoint.oidc.state")).not.toBeNull();
	});

	it("stepUpOidcLogin adds prompt=login to force a fresh Keycloak credential prompt", async () => {
		mockConfigAndDiscovery();

		function StepUpTrigger() {
			const { stepUpOidcLogin } = useAuth();
			return (
				<button type="button" onClick={() => void stepUpOidcLogin("/credentials")}>
					stepup
				</button>
			);
		}

		render(
			<AuthProvider>
				<StepUpTrigger />
				<Probe />
			</AuthProvider>,
		);

		await waitFor(() => expect(screen.getByText("signed out")).toBeInTheDocument());
		screen.getByText("stepup").click();

		await waitFor(() => expect(assignMock).toHaveBeenCalledTimes(1));
		const target = String(assignMock.mock.calls[0][0]);
		expect(target).toContain(AUTHORIZE_ENDPOINT);
		expect(target).toContain("prompt=login");
		// The requested return-to path is stashed for the callback to restore.
		expect(window.sessionStorage.getItem("waypoint.oidc.return_to")).toBe("/credentials");
	});
});
