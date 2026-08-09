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
