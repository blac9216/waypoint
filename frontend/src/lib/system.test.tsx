import { render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "./auth";
import { SYSTEM_POLL_INTERVAL_MS, SystemProvider } from "./system";
import { useSystem } from "./system-context";

/**
 * Issue #495: `SystemProvider` fetched `/system` once at sign-in and never
 * again, so the top-bar Runners indicator (and starvation state from
 * #489/#490) went stale until a full reload. These tests pin the fix: a
 * timer-driven re-poll, a focus/visibility-driven re-poll, and — the part
 * most likely to regress — that a *failed* refetch keeps the last-known
 * state instead of flapping back to "unknown"/disconnected.
 */

const STORAGE_KEY = "waypoint.session";

function seedSignedInSession(): void {
	window.sessionStorage.setItem(
		STORAGE_KEY,
		JSON.stringify({
			token: "tok-1",
			username: "j.moreno",
			role: "Admin",
			expiresAt: new Date(Date.now() + 60_000).toISOString(),
		}),
	);
}

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(body === undefined ? null : JSON.stringify(body), {
		status,
		headers: { "Content-Type": "application/json" },
	});
}

function systemBody(runnerAvailable: boolean) {
	return {
		version: "2.4.1",
		build: "24817",
		mode: "connected",
		update_available: null,
		runners: [
			{
				worker_id: "compliance-runner-1",
				job_types: ["stig-scan"],
				available: runnerAvailable,
				last_seen_at: new Date().toISOString(),
				starved_job_types: [],
			},
		],
	};
}

function Probe() {
	const { system, ready } = useSystem();
	if (!ready) {
		return <div>loading</div>;
	}
	const runner = system?.runners[0];
	return <div>runner-available={runner ? String(runner.available) : "none"}</div>;
}

describe("SystemProvider re-polling (issue #495)", () => {
	let originalFetch: typeof fetch;
	let systemResponses: Array<() => Response>;

	beforeEach(() => {
		originalFetch = globalThis.fetch;
		vi.useFakeTimers();
		seedSignedInSession();
		systemResponses = [];

		globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
			const url = typeof input === "string" ? input : input.toString();
			if (url === "/api/v1/system") {
				const next = systemResponses.shift();
				return next ? next() : jsonResponse(systemBody(true));
			}
			if (url === "/api/v1/stigman") {
				return jsonResponse(undefined, 404);
			}
			throw new Error(`unexpected fetch: ${url}`);
		}) as unknown as typeof fetch;
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
		window.sessionStorage.clear();
		vi.useRealTimers();
	});

	function mount() {
		render(
			<AuthProvider>
				<SystemProvider>
					<Probe />
				</SystemProvider>
			</AuthProvider>,
		);
	}

	it("fetches /system once on mount", async () => {
		systemResponses.push(() => jsonResponse(systemBody(true)));
		mount();

		await vi.waitFor(() => expect(screen.getByText("runner-available=true")).toBeInTheDocument());

		const systemCalls = (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls.filter((call: unknown[]) => {
			const input = call[0] as RequestInfo | URL;
			return (typeof input === "string" ? input : input.toString()) === "/api/v1/system";
		});
		expect(systemCalls.length).toBe(1);
	});

	it("re-polls /system on the interval and reflects a changed runner state", async () => {
		systemResponses.push(() => jsonResponse(systemBody(true)));
		mount();
		await vi.waitFor(() => expect(screen.getByText("runner-available=true")).toBeInTheDocument());

		systemResponses.push(() => jsonResponse(systemBody(false)));
		await vi.advanceTimersByTimeAsync(SYSTEM_POLL_INTERVAL_MS);

		await vi.waitFor(() => expect(screen.getByText("runner-available=false")).toBeInTheDocument());
	});

	it("re-polls /system on window focus (visibilitychange to visible)", async () => {
		systemResponses.push(() => jsonResponse(systemBody(true)));
		mount();
		await vi.waitFor(() => expect(screen.getByText("runner-available=true")).toBeInTheDocument());

		systemResponses.push(() => jsonResponse(systemBody(false)));
		Object.defineProperty(document, "visibilityState", { value: "visible", configurable: true });
		document.dispatchEvent(new Event("visibilitychange"));

		await vi.waitFor(() => expect(screen.getByText("runner-available=false")).toBeInTheDocument());
	});

	it("keeps the last-known state when a re-poll fails, instead of flapping", async () => {
		systemResponses.push(() => jsonResponse(systemBody(true)));
		mount();
		await vi.waitFor(() => expect(screen.getByText("runner-available=true")).toBeInTheDocument());

		systemResponses.push(() => jsonResponse(undefined, 500));
		await vi.advanceTimersByTimeAsync(SYSTEM_POLL_INTERVAL_MS);

		// Give the failed fetch's microtasks a chance to settle, then assert
		// the previously-fetched value is still shown rather than cleared.
		await vi.advanceTimersByTimeAsync(0);
		expect(screen.getByText("runner-available=true")).toBeInTheDocument();
	});

	it("does not leak the poll timer or focus/visibility listeners on unmount", async () => {
		systemResponses.push(() => jsonResponse(systemBody(true)));

		const addSpy = vi.spyOn(document, "addEventListener");
		const removeSpy = vi.spyOn(document, "removeEventListener");
		const winAddSpy = vi.spyOn(window, "addEventListener");
		const winRemoveSpy = vi.spyOn(window, "removeEventListener");

		const { unmount } = render(
			<AuthProvider>
				<SystemProvider>
					<Probe />
				</SystemProvider>
			</AuthProvider>,
		);
		await vi.waitFor(() => expect(screen.getByText("runner-available=true")).toBeInTheDocument());

		const visibilityAdds = addSpy.mock.calls.filter((call) => call[0] === "visibilitychange").length;
		const focusAdds = winAddSpy.mock.calls.filter((call) => call[0] === "focus").length;
		expect(visibilityAdds).toBeGreaterThan(0);
		expect(focusAdds).toBeGreaterThan(0);

		unmount();

		const visibilityRemoves = removeSpy.mock.calls.filter((call) => call[0] === "visibilitychange").length;
		const focusRemoves = winRemoveSpy.mock.calls.filter((call) => call[0] === "focus").length;
		expect(visibilityRemoves).toBe(visibilityAdds);
		expect(focusRemoves).toBe(focusAdds);

		// An orphaned timer would still fire after unmount; this must be silent.
		await vi.advanceTimersByTimeAsync(SYSTEM_POLL_INTERVAL_MS);

		addSpy.mockRestore();
		removeSpy.mockRestore();
		winAddSpy.mockRestore();
		winRemoveSpy.mockRestore();
	});
});
