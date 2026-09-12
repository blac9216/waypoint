import { act, render, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { SystemProvider } from "../../lib/system";
import type { CatalogArtifact } from "./catalog";

/**
 * Issue #1803: `load`'s `.finally` used to call `setLoading(false)` after
 * unmount, guarded only by `isCurrent()` — unmount aborts the in-flight walk
 * (issue #1592 F4) but never bumps `loadGenerationRef`, so `isCurrent()`
 * stayed true on the unmount path. This is a documented no-op under React
 * 18+ (no crash, no console warning — see the issue's own "Impact" section),
 * so black-box DOM/console assertions cannot distinguish the fixed and
 * unfixed code. This test instead captures the real `setLoading` reference
 * via a scoped `react` mock (the only `useState(true)` caller left in the
 * tree once `useCatalogPull`/`StoresUsagePanel` are stubbed out) and spies on
 * it directly, so it can assert the setter is genuinely never invoked once
 * the walk's request has been aborted by unmount.
 */
vi.mock("./useCatalogPull", () => ({
	useCatalogPull: () => ({
		status: { ready: true },
		loading: false,
		loadError: null,
		reload: () => {},
		running: false,
		logLines: [],
		actionError: null,
		doPull: async () => {},
	}),
}));

vi.mock("./StoresUsagePanel", () => ({
	StoresUsagePanel: () => null,
}));

// Captured once, on the very first render's `useState(true)` call — the only
// one left in the tree once `useCatalogPull`/`StoresUsagePanel` are stubbed
// above. `load`'s `.finally` is bound via `useCallback(..., [])`, so its
// closure keeps calling whatever setter this first render returned for
// `setLoading`, regardless of what later renders return — exactly what lets
// a spy installed here observe every call `load` ever makes to it.
let loadingSetterSpy: ReturnType<typeof vi.fn> | null = null;

vi.mock("react", async (importOriginal) => {
	const actual = await importOriginal<typeof import("react")>();
	return {
		...actual,
		useState<T>(initial: T) {
			const result = actual.useState(initial);
			if (initial === true && loadingSetterSpy === null) {
				const real = result[1];
				loadingSetterSpy = vi.fn((v: unknown) => (real as (v: unknown) => void)(v));
				return [result[0], loadingSetterSpy] as typeof result;
			}
			return result;
		},
	};
});

// Imported after the mocks above so DownloadCatalogScreen picks up the
// mocked `react`/`./useCatalogPull`/`./StoresUsagePanel`.
const { DownloadCatalogScreen } = await import("./DownloadCatalogScreen");

const ARTIFACTS: CatalogArtifact[] = [
	{
		id: "art-1",
		name: "VCF-Installer-5.2.1.iso",
		sha256: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
		product: "VCF Installer",
		version: "5.2.1",
		size_bytes: 4_294_967_296,
		status: "indexed",
	},
];

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("DownloadCatalogScreen loading guard (issue #1803)", () => {
	let originalFetch: typeof fetch;
	let deferred: Array<(r: Response) => void>;
	let signals: AbortSignal[];

	beforeEach(() => {
		loadingSetterSpy = null;
		deferred = [];
		signals = [];
		originalFetch = globalThis.fetch;
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			if (url.startsWith("/api/v1/catalog/artifacts")) {
				if (init?.signal) {
					signals.push(init.signal as AbortSignal);
				}
				return new Promise<Response>((resolve) => {
					deferred.push(resolve);
				});
			}
			if (url.startsWith("/api/v1/events") || /^\/api\/v1\/runs\/[^/]+\/events/.test(url)) {
				return { ok: true, status: 200, body: null } as unknown as Response;
			}
			if (url === "/api/v1/downloads" && (!init || init.method === undefined || init.method === "GET")) {
				return jsonResponse([]);
			}
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
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
		window.sessionStorage.clear();
	});

	it("never calls setLoading(false) once the walk's request was aborted by unmount", async () => {
		const view = render(
			<AuthProvider>
				<SystemProvider>
					<DownloadCatalogScreen />
				</SystemProvider>
			</AuthProvider>,
		);
		await waitFor(() => expect(deferred.length).toBe(1));
		expect(loadingSetterSpy).not.toBeNull();
		// The initial mount's own `setLoading(true)` call (before the fetch
		// resolves) already went through the spy — clear that so the
		// assertion below is only about calls made *after* unmount.
		loadingSetterSpy!.mockClear();

		view.unmount();
		expect(signals[0].aborted).toBe(true);

		await act(async () => {
			deferred[0](
				new Response(JSON.stringify([ARTIFACTS[0]]), {
					status: 200,
					headers: { "Content-Type": "application/json", "X-Total-Count": "1" },
				}),
			);
			await new Promise((resolve) => setTimeout(resolve, 0));
		});

		// Pre-fix, `.finally`'s guard was `isCurrent()` alone — true here
		// since unmounting never bumps `loadGenerationRef` — so resolving the
		// deferred fetch after unmount still called the real `setLoading`.
		// Fixed, `.finally` also checks `controller.signal.aborted` (true
		// here, asserted above), so the setter is never reached.
		expect(loadingSetterSpy).not.toHaveBeenCalled();
	});
});
