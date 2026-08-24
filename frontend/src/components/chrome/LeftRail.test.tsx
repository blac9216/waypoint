import { render, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { RouterProvider } from "../../lib/router";
import { SystemProvider } from "../../lib/system";
import { LeftRail } from "./LeftRail";

/**
 * Issue #94: `LeftRail` used to recompute `mode` locally from `system?.mode
 * ?? null` instead of consuming `useSystem().mode`/`ready` — the same
 * "unknown vs disconnected" conflation issue #82 removed from the router.
 * These tests pin the fix: the `connectedOnly` nav item ("Download
 * Catalog") stays hidden while the system fetch is in flight (fail-safe,
 * unchanged from before) and appears once `mode` resolves to `"connected"`,
 * driven by the shared tri-state rather than a second local derivation.
 */

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(body === undefined ? null : JSON.stringify(body), {
		status,
		headers: { "Content-Type": "application/json" },
	});
}

describe("LeftRail connectedOnly nav gating (issue #94)", () => {
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
				role: "Operator",
				expiresAt: new Date(Date.now() + 60_000).toISOString(),
			}),
		);
	}

	beforeEach(() => {
		originalFetch = globalThis.fetch;
		window.history.pushState(null, "", "/");
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
		window.sessionStorage.clear();
	});

	function mount() {
		render(
			<AuthProvider>
				<RouterProvider>
					<SystemProvider>
						<LeftRail open onToggle={() => {}} />
					</SystemProvider>
				</RouterProvider>
			</AuthProvider>,
		);
	}

	it("hides the connectedOnly item while the system fetch is in flight (mode unknown, fail-safe)", async () => {
		let resolveFetch: (() => void) | undefined;
		installFetchMock(
			() =>
				new Promise<Response>((resolve) => {
					resolveFetch = () => resolve(jsonResponse({ version: "2.4.1", build: "24817", mode: "connected", update_available: null }));
				}),
		);
		mount();

		// While in flight, `ready` is false and `mode` is "unknown" — the
		// connectedOnly item must stay hidden, same fail-safe direction as
		// the old code, but now driven by the shared tri-state.
		expect(document.body.textContent).not.toContain("Download Catalog");

		resolveFetch?.();
		await waitFor(() => {
			expect(document.body.textContent).toContain("Download Catalog");
		});
	});

	it("shows the connectedOnly item once mode resolves to connected", async () => {
		installFetchMock(() => jsonResponse({ version: "2.4.1", build: "24817", mode: "connected", update_available: null }));
		mount();

		await waitFor(() => {
			expect(document.body.textContent).toContain("Download Catalog");
		});
	});

	it("keeps the connectedOnly item hidden once mode settles to disconnected", async () => {
		installFetchMock(() => jsonResponse({ version: "2.4.1", build: "24817", mode: "disconnected", update_available: null }));
		mount();

		await waitFor(() => {
			// Wait for the fetch to settle by asserting on an always-present item.
			expect(document.body.textContent).toContain("Dashboard");
		});
		expect(document.body.textContent).not.toContain("Download Catalog");
	});

	it("keeps the connectedOnly item hidden when the system fetch fails outright", async () => {
		installFetchMock(() => {
			throw new Error("network down");
		});
		mount();

		await waitFor(() => {
			expect(document.body.textContent).toContain("Dashboard");
		});
		expect(document.body.textContent).not.toContain("Download Catalog");
	});
});

/**
 * Issue #711: PR #709 restored the /live-run console but not its nav entry —
 * LeftRail's NavGroup type excluded `live-run` (a #590/ADR-0019-era decision
 * that predated the console's restoration), so it was reachable only via
 * Start-a-Scan, job-detail links, or a direct URL. Pins that Live Run now
 * renders in the COMPLIANCE nav group and links to /live-run.
 */
describe("LeftRail Live Run nav entry (issue #711)", () => {
	let originalFetch: typeof fetch;

	function installFetchMock() {
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
			const url = typeof input === "string" ? input : input.toString();
			if (url === "/api/v1/system") {
				return jsonResponse({ version: "2.4.1", build: "24817", mode: "connected", update_available: null });
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
				role: "Operator",
				expiresAt: new Date(Date.now() + 60_000).toISOString(),
			}),
		);
	}

	beforeEach(() => {
		originalFetch = globalThis.fetch;
		window.history.pushState(null, "", "/");
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
		window.sessionStorage.clear();
	});

	function mount() {
		render(
			<AuthProvider>
				<RouterProvider>
					<SystemProvider>
						<LeftRail open onToggle={() => {}} />
					</SystemProvider>
				</RouterProvider>
			</AuthProvider>,
		);
	}

	it("renders Live Run in the nav, linking to /live-run", async () => {
		installFetchMock();
		mount();

		await waitFor(() => {
			expect(document.body.textContent).toContain("Live Run");
		});
		const link = document.querySelector('a[href="/live-run"]');
		expect(link).not.toBeNull();
		expect(link?.textContent).toContain("Live Run");
	});
});
