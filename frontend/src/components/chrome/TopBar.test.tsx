import { render, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { SystemProvider } from "../../lib/system";
import { ThemeProvider } from "../../lib/theme";
import { TopBar } from "./TopBar";

/**
 * Issue #316: `GET /stigman`'s merged response (`StigManagerConnectionResponse`,
 * PR #314) has no `connected` field — it is stored configuration, not a
 * reachability signal. These tests pin the pill to what the backend actually
 * sends: `stigman !== null` (a connection is configured) vs. the 404 the
 * controller throws when none is (`StigManagerController.GetGlobal`).
 */

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(body === undefined ? null : JSON.stringify(body), {
		status,
		headers: { "Content-Type": "application/json" },
	});
}

describe("TopBar STIG Manager pill (issue #316)", () => {
	let originalFetch: typeof fetch;

	function installFetchMock(stigmanResponse: () => Response) {
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
			const url = typeof input === "string" ? input : input.toString();
			if (url === "/api/v1/system") {
				return jsonResponse({ version: "2.4.1", build: "24817", mode: "connected", update_available: null });
			}
			if (url === "/api/v1/stigman") {
				return stigmanResponse();
			}
			throw new Error(`unexpected fetch: ${url}`);
		}) as unknown as typeof fetch;

		window.sessionStorage.setItem(
			"waypoint.session",
			JSON.stringify({
				token: "tok-1",
				username: "j.moreno",
				role: "Admin",
				expiresAt: new Date(Date.now() + 60_000).toISOString(),
			}),
		);
	}

	beforeEach(() => {
		originalFetch = globalThis.fetch;
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
		window.sessionStorage.clear();
	});

	function mount() {
		render(
			<AuthProvider>
				<SystemProvider>
					<ThemeProvider>
						<TopBar screenTitle="Dashboard" />
					</ThemeProvider>
				</SystemProvider>
			</AuthProvider>,
		);
	}

	it("shows configured (is-ok dot) when GET /stigman returns a connection, without reading a nonexistent `connected` field", async () => {
		installFetchMock(() =>
			jsonResponse({
				endpoint: "https://stigman.example.internal",
				authority: "https://keycloak.example.internal/realms/waypoint",
				collection: "17",
				client_id: "waypoint-appliance",
				scope: "openid stig-manager:stig:read",
				credential_id: "cred-token-1",
				created_at: "2026-01-01T00:00:00Z",
				updated_at: "2026-07-01T00:00:00Z",
			}),
		);
		mount();

		await waitFor(() => {
			const dot = document.querySelector(".top-bar__stigman-dot");
			expect(dot).toHaveClass("is-ok");
		});

		const pill = document.querySelector(".top-bar__stigman");
		expect(pill).toHaveAttribute(
			"title",
			"STIG Manager: https://stigman.example.internal, collection 17 — configured",
		);
	});

	it("shows not-configured (is-off dot) when GET /stigman 404s", async () => {
		installFetchMock(() =>
			jsonResponse({ error: { code: "not_found", message: "No global STIG Manager connection is configured." } }, 404),
		);
		mount();

		await waitFor(() => {
			const dot = document.querySelector(".top-bar__stigman-dot");
			expect(dot).toHaveClass("is-off");
		});

		const pill = document.querySelector(".top-bar__stigman");
		expect(pill).toHaveAttribute("title", "STIG Manager: not configured");
	});

	it("never renders a 'connected'/'not reachable' claim (regression guard for #316)", async () => {
		// The real wire never sends `connected` at all (PR #314's
		// `StigManagerConnectionResponse` has no such field) — this fixture is
		// the honest shape. Assert the pill's language talks about
		// configuration, never reachability, so a future regression back to
		// `stigman?.connected` (always `undefined`, always "not reachable")
		// would fail this on the wording alone.
		installFetchMock(() =>
			jsonResponse({
				endpoint: "https://stigman.example.internal",
				authority: "https://keycloak.example.internal/realms/waypoint",
				collection: "17",
				client_id: "waypoint-appliance",
				scope: "openid stig-manager:stig:read",
				credential_id: null,
				created_at: "2026-01-01T00:00:00Z",
				updated_at: "2026-07-01T00:00:00Z",
			}),
		);
		mount();

		await waitFor(() => {
			const dot = document.querySelector(".top-bar__stigman-dot");
			expect(dot).toHaveClass("is-ok");
		});

		const title = document.querySelector(".top-bar__stigman")?.getAttribute("title");
		expect(title).not.toMatch(/not reachable/i);
		expect(title).not.toMatch(/\bconnected\b/i);
	});
});

/**
 * Issue #94: `TopBar` used to derive its mode badge from `system?.mode ??
 * null` — the same conflation issue #82 removed from the router. `null`
 * meant both "still loading" and "the fetch failed", so a `GET /system`
 * that was merely slow rendered the false claim "could not reach the
 * Waypoint API." These tests pin the fix: a neutral loading treatment while
 * `!ready`, the real outage wording only once a fetch has actually settled
 * to failure, and the correct label for each resolved mode.
 */
describe("TopBar mode badge tri-state (issue #94)", () => {
	let originalFetch: typeof fetch;

	function installFetchMock(systemImpl: () => Promise<Response> | Response) {
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
			const url = typeof input === "string" ? input : input.toString();
			if (url === "/api/v1/system") {
				return systemImpl();
			}
			if (url === "/api/v1/stigman") {
				return jsonResponse({ error: { code: "not_found", message: "No global STIG Manager connection is configured." } }, 404);
			}
			throw new Error(`unexpected fetch: ${url}`);
		}) as unknown as typeof fetch;

		window.sessionStorage.setItem(
			"waypoint.session",
			JSON.stringify({
				token: "tok-1",
				username: "j.moreno",
				role: "Admin",
				expiresAt: new Date(Date.now() + 60_000).toISOString(),
			}),
		);
	}

	beforeEach(() => {
		originalFetch = globalThis.fetch;
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
		window.sessionStorage.clear();
	});

	function mount() {
		render(
			<AuthProvider>
				<SystemProvider>
					<ThemeProvider>
						<TopBar screenTitle="Dashboard" />
					</ThemeProvider>
				</SystemProvider>
			</AuthProvider>,
		);
	}

	it("shows a neutral loading badge (not the outage message) while /system is in flight", async () => {
		let resolveFetch: (() => void) | undefined;
		installFetchMock(
			() =>
				new Promise<Response>((resolve) => {
					resolveFetch = () => resolve(jsonResponse({ version: "2.4.1", build: "24817", mode: "connected", update_available: null }));
				}),
		);
		mount();

		const badge = document.querySelector(".top-bar__mode");
		expect(badge).toHaveClass("top-bar__mode--unknown");
		expect(badge?.textContent).toContain("CHECKING");
		expect(badge).toHaveAttribute("title", "Checking deployment mode…");
		expect(badge?.getAttribute("title")).not.toMatch(/could not reach/i);

		resolveFetch?.();
		await waitFor(() => {
			expect(document.querySelector(".top-bar__mode")).toHaveClass("top-bar__mode--connected");
		});
	});

	it("shows the real outage message only after the fetch settles to failure", async () => {
		installFetchMock(() => {
			throw new Error("network down");
		});
		mount();

		await waitFor(() => {
			const badge = document.querySelector(".top-bar__mode");
			expect(badge).toHaveClass("top-bar__mode--disconnected");
		});

		const badge = document.querySelector(".top-bar__mode");
		expect(badge).toHaveAttribute("title", "Deployment mode unavailable — could not reach the Waypoint API.");
		expect(badge?.textContent).toContain("MODE · AIR-GAPPED");
	});

	it("shows the connected label once mode resolves to connected", async () => {
		installFetchMock(() => jsonResponse({ version: "2.4.1", build: "24817", mode: "connected", update_available: null }));
		mount();

		await waitFor(() => {
			const badge = document.querySelector(".top-bar__mode");
			expect(badge).toHaveClass("top-bar__mode--connected");
			expect(badge?.textContent).toContain("MODE · INTERNET-ENABLED");
			expect(badge).toHaveAttribute(
				"title",
				"Internet-enabled: reaches the Broadcom depot and GitHub; all features; builds signed export bundles.",
			);
		});
	});

	it("shows the disconnected label (not the outage message) when /system resolves with mode disconnected", async () => {
		installFetchMock(() => jsonResponse({ version: "2.4.1", build: "24817", mode: "disconnected", update_available: null }));
		mount();

		await waitFor(() => {
			const badge = document.querySelector(".top-bar__mode");
			expect(badge).toHaveClass("top-bar__mode--disconnected");
			expect(badge?.textContent).toContain("MODE · AIR-GAPPED");
			expect(badge).toHaveAttribute(
				"title",
				"Air-gapped: no external network; consumes imported bundles; download/catalog features are hidden.",
			);
		});
	});
});
