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

	it("posts to /api/v1/auth/login and signs in on success", async () => {
		globalThis.fetch = vi.fn(async (url: string, init?: RequestInit) => {
			expect(url).toBe("/api/v1/auth/login");
			expect(JSON.parse(init?.body as string)).toEqual({ username: "admin", password: "waypoint-dev" });
			return new Response(JSON.stringify({ token: "tok-1", user: { username: "admin", role: "Admin" } }), {
				status: 200,
				headers: { "Content-Type": "application/json" },
			});
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
