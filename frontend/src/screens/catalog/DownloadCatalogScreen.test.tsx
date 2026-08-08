import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { SystemProvider } from "../../lib/system";
import type { WaypointEvent } from "../../lib/events";
import { DownloadCatalogScreen } from "./DownloadCatalogScreen";
import type { CatalogArtifact } from "./catalog";

/** A `fetch` mock for `/api/v1/events` whose stream stays open until the
 * test pushes into it — same helper shape as JobLogDrawer.test.tsx, reused
 * here because this screen's live progress is SSE-only, same as the drawer. */
function createDriveableSse() {
	const encoder = new TextEncoder();
	const queued: string[] = [];
	let resolveNext: ((r: { value: Uint8Array | undefined; done: boolean }) => void) | null = null;

	const reader = {
		read(): Promise<{ value: Uint8Array | undefined; done: boolean }> {
			const next = queued.shift();
			if (next !== undefined) {
				return Promise.resolve({ value: encoder.encode(next), done: false });
			}
			return new Promise((resolve) => {
				resolveNext = resolve;
			});
		},
		releaseLock() {},
	};

	return {
		response: { ok: true, status: 200, body: { getReader: () => reader } } as unknown as Response,
		push(text: string) {
			if (resolveNext) {
				const resolve = resolveNext;
				resolveNext = null;
				resolve({ value: encoder.encode(text), done: false });
			} else {
				queued.push(text);
			}
		},
	};
}

function frame(event: WaypointEvent): string {
	return `id: ${event.seq}\ndata: ${JSON.stringify(event)}\n\n`;
}

const ARTIFACTS: CatalogArtifact[] = [
	{
		id: "art-1",
		name: "VCF-Installer-5.2.1.iso",
		sha256: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
		product: "VCF Installer",
		version: "5.2.1",
		size_bytes: 4_294_967_296,
		status: "not_downloaded",
	},
	{
		id: "art-2",
		name: "ESXi-8.0U3-patch.zip",
		sha256: "a1b2c3d4e5f60718293a4b5c6d7e8f90112233445566778899aabbccddeeff0",
		product: "ESXi",
		version: "8.0U3",
		size_bytes: 734_003_200,
		status: "failed",
		failure_reason: "checksum mismatch",
	},
];

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("DownloadCatalogScreen", () => {
	let originalFetch: typeof fetch;
	let sse: ReturnType<typeof createDriveableSse>;
	let fetchCalls: { url: string; init?: RequestInit }[];
	let queuePostBody: unknown;

	function installFetchMock(role: string) {
		fetchCalls = [];
		sse = createDriveableSse();
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			fetchCalls.push({ url, init });

			if (url.startsWith("/api/v1/events")) {
				return sse.response;
			}
			if (url.startsWith("/api/v1/catalog/artifacts")) {
				return jsonResponse({ artifacts: ARTIFACTS, index_synced_at: "2026-08-08T12:00:00Z" });
			}
			if (url === "/api/v1/downloads" && (!init || init.method === undefined)) {
				return jsonResponse([]);
			}
			if (url === "/api/v1/downloads" && init?.method === "POST") {
				queuePostBody = JSON.parse(init.body as string);
				return jsonResponse({ run_id: "run-1", job_ids: ["job-1", "job-2"] });
			}
			if (url === "/api/v1/system") {
				return jsonResponse({ version: "2.4.1", build: "24817", mode: "connected", update_available: null });
			}
			if (url === "/api/v1/stigman") {
				return jsonResponse({ connected: false, endpoint: null, collection: null });
			}
			throw new Error(`unexpected fetch: ${url}`);
		}) as unknown as typeof fetch;

		window.sessionStorage.setItem(
			"waypoint.session",
			JSON.stringify({
				token: "tok-1",
				username: "j.moreno",
				role,
				expiresAt: new Date(Date.now() + 60_000).toISOString(),
			}),
		);
	}

	beforeEach(() => {
		originalFetch = globalThis.fetch;
		queuePostBody = undefined;
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
		window.sessionStorage.clear();
	});

	async function mount() {
		render(
			<AuthProvider>
				<SystemProvider>
					<DownloadCatalogScreen />
				</SystemProvider>
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("VCF-Installer-5.2.1.iso")).toBeInTheDocument());
	}

	async function deliver(text: string) {
		await act(async () => {
			sse.push(text);
			await new Promise((resolve) => setTimeout(resolve, 0));
		});
	}

	it("renders the artifact table from GET /catalog/artifacts", async () => {
		installFetchMock("Operator");
		await mount();

		expect(screen.getByText("ESXi-8.0U3-patch.zip")).toBeInTheDocument();
		expect(screen.getAllByText("VCF Installer").length).toBeGreaterThan(0);
		expect(screen.getByText(/Index synced/)).toBeInTheDocument();
	});

	it("renders the failed-checksum row distinctly with a retry affordance", async () => {
		installFetchMock("Operator");
		await mount();

		const failedStatus = screen.getByTitle("failed — checksum mismatch");
		expect(failedStatus).toHaveClass("artifact-table__status--bad");
		const row = failedStatus.closest("tr")!;
		expect(row).toHaveClass("is-failed");
		expect(within(row).getByRole("button", { name: "Retry" })).toBeInTheDocument();
	});

	it("re-queues just the failed artifact when Retry is clicked", async () => {
		installFetchMock("Operator");
		await mount();

		const row = screen.getByTitle("failed — checksum mismatch").closest("tr")!;
		fireEvent.click(within(row).getByRole("button", { name: "Retry" }));

		await waitFor(() => expect(queuePostBody).toEqual({ artifact_ids: ["art-2"] }));
	});

	it("filters via the search box, driving GET /catalog/artifacts?search=...", async () => {
		installFetchMock("Operator");
		await mount();

		fireEvent.change(screen.getByLabelText("Search artifacts"), { target: { value: "ESXi" } });

		await waitFor(() =>
			expect(fetchCalls.some((c) => c.url.includes("/catalog/artifacts?search=ESXi"))).toBe(true),
		);
	});

	it("selecting rows shows the sticky footer and queues N downloads via POST /downloads", async () => {
		installFetchMock("Operator");
		await mount();

		fireEvent.click(screen.getByLabelText("Select VCF-Installer-5.2.1.iso"));
		fireEvent.click(screen.getByLabelText("Select ESXi-8.0U3-patch.zip"));

		expect(screen.getByText("Queue 2 downloads")).toBeInTheDocument();

		fireEvent.click(screen.getByText("Queue 2 downloads"));

		await waitFor(() => expect(queuePostBody).toEqual({ artifact_ids: ["art-1", "art-2"] }));
	});

	it("disables the queue action with a reason below Operator", async () => {
		installFetchMock("Cyber");
		await mount();

		fireEvent.click(screen.getByLabelText("Select VCF-Installer-5.2.1.iso"));

		const button = screen.getByText("Queue 1 downloads");
		expect(button).toBeDisabled();
		expect(button).toHaveAttribute("title", expect.stringContaining("Requires Operator"));
	});

	it("does not show the retry affordance for a role below Operator", async () => {
		installFetchMock("Cyber");
		await mount();

		const row = screen.getByTitle("failed — checksum mismatch").closest("tr")!;
		expect(within(row).queryByRole("button", { name: "Retry" })).not.toBeInTheDocument();
	});

	it("updates status live from download.progress SSE events, no polling", async () => {
		installFetchMock("Operator");
		await mount();

		const fetchCountBefore = (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls.length;

		await deliver(
			frame({
				seq: 1,
				ts: "2026-08-08T12:01:00Z",
				type: "download.progress",
				job_id: "job-1",
				run_id: "run-1",
				data: { artifact_id: "art-1", state: "downloading", progress_percent: 43, rate_bytes_per_sec: 1048576, eta_seconds: 90, retries: 0 },
			}),
		);

		await waitFor(() => expect(screen.getByTitle("downloading 43%")).toBeInTheDocument());

		// The only additional network activity since mount is the SSE frame
		// itself — no re-fetch of /catalog/artifacts or /downloads happened.
		const fetchCountAfter = (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls.length;
		expect(fetchCountAfter).toBe(fetchCountBefore);

		await deliver(
			frame({
				seq: 2,
				ts: "2026-08-08T12:02:00Z",
				type: "job.state",
				job_id: "job-1",
				run_id: "run-1",
				data: { to: "verified" },
			}),
		);

		await waitFor(() => expect(screen.getByTitle("verified")).toBeInTheDocument());
	});

	it("mode-gating stub hides the screen when mode=disconnected", async () => {
		fetchCalls = [];
		sse = createDriveableSse();
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			if (url.startsWith("/api/v1/events")) return sse.response;
			if (url.startsWith("/api/v1/catalog/artifacts")) return jsonResponse({ artifacts: ARTIFACTS, index_synced_at: null });
			if (url === "/api/v1/downloads" && init?.method === undefined) return jsonResponse([]);
			if (url === "/api/v1/system") return jsonResponse({ version: "2.4.1", build: "24817", mode: "disconnected", update_available: null });
			if (url === "/api/v1/stigman") return jsonResponse({ connected: false, endpoint: null, collection: null });
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

		render(
			<AuthProvider>
				<SystemProvider>
					<DownloadCatalogScreen />
				</SystemProvider>
			</AuthProvider>,
		);

		await waitFor(() => expect(screen.getByText("Download Catalog unavailable")).toBeInTheDocument());
		expect(screen.queryByText("VCF-Installer-5.2.1.iso")).not.toBeInTheDocument();
	});
});
