import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider, useAuth } from "../../lib/auth";
import { LoginScreen } from "./LoginScreen";

function Probe() {
	const { user, status } = useAuth();
	if (status === "restoring") {
		return <div>restoring</div>;
	}
	return <div>{user ? `signed in as ${user.username} (${user.role})` : "signed out"}</div>;
}

describe("LoginScreen", () => {
	let originalFetch: typeof fetch;

	beforeEach(() => {
		originalFetch = globalThis.fetch;
		window.sessionStorage.clear();
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
	});

	it("posts to /api/v1/auth/login, fetches /auth/me, and signs in on success", async () => {
		// Pinned to the real backend's wire shape (backend/Waypoint.Api/Contracts/AuthContracts.cs):
		// login returns {token, role, expires_at} with NO `user` object, and identity comes
		// from a separate GET /auth/me call. A client still expecting {token, user} would read
		// `user` as undefined here and never reach "signed in".
		const futureExpiry = new Date(Date.now() + 60 * 60 * 1000).toISOString();
		const calls: string[] = [];
		globalThis.fetch = vi.fn(async (url: string, init?: RequestInit) => {
			calls.push(url);
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

		fireEvent.change(screen.getByLabelText("Username"), { target: { value: "admin" } });
		fireEvent.change(screen.getByLabelText("Password"), { target: { value: "waypoint-dev" } });
		fireEvent.click(screen.getByRole("button", { name: /sign in/i }));

		await waitFor(() => expect(screen.getByText(/signed in as admin \(Admin\)/)).toBeInTheDocument());
		expect(calls).toEqual(["/api/v1/auth/login", "/api/v1/auth/me"]);

		// The full chrome-rendering bug (issue #64) hinged on `user` staying null
		// because JSON.stringify drops an `undefined` key — assert the persisted
		// session actually round-trips a well-formed, non-empty object.
		const stored = JSON.parse(window.sessionStorage.getItem("waypoint.session") as string);
		expect(stored).toEqual({ token: "tok-1", username: "admin", role: "Admin", expiresAt: futureExpiry });
	});

	it("shows the server's error message on invalid credentials", async () => {
		globalThis.fetch = vi.fn(async () => {
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

		fireEvent.change(screen.getByLabelText("Username"), { target: { value: "admin" } });
		fireEvent.change(screen.getByLabelText("Password"), { target: { value: "wrong" } });
		fireEvent.click(screen.getByRole("button", { name: /sign in/i }));

		await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("Invalid username or password."));
	});
});
