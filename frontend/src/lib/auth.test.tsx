import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider, useAuth } from "./auth";

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
		expect(calls).toEqual(["/api/v1/auth/login"]);
	});
});
