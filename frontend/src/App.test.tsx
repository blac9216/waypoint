import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import App from "./App";
import { SYSTEM_FETCH_TIMEOUT_MS } from "./lib/system";
import { CatalogScreen, ConfigurationScreen } from "./screens/screens";

/**
 * Mount-detecting spy for issue #78 (extended to `CatalogScreen` for issue
 * #82): wraps the real component (preserving its rendered output, so tests
 * that DO expect it to render still see the real markup) while recording
 * every invocation. A React function component's body runs exactly when
 * React mounts it into the tree, so "the spy was never called" is
 * equivalent to "the component was never mounted" — a stronger claim than
 * "the DOM doesn't contain its text", because it holds even before any of
 * the component's own effects (e.g. a future on-mount fetch) have had a
 * chance to run.
 */
vi.mock("./screens/screens", async (importOriginal) => {
	const actual = await importOriginal<typeof import("./screens/screens")>();
	return {
		...actual,
		ConfigurationScreen: vi.fn(actual.ConfigurationScreen),
		CatalogScreen: vi.fn(actual.CatalogScreen),
	};
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
			return jsonResponse({
				endpoint: "https://stigman.example.internal",
				authority: "https://keycloak.example.internal/realms/waypoint",
				collection: "17",
				client_id: "waypoint-appliance",
				scope: "openid stig-manager:stig:read",
				credential_id: "cred-token-1",
				created_at: "2026-01-01T00:00:00Z",
				updated_at: "2026-07-01T00:00:00Z",
			});
		}
		if (url.startsWith("/api/v1/events")) {
			// Stay open indefinitely (a real SSE connection); the test never awaits it.
			return new Promise(() => {});
		}
		return jsonResponse({ error: { code: "not_found", message: "unhandled in test" } }, 404);
	}) as unknown as typeof fetch;
}

/**
 * Like `installChromeFetchMock`, but `/api/v1/system` doesn't resolve until
 * `resolveSystem` (returned) is called — the deferred window issue #82 lives
 * in. Login and `/stigman` still resolve immediately, matching the real
 * timing (`/system` and `/stigman` are fired together per `system.tsx`, but
 * only `/system`'s answer matters for mode-gating).
 */
function installDeferredSystemFetchMock(role: "Viewer" | "Admin" | "Operator" = "Operator") {
	let resolveSystem!: (mode: "connected" | "disconnected") => void;
	const systemPromise = new Promise<"connected" | "disconnected">((resolve) => {
		resolveSystem = resolve;
	});

	globalThis.fetch = vi.fn(async (url: string) => {
		if (url === "/api/v1/auth/login") {
			return jsonResponse({ token: "tok-1", role, expires_at: new Date(Date.now() + 60 * 60 * 1000).toISOString() });
		}
		if (url === "/api/v1/auth/me") {
			return jsonResponse({ username: "j.moreno", role });
		}
		if (url === "/api/v1/system") {
			const mode = await systemPromise;
			return jsonResponse({ version: "0.1.0-dev", build: "local", mode, update_available: null });
		}
		if (url === "/api/v1/stigman") {
			return jsonResponse({ error: { code: "not_found", message: "No global STIG Manager connection is configured." } }, 404);
		}
		if (url.startsWith("/api/v1/events")) {
			return new Promise(() => {});
		}
		return jsonResponse({ error: { code: "not_found", message: "unhandled in test" } }, 404);
	}) as unknown as typeof fetch;

	return { resolveSystem };
}

/**
 * Like `installDeferredSystemFetchMock`, but `/system` and `/stigman` are
 * never resolved by anyone — the wedged-backend shape from the PR #88
 * round-1 review, finding 2: the connection is accepted and then no answer
 * ever comes. Distinct from an error (a 500 rejects, so `ready` flips and
 * mode folds to `disconnected` already) and the only case that was
 * unbounded.
 *
 * The `AbortSignal` is honoured exactly as the platform `fetch` does — it
 * rejects with an `AbortError` `DOMException` — so this mock can only be
 * escaped by the client actually aborting the request. If `apiFetch` sets no
 * timeout, nothing here ever settles, which is precisely the condition under
 * test.
 */
function installHangingSystemFetchMock(role: "Viewer" | "Admin" | "Operator" = "Operator") {
	globalThis.fetch = vi.fn(async (url: string, init?: RequestInit) => {
		if (url === "/api/v1/auth/login") {
			return jsonResponse({ token: "tok-1", role, expires_at: new Date(Date.now() + 60 * 60 * 1000).toISOString() });
		}
		if (url === "/api/v1/auth/me") {
			return jsonResponse({ username: "j.moreno", role });
		}
		if (url === "/api/v1/system" || url === "/api/v1/stigman") {
			return new Promise<Response>((_resolve, reject) => {
				init?.signal?.addEventListener("abort", () => {
					reject(new DOMException("The operation was aborted.", "AbortError"));
				});
			});
		}
		if (url.startsWith("/api/v1/events")) {
			return new Promise(() => {});
		}
		return jsonResponse({ error: { code: "not_found", message: "unhandled in test" } }, 404);
	}) as unknown as typeof fetch;
}

/**
 * The shape round 2 of the PR #88 review measured, and the round-1 fix did
 * NOT cover: `/system` answers with a **200 and headers**, then its body
 * never completes. `fetch` resolves as soon as headers are in, so a timer
 * cleared in a `finally` attached to the fetch call is already gone by the
 * time `response.json()` is awaited — and that read is then unbounded.
 * Identical end state to a full hang: `ready` false forever, `/catalog` a
 * blank page. Measured at 25 s in real Chromium, three times past the 8 s
 * bound.
 *
 * The body stream here only ever settles by being cancelled, which `fetch`
 * does when its signal aborts — so a test on this mock cannot pass unless the
 * deadline genuinely spans the body read.
 */
function installStallingBodyFetchMock(role: "Viewer" | "Admin" | "Operator" = "Operator") {
	globalThis.fetch = vi.fn(async (url: string, init?: RequestInit) => {
		if (url === "/api/v1/auth/login") {
			return jsonResponse({ token: "tok-1", role, expires_at: new Date(Date.now() + 60 * 60 * 1000).toISOString() });
		}
		if (url === "/api/v1/auth/me") {
			return jsonResponse({ username: "j.moreno", role });
		}
		if (url === "/api/v1/system" || url === "/api/v1/stigman") {
			const stream = new ReadableStream({
				start(controller) {
					// A first chunk lands, so this is unambiguously a live 200
					// with a partial body rather than a connect that never
					// completed — then nothing more ever arrives.
					controller.enqueue(new TextEncoder().encode('{"version":"0.1.0-dev",'));
					init?.signal?.addEventListener("abort", () => {
						controller.error(new DOMException("The operation was aborted.", "AbortError"));
					});
				},
			});
			return new Response(stream, { status: 200, headers: { "Content-Type": "application/json" } });
		}
		if (url.startsWith("/api/v1/events")) {
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
		vi.mocked(CatalogScreen).mockClear();
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
				return jsonResponse({ error: { code: "not_found", message: "No global STIG Manager connection is configured." } }, 404);
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

	/**
	 * Issue #82: a connected-only deep link (`/catalog`) used to be redirected
	 * to Dashboard because the mode guard decided against `mode: null` before
	 * `GET /api/v1/system` resolved. These tests hold `/system` open with
	 * `installDeferredSystemFetchMock` to make that window observable and
	 * assert on both ends of it: nothing decided while pending (never mounts
	 * CatalogScreen, never rewrites the URL away), then the correct outcome
	 * once mode is known — allowed when connected, redirected when not.
	 */
	describe("mode-gated deep link waits for /api/v1/system instead of deciding early (issue #82)", () => {
		it("neither mounts CatalogScreen nor rewrites the URL while mode is still unknown", async () => {
			window.history.pushState(null, "", "/catalog");
			const { resolveSystem } = installDeferredSystemFetchMock("Operator");
			render(<App />);
			await signIn();

			// Give pending microtasks/effects a turn without ever resolving
			// /system — if the old bug were present, the redirect effect would
			// already have fired and rewritten the URL by now.
			await new Promise((resolve) => setTimeout(resolve, 20));

			expect(window.location.pathname).toBe("/catalog");
			expect(CatalogScreen).not.toHaveBeenCalled();

			resolveSystem("connected");
			await waitFor(() => expect(CatalogScreen).toHaveBeenCalled());
		});

		it("mounts CatalogScreen and keeps the deep-linked URL once mode resolves to connected", async () => {
			window.history.pushState(null, "", "/catalog");
			const { resolveSystem } = installDeferredSystemFetchMock("Operator");
			render(<App />);
			await signIn();

			resolveSystem("connected");

			await waitFor(() => expect(CatalogScreen).toHaveBeenCalled());
			// The fix: the URL was never rewritten during the pending window, so
			// it's still exactly the deep link the user followed.
			expect(window.location.pathname).toBe("/catalog");
			expect(screen.getAllByText("Download Catalog").length).toBeGreaterThan(0);
		});

		it("still redirects to Dashboard once mode resolves to disconnected — the pending window doesn't turn into a silent allow", async () => {
			window.history.pushState(null, "", "/catalog");
			const { resolveSystem } = installDeferredSystemFetchMock("Operator");
			render(<App />);
			await signIn();

			resolveSystem("disconnected");

			await waitFor(() => expect(window.location.pathname).toBe("/"));
			expect(CatalogScreen).not.toHaveBeenCalled();
		});

		/**
		 * PR #88 round-1 review, finding 2. `apiFetch` set no `AbortSignal`, so
		 * a `/system` that hangs rather than errors left `ready` false, `mode`
		 * `"unknown"` and `access` `"pending"` forever — and `AppShell` renders
		 * `null` while pending, so `/catalog` was a permanently blank page with
		 * no chrome, no spinner and no error (measured in Chromium at 1 s,
		 * 3.5 s and 9.5 s with `bodyTextLen: 0`, where `main` showed a usable
		 * Dashboard). The fix bounds the fetch; the assertion is that the wait
		 * *ends*, in the same fail-safe place a 500 already lands.
		 *
		 * Fake timers, because the point is a real 8-second wall clock and
		 * asserting on both sides of it. RTL's `waitFor` cannot drive vitest's
		 * fake timers (it looks for a `jest` global), so this drives `act`
		 * directly rather than polling.
		 */
		it("bounds the pending window when /api/v1/system hangs, folding to the same fail-safe a 500 takes", async () => {
			vi.useFakeTimers();
			try {
				window.history.pushState(null, "", "/catalog");
				installHangingSystemFetchMock("Operator");
				render(<App />);
				await act(async () => {
					signIn();
				});

				// Just before the deadline: still the pathological state the
				// review measured — nothing rendered at all, not even chrome.
				await act(async () => {
					await vi.advanceTimersByTimeAsync(SYSTEM_FETCH_TIMEOUT_MS - 500);
				});
				expect(document.body.textContent).toBe("");
				expect(CatalogScreen).not.toHaveBeenCalled();

				// Past the deadline the fetch is abandoned and rejects, so
				// `ready` flips, mode folds to "disconnected", /catalog is denied
				// (not pending), and the operator gets a usable Dashboard.
				await act(async () => {
					await vi.advanceTimersByTimeAsync(1000);
				});
				expect(screen.getByText("WAYPOINT")).toBeInTheDocument();
				expect(screen.getAllByText("Dashboard").length).toBeGreaterThan(0);
				expect(window.location.pathname).toBe("/");
				// The pending window never turned into a silent allow on the way
				// out (the #78 never-mount property, across the timeout path).
				expect(CatalogScreen).not.toHaveBeenCalled();
			} finally {
				vi.useRealTimers();
			}
		});

		it("aborts the hung request rather than leaving it outstanding", async () => {
			// The mechanism, asserted directly: the mock only ever settles if
			// the client aborts it, so a `ready` chrome here is proof the
			// AbortSignal fired. Guards against a future "fix" that flips
			// `ready` on a bare timer while the request stays open — which
			// would settle the UI but leak a connection per sign-in.
			vi.useFakeTimers();
			try {
				installHangingSystemFetchMock("Operator");
				render(<App />);
				await act(async () => {
					signIn();
				});
				const systemCall = vi.mocked(globalThis.fetch).mock.calls.find(([url]) => url === "/api/v1/system");
				const signal = (systemCall?.[1] as RequestInit | undefined)?.signal;
				expect(signal).toBeDefined();
				expect(signal?.aborted).toBe(false);

				await act(async () => {
					await vi.advanceTimersByTimeAsync(SYSTEM_FETCH_TIMEOUT_MS + 500);
				});

				expect(signal?.aborted).toBe(true);
				expect(screen.getByText("WAYPOINT")).toBeInTheDocument();
			} finally {
				vi.useRealTimers();
			}
		});

		/**
		 * PR #88 round-2 review, finding 1. The round-1 fix bounded only the
		 * headers phase — `clearTimeout` ran in a `finally` attached to the
		 * `fetch` call, which resolves when headers arrive — so a 200 whose
		 * body stalls left `await response.json()` unbounded and reproduced
		 * the blank page exactly (measured at 25 s in Chromium, past the 8 s
		 * bound). The two existing hang tests above could not see it, because
		 * their `fetch` never resolves at all.
		 */
		it("bounds a 200 whose response BODY stalls, not just a connect that never completes", async () => {
			vi.useFakeTimers();
			try {
				window.history.pushState(null, "", "/catalog");
				installStallingBodyFetchMock("Operator");
				render(<App />);
				await act(async () => {
					signIn();
				});

				// Headers are in and a first body chunk landed, so the round-1
				// timer would already have been cleared here. Still blank.
				await act(async () => {
					await vi.advanceTimersByTimeAsync(SYSTEM_FETCH_TIMEOUT_MS - 500);
				});
				expect(document.body.textContent).toBe("");
				expect(CatalogScreen).not.toHaveBeenCalled();

				// Past the deadline the body read is abandoned, so `ready`
				// flips, mode folds to "disconnected", and the chrome renders.
				await act(async () => {
					await vi.advanceTimersByTimeAsync(1000);
				});
				expect(screen.getByText("WAYPOINT")).toBeInTheDocument();
				expect(window.location.pathname).toBe("/");
				expect(CatalogScreen).not.toHaveBeenCalled();
			} finally {
				vi.useRealTimers();
			}
		});

		it("a role-insufficient deep link is still denied immediately, without waiting on mode at all", async () => {
			// Role settles synchronously (evaluateRouteAccess checks it first),
			// so a Viewer hitting /catalog must redirect right away — it should
			// not sit "pending" just because /catalog also happens to be
			// connectedOnly.
			window.history.pushState(null, "", "/catalog");
			installDeferredSystemFetchMock("Viewer");
			render(<App />);
			await signIn();

			await waitFor(() => expect(window.location.pathname).toBe("/"));
			expect(CatalogScreen).not.toHaveBeenCalled();
		});
	});
});
