import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import App from "./App";

function jsonResponse(body: unknown, status = 200) {
	return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

/** Routes the handful of endpoints the chrome touches on a signed-in mount:
 * login, /system, /stigman, and an /events connection that just stays open
 * (this test isn't exercising the drawer's live-stream behavior — that's
 * events.test.ts / JobLogDrawer.test.ts). */
function installChromeFetchMock(role: "Viewer" | "Admin" = "Admin") {
	globalThis.fetch = vi.fn(async (url: string) => {
		if (url === "/api/v1/auth/login") {
			return jsonResponse({ token: "tok-1", user: { username: "j.moreno", role } });
		}
		if (url === "/api/v1/system") {
			return jsonResponse({ version: "0.1.0-dev", build: "local", mode: "connected", update_available: null });
		}
		if (url === "/api/v1/stigman") {
			return jsonResponse({ connected: true, endpoint: "stigman.example.internal", collection: "17" });
		}
		if (url.startsWith("/api/v1/events")) {
			// Stay open indefinitely (a real SSE connection); the test never awaits it.
			return new Promise(() => {});
		}
		return jsonResponse({ error: { code: "not_found", message: "unhandled in test" } }, 404);
	}) as unknown as typeof fetch;
}

async function signIn() {
	fireEvent.change(screen.getByLabelText("Username"), { target: { value: "admin" } });
	fireEvent.change(screen.getByLabelText("Password"), { target: { value: "waypoint-dev" } });
	fireEvent.click(screen.getByRole("button", { name: /sign in/i }));
}

describe("App", () => {
	let originalFetch: typeof fetch;

	beforeEach(() => {
		originalFetch = globalThis.fetch;
		window.sessionStorage.clear();
		window.localStorage.clear();
		window.history.pushState(null, "", "/");
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
	});

	it("shows the login screen when signed out, then the chrome after signing in", async () => {
		installChromeFetchMock("Admin");
		render(<App />);

		expect(screen.getByLabelText("Username")).toBeInTheDocument();

		await signIn();

		// Top bar: wordmark, qualifier, and the active screen's title.
		await waitFor(() => expect(screen.getByText("WAYPOINT")).toBeInTheDocument());
		expect(screen.getByText("DoD VCF Toolkit")).toBeInTheDocument();
		expect(screen.getAllByText("Dashboard").length).toBeGreaterThan(0);

		// Left rail: nav groups are present with their items.
		expect(screen.getByText("COMPLIANCE")).toBeInTheDocument();
		expect(screen.getByText("Live Run")).toBeInTheDocument();
		expect(screen.getByText("Configuration")).toBeInTheDocument();

		// Job log drawer bar, closed by default.
		expect(screen.getByText("JOB LOG")).toBeInTheDocument();
	});

	it("redirects a Viewer away from an Admin-only screen instead of rendering it (screen-level guard)", async () => {
		installChromeFetchMock("Viewer");
		render(<App />);
		await signIn();

		await waitFor(() => expect(screen.getByText("WAYPOINT")).toBeInTheDocument());

		// A Viewer navigating straight to /config (e.g. a stale deep link)
		// must land on Dashboard, not Configuration — README "Roles &
		// Permissions": "changing role while inside a screen the new role
		// cannot access redirects to Dashboard. Do not rely on gating the nav
		// entry point alone."
		window.history.pushState(null, "", "/config");
		window.dispatchEvent(new PopStateEvent("popstate"));

		await waitFor(() => expect(window.location.pathname).toBe("/"));
	});

	it("hides the Download Catalog nav item entirely in air-gapped mode (mode-gating, not role-gating)", async () => {
		globalThis.fetch = vi.fn(async (url: string) => {
			if (url === "/api/v1/auth/login") {
				return jsonResponse({ token: "tok-1", user: { username: "j.moreno", role: "Admin" } });
			}
			if (url === "/api/v1/system") {
				return jsonResponse({ version: "0.1.0-dev", build: "local", mode: "disconnected", update_available: null });
			}
			if (url === "/api/v1/stigman") {
				return jsonResponse({ connected: false, endpoint: null, collection: null });
			}
			if (url.startsWith("/api/v1/events")) {
				return new Promise(() => {});
			}
			return jsonResponse({ error: { code: "not_found", message: "unhandled" } }, 404);
		}) as unknown as typeof fetch;

		render(<App />);
		await signIn();

		await waitFor(() => expect(screen.getByText("WAYPOINT")).toBeInTheDocument());
		await waitFor(() => expect(screen.queryByText("Download Catalog")).not.toBeInTheDocument());
	});
});
