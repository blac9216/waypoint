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
