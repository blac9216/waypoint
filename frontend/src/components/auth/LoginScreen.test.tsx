import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider, __resetAuthConfigCacheForTests } from "../../lib/auth";
import { useAuth } from "../../lib/auth-context";
import { LoginScreen } from "./LoginScreen";

function Probe() {
	const { user, status } = useAuth();
	if (status === "restoring") {
		return <div>restoring</div>;
	}
	return <div>{user ? `signed in as ${user.username} (${user.role})` : "signed out"}</div>;
}

/** `GET /auth/config` (issue #534) — every test needs this answered before `LoginScreen` decides whether to render the local-auth form at all. */
function mockAuthConfig(localAuthEnabled: boolean) {
	return { local_auth_enabled: localAuthEnabled, oidc_authority: "/auth/realms/waypoint", oidc_client_id: "waypoint-frontend" };
}

describe("LoginScreen", () => {
	let originalFetch: typeof fetch;

	beforeEach(() => {
		originalFetch = globalThis.fetch;
		window.sessionStorage.clear();
		// `auth.tsx` caches GET /auth/config module-level (one fetch per page
		// load in the real app) — without this reset, every test after the
		// first would silently reuse the first test's mocked answer.
		__resetAuthConfigCacheForTests();
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
	});

	it("always offers the Keycloak sign-in button", async () => {
		globalThis.fetch = vi.fn(async (url: string) => {
			if (url === "/api/v1/auth/config") {
				return new Response(JSON.stringify(mockAuthConfig(false)), {
					status: 200,
					headers: { "Content-Type": "application/json" },
				});
			}
			throw new Error(`unexpected fetch: ${url}`);
		}) as unknown as typeof fetch;

		render(
			<AuthProvider>
				<LoginScreen />
			</AuthProvider>,
		);

		expect(await screen.findByRole("button", { name: "Sign in with Keycloak" })).toBeInTheDocument();
	});

	it("withholds the local-auth form until GET /auth/config resolves, then hides it when the dev flag is off", async () => {
		let resolveConfig: (() => void) | undefined;
		const configGate = new Promise<void>((resolve) => {
			resolveConfig = resolve;
		});
		globalThis.fetch = vi.fn(async (url: string) => {
			if (url === "/api/v1/auth/config") {
				await configGate;
				return new Response(JSON.stringify(mockAuthConfig(false)), {
					status: 200,
					headers: { "Content-Type": "application/json" },
				});
			}
			throw new Error(`unexpected fetch: ${url}`);
		}) as unknown as typeof fetch;

		render(
			<AuthProvider>
				<LoginScreen />
			</AuthProvider>,
		);

		// Config still loading: no local form, but the Keycloak button is
		// already there (it doesn't depend on the dev flag at all).
		expect(screen.getByRole("button", { name: "Sign in with Keycloak" })).toBeInTheDocument();
		expect(screen.queryByLabelText("Username")).not.toBeInTheDocument();

		resolveConfig?.();
		await waitFor(() => expect(screen.queryByLabelText("Username")).not.toBeInTheDocument());
	});

	it("shows the local-auth form once GET /auth/config reports LocalAuth:Enabled", async () => {
		globalThis.fetch = vi.fn(async (url: string) => {
			if (url === "/api/v1/auth/config") {
				return new Response(JSON.stringify(mockAuthConfig(true)), {
					status: 200,
					headers: { "Content-Type": "application/json" },
				});
			}
			throw new Error(`unexpected fetch: ${url}`);
		}) as unknown as typeof fetch;

		render(
			<AuthProvider>
				<LoginScreen />
			</AuthProvider>,
		);

		expect(await screen.findByLabelText("Username")).toBeInTheDocument();
		expect(screen.getByLabelText("Password")).toBeInTheDocument();
	});

	it("posts to /api/v1/auth/login, fetches /auth/me, and signs in on success (dev-flag local auth)", async () => {
		// Pinned to the real backend's wire shape (backend/Waypoint.Api/Contracts/AuthContracts.cs):
		// login returns {token, role, expires_at} with NO `user` object, and identity comes
		// from a separate GET /auth/me call. A client still expecting {token, user} would read
		// `user` as undefined here and never reach "signed in".
		const futureExpiry = new Date(Date.now() + 60 * 60 * 1000).toISOString();
		const calls: string[] = [];
		globalThis.fetch = vi.fn(async (url: string, init?: RequestInit) => {
			calls.push(url);
			if (url === "/api/v1/auth/config") {
				return new Response(JSON.stringify(mockAuthConfig(true)), {
					status: 200,
					headers: { "Content-Type": "application/json" },
				});
			}
			if (url === "/api/v1/auth/login") {
				expect(JSON.parse(init?.body as string)).toEqual({ username: "admin", password: "waypoint-dev" });
				return new Response(JSON.stringify({ token: "tok-1", role: "Admin", expires_at: futureExpiry }), {
					status: 200,
					headers: { "Content-Type": "application/json" },
				});
			}
			if (url === "/api/v1/auth/me") {
				expect((init?.headers as Headers | undefined)?.get("Authorization")).toBe("Bearer tok-1");
				return new Response(JSON.stringify({ username: "admin", role: "Admin" }), {
					status: 200,
					headers: { "Content-Type": "application/json" },
				});
			}
			throw new Error(`unexpected fetch: ${url}`);
		}) as unknown as typeof fetch;

		render(
			<AuthProvider>
				<LoginScreen />
				<Probe />
			</AuthProvider>,
		);

		fireEvent.change(await screen.findByLabelText("Username"), { target: { value: "admin" } });
		fireEvent.change(screen.getByLabelText("Password"), { target: { value: "waypoint-dev" } });
		fireEvent.click(screen.getByRole("button", { name: /sign in \(local\)/i }));

		await waitFor(() => expect(screen.getByText(/signed in as admin \(Admin\)/)).toBeInTheDocument());
		expect(calls).toEqual(["/api/v1/auth/config", "/api/v1/auth/login", "/api/v1/auth/me"]);

		// The full chrome-rendering bug (issue #64) hinged on `user` staying null
		// because JSON.stringify drops an `undefined` key — assert the persisted
		// session actually round-trips a well-formed, non-empty object.
		const stored = JSON.parse(window.sessionStorage.getItem("waypoint.session") as string);
		expect(stored).toEqual({ token: "tok-1", username: "admin", role: "Admin", expiresAt: futureExpiry, kind: "local" });
	});

	it("shows the server's error message on invalid credentials", async () => {
		globalThis.fetch = vi.fn(async (url: string) => {
			if (url === "/api/v1/auth/config") {
				return new Response(JSON.stringify(mockAuthConfig(true)), {
					status: 200,
					headers: { "Content-Type": "application/json" },
				});
			}
			return new Response(
				JSON.stringify({ error: { code: "invalid_credentials", message: "Invalid username or password." } }),
				{ status: 401, headers: { "Content-Type": "application/json" } },
			);
		}) as unknown as typeof fetch;

		render(
			<AuthProvider>
				<LoginScreen />
			</AuthProvider>,
		);

		fireEvent.change(await screen.findByLabelText("Username"), { target: { value: "admin" } });
		fireEvent.change(screen.getByLabelText("Password"), { target: { value: "wrong" } });
		fireEvent.click(screen.getByRole("button", { name: /sign in \(local\)/i }));

		await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("Invalid username or password."));
	});

	it("surfaces an error inline (not a thrown rejection) when the Keycloak redirect itself cannot be started", async () => {
		globalThis.fetch = vi.fn(async (url: string) => {
			if (url === "/api/v1/auth/config") {
				return new Response(JSON.stringify(mockAuthConfig(false)), {
					status: 200,
					headers: { "Content-Type": "application/json" },
				});
			}
			// Discovery fetch — same-origin `/auth/realms/waypoint/.well-known/openid-configuration`.
			return new Response("Service Unavailable", { status: 503, statusText: "Service Unavailable" });
		}) as unknown as typeof fetch;

		render(
			<AuthProvider>
				<LoginScreen />
			</AuthProvider>,
		);

		fireEvent.click(await screen.findByRole("button", { name: "Sign in with Keycloak" }));

		await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent(/OIDC discovery failed/));
	});
});
