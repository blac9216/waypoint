import { fireEvent, render, screen, waitFor } from "@testing-library/react";
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

/**
 * Issue #489: `GET /system`'s `runners[].starved_job_types` (issue #467's
 * per-runner resource-admission-starvation report) reaches the Runners
 * indicator. These pin the three states an operator can see: nothing
 * starved (existing behavior, unaffected), transient starvation (warn,
 * self-resolving), and permanent starvation (escalated bad-severity,
 * misconfiguration that never self-resolves on its own).
 */
describe("TopBar Runners indicator admission starvation (issue #489)", () => {
	let originalFetch: typeof fetch;

	function installFetchMock(runners: unknown[]) {
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
			const url = typeof input === "string" ? input : input.toString();
			if (url === "/api/v1/system") {
				return jsonResponse({ version: "2.4.1", build: "24817", mode: "connected", update_available: null, runners });
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

	function runnersPill(): Element {
		const pills = document.querySelectorAll(".top-bar__stigman");
		// The second `.top-bar__stigman`-shaped pill is the Runners indicator
		// (the first is the STIG Manager pill).
		const pill = pills[1];
		if (!pill) {
			throw new Error("Runners indicator pill not found");
		}
		return pill;
	}

	it("shows no starvation badge and an is-ok dot when no runner reports starved job types", async () => {
		installFetchMock([
			{
				worker_id: "compliance-runner-1",
				job_types: ["discover", "scan"],
				available: true,
				last_seen_at: "2026-08-11T00:00:00Z",
				starved_job_types: [],
			},
		]);
		mount();

		await waitFor(() => {
			expect(runnersPill().querySelector(".top-bar__stigman-dot")).toHaveClass("is-ok");
		});
		expect(runnersPill().querySelector(".top-bar__runners-starved")).toBeNull();
	});

	it("shows a transient-starvation warning (warn severity) without escalating to bad", async () => {
		installFetchMock([
			{
				worker_id: "download-runner-1",
				job_types: ["download", "catalog-index"],
				available: true,
				last_seen_at: "2026-08-11T00:00:00Z",
				starved_job_types: [{ job_type: "download", permanent: false }],
			},
		]);
		mount();

		await waitFor(() => {
			const badge = runnersPill().querySelector(".top-bar__runners-starved");
			expect(badge).not.toBeNull();
			expect(badge).toHaveClass("is-degraded");
			expect(badge?.textContent).toMatch(/starved/i);
			expect(badge?.textContent).not.toMatch(/permanent/i);
		});
		expect(runnersPill().querySelector(".top-bar__stigman-dot")).toHaveClass("is-degraded");
		expect(runnersPill().getAttribute("title")).toMatch(/download \(transient\)/);
	});

	it("shows a permanent-starvation warning (bad severity, misconfiguration) distinguished from transient", async () => {
		installFetchMock([
			{
				worker_id: "compliance-runner-1",
				job_types: ["discover", "scan"],
				available: true,
				last_seen_at: "2026-08-11T00:00:00Z",
				starved_job_types: [{ job_type: "scan", permanent: true }],
			},
		]);
		mount();

		await waitFor(() => {
			const badge = runnersPill().querySelector(".top-bar__runners-starved");
			expect(badge).not.toBeNull();
			expect(badge).toHaveClass("is-bad");
			expect(badge?.textContent).toMatch(/permanent/i);
		});
		expect(runnersPill().querySelector(".top-bar__stigman-dot")).toHaveClass("is-bad");
		expect(runnersPill().getAttribute("title")).toMatch(/scan \(permanent — misconfiguration, will not self-resolve\)/);
	});
});

/**
 * Issue #868: `AuthContext.logout()` (lib/auth.tsx) worked end-to-end since
 * #873 but no component ever invoked it — the rendered app had no sign-out
 * control at all. These pin the fix: the control renders only while signed
 * in, and activating it actually calls through to `logout()` rather than
 * just looking like a button.
 */
describe("TopBar sign-out control (issue #868)", () => {
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
	}

	function signIn() {
		window.sessionStorage.setItem(
			"waypoint.session",
			JSON.stringify({
				token: "tok-1",
				username: "j.moreno",
				role: "Admin",
				// No `kind` — restores as `"local"` (see auth.tsx's readStoredSession),
				// so logout() takes the plain dropSession() path and does not need
				// discovery/end-session fetch mocking on top of the above.
				expiresAt: new Date(Date.now() + 60_000).toISOString(),
			}),
		);
	}

	beforeEach(() => {
		originalFetch = globalThis.fetch;
		installFetchMock();
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

	it("renders a 'Sign out' control when signed in", async () => {
		signIn();
		mount();

		await waitFor(() => {
			expect(screen.getByRole("button", { name: "Sign out" })).toBeVisible();
		});
	});

	it("does not render the control when signed out", async () => {
		mount();

		await waitFor(() => {
			expect(document.querySelector(".top-bar__user")?.textContent).toBe("—");
		});
		expect(screen.queryByRole("button", { name: "Sign out" })).toBeNull();
	});

	it("invokes logout() (drops the session) when activated", async () => {
		signIn();
		mount();

		const button = await screen.findByRole("button", { name: "Sign out" });
		fireEvent.click(button);

		await waitFor(() => {
			expect(window.sessionStorage.getItem("waypoint.session")).toBeNull();
		});
		expect(document.querySelector(".top-bar__user")?.textContent).toBe("—");
	});
});
