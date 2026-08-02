import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import App from "./App";
import { ConfigurationScreen } from "./screens/screens";

/**
 * Mount-detecting spy for issue #78: wraps the real `ConfigurationScreen`
 * (preserving its rendered output, so tests that DO expect it to render —
 * none currently do, but future ones might — still see the real markup)
 * while recording every invocation. A React function component's body runs
 * exactly when React mounts it into the tree, so "the spy was never called"
 * is equivalent to "the component was never mounted" — a stronger claim
 * than "the DOM doesn't contain its text", because it holds even before any
 * of the component's own effects (e.g. a future on-mount fetch) have had a
 * chance to run.
 */
vi.mock("./screens/screens", async (importOriginal) => {
	const actual = await importOriginal<typeof import("./screens/screens")>();
	return { ...actual, ConfigurationScreen: vi.fn(actual.ConfigurationScreen) };
});

function jsonResponse(body: unknown, status = 200) {
	return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

/** Routes the handful of endpoints the chrome touches on a signed-in mount:
 * login, /auth/me, /system, /stigman, and an /events connection that just
 * stays open (this test isn't exercising the drawer's live-stream behavior
 * — that's events.test.ts / JobLogDrawer.test.ts). Login/me shapes match
 * the real backend contract (issue #64 — no `user` object on the login
 * response; identity comes from a separate /auth/me call). */
function installChromeFetchMock(role: "Viewer" | "Admin" = "Admin") {
	globalThis.fetch = vi.fn(async (url: string) => {
		if (url === "/api/v1/auth/login") {
			return jsonResponse({ token: "tok-1", role, expires_at: new Date(Date.now() + 60 * 60 * 1000).toISOString() });
		}
		if (url === "/api/v1/auth/me") {
			return jsonResponse({ username: "j.moreno", role });
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
		vi.mocked(ConfigurationScreen).mockClear();
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

	it("never mounts ConfigurationScreen for a Viewer navigating to /config (issue #78: not just the final URL)", async () => {
		// This is the regression case for issue #78: with the old
		// useEffect-based guard, `<ConfigurationScreen />` was created and
		// mounted in the SAME commit as the disallowed navigation, and only
		// unmounted on the NEXT commit once the redirect effect fired. A test
		// that merely asserts `window.location.pathname === "/"` afterwards
		// (the previous test) cannot see that — it would pass identically
		// whether or not the disallowed screen mounted first. The mount spy
		// can.
		installChromeFetchMock("Viewer");
		render(<App />);
		await signIn();

		await waitFor(() => expect(screen.getByText("WAYPOINT")).toBeInTheDocument());

		window.history.pushState(null, "", "/config");
		window.dispatchEvent(new PopStateEvent("popstate"));

		await waitFor(() => expect(window.location.pathname).toBe("/"));
		await waitFor(() => expect(screen.getAllByText("Dashboard").length).toBeGreaterThan(0));

		expect(ConfigurationScreen).not.toHaveBeenCalled();
	});

	it("never mounts ConfigurationScreen for a Viewer who signs in already deep-linked to /config", async () => {
		// The more direct reproduction of the "mounts for a frame" bug: the
		// disallowed route is current from the very first render after
		// sign-in, not reached by an in-app navigation. If the guard only
		// unwinds a screen that already mounted, this is exactly the case
		// where a real on-mount fetch (the first data-bearing screen, #12)
		// would already have fired before any redirect could run.
		window.history.pushState(null, "", "/config");
		installChromeFetchMock("Viewer");
		render(<App />);
		await signIn();

		await waitFor(() => expect(screen.getByText("WAYPOINT")).toBeInTheDocument());
		await waitFor(() => expect(window.location.pathname).toBe("/"));
		expect(screen.getAllByText("Dashboard").length).toBeGreaterThan(0);

		expect(ConfigurationScreen).not.toHaveBeenCalled();
	});

	it("does mount ConfigurationScreen for an Admin navigating to /config (guard isn't just always false)", async () => {
		// The counterpart to the two "never mounts" tests above: proves the
		// mock is a faithful spy on the real component (it renders the real
		// output) and that the guard actually discriminates by role rather
		// than accidentally blocking every screen.
		installChromeFetchMock("Admin");
		render(<App />);
		await signIn();

		await waitFor(() => expect(screen.getByText("WAYPOINT")).toBeInTheDocument());

		window.history.pushState(null, "", "/config");
		window.dispatchEvent(new PopStateEvent("popstate"));

		await waitFor(() => expect(ConfigurationScreen).toHaveBeenCalled());
		expect(window.location.pathname).toBe("/config");
		expect(screen.getAllByText("Configuration").length).toBeGreaterThan(0);
	});

	it("hides the Download Catalog nav item entirely in air-gapped mode (mode-gating, not role-gating)", async () => {
		globalThis.fetch = vi.fn(async (url: string) => {
			if (url === "/api/v1/auth/login") {
				return jsonResponse({
					token: "tok-1",
					role: "Admin",
					expires_at: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
				});
			}
			if (url === "/api/v1/auth/me") {
				return jsonResponse({ username: "j.moreno", role: "Admin" });
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
