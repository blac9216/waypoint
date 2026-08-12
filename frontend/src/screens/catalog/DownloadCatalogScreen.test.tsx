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
				// The real CatalogController.ListArtifacts returns a bare array
				// (`return Ok(items...)`), not an envelope with index_synced_at —
				// see catalog.ts's fetchCatalogArtifacts doc comment (issue #468
				// found the mismatch live). Mocking the real shape here.
				return jsonResponse(ARTIFACTS);
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
				return jsonResponse({ error: { code: "not_found", message: "No global STIG Manager connection is configured." } }, 404);
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

	it("filters via the search box client-side (backend has no search query parameter — issue #468)", async () => {
		installFetchMock("Operator");
		await mount();

		expect(screen.getByText("ESXi-8.0U3-patch.zip")).toBeInTheDocument();
		expect(screen.getAllByText("VCF Installer").length).toBeGreaterThan(0);

		fireEvent.change(screen.getByLabelText("Search artifacts"), { target: { value: "ESXi" } });

		await waitFor(() => expect(screen.queryByText("VCF-Installer-5.2.1.iso")).not.toBeInTheDocument());
		expect(screen.getByText("ESXi-8.0U3-patch.zip")).toBeInTheDocument();
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

	it("shows a transfer-time estimate in the footer for a non-empty selection (assumed-bandwidth basis)", async () => {
		installFetchMock("Operator");
		await mount();

		// art-2 alone is 734,003,200 bytes; with no live download rate the
		// footer falls back to ASSUMED_BANDWIDTH_BYTES_PER_SEC (1,250,000 B/s
		// = 1.2 MB/s), giving ~587s -> rounds to 10m.
		fireEvent.click(screen.getByLabelText("Select ESXi-8.0U3-patch.zip"));

		expect(screen.getByText("est. 10m at 1.2 MB/s")).toBeInTheDocument();
	});

	it("scales the transfer estimate with the size of the selection", async () => {
		installFetchMock("Operator");
		await mount();

		fireEvent.click(screen.getByLabelText("Select ESXi-8.0U3-patch.zip"));
		expect(screen.getByText("est. 10m at 1.2 MB/s")).toBeInTheDocument();

		// Adding the 4 GiB ISO brings the selection to 5,028,970,496 bytes,
		// which crosses into the hours formatting (~67 minutes -> 1hr 7m).
		fireEvent.click(screen.getByLabelText("Select VCF-Installer-5.2.1.iso"));
		expect(screen.getByText("est. 1hr 7m at 1.2 MB/s")).toBeInTheDocument();
		expect(screen.queryByText("est. 10m at 1.2 MB/s")).not.toBeInTheDocument();
	});

	it("prefers the queue's live aggregate rate over the assumed-bandwidth constant", async () => {
		installFetchMock("Operator");
		await mount();

		fireEvent.click(screen.getByLabelText("Select ESXi-8.0U3-patch.zip"));
		expect(screen.getByText("est. 10m at 1.2 MB/s")).toBeInTheDocument();

		// A live downloading job reports a much faster rate (10 MB/s) than the
		// assumed constant — the footer should switch to using it.
		await deliver(
			frame({
				seq: 1,
				ts: "2026-08-08T12:01:00Z",
				type: "download.progress",
				job_id: "job-9",
				run_id: "run-9",
				data: { artifact_id: "art-9", state: "downloading", progress_percent: 10, rate_bytes_per_sec: 10_000_000, eta_seconds: 900, retries: 0 },
			}),
		);

		await waitFor(() => expect(screen.getByText(/at 9\.5 MB\/s/)).toBeInTheDocument());
	});

	it("hides the transfer estimate when the selection is empty", async () => {
		installFetchMock("Operator");
		await mount();

		expect(screen.queryByText(/^est\./)).not.toBeInTheDocument();

		fireEvent.click(screen.getByLabelText("Select ESXi-8.0U3-patch.zip"));
		expect(screen.getByText(/^est\./)).toBeInTheDocument();

		fireEvent.click(screen.getByText("Clear"));
		expect(screen.queryByText(/^est\./)).not.toBeInTheDocument();
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
			if (url.startsWith("/api/v1/catalog/artifacts")) return jsonResponse(ARTIFACTS);
			if (url === "/api/v1/downloads" && init?.method === undefined) return jsonResponse([]);
			if (url === "/api/v1/system") return jsonResponse({ version: "2.4.1", build: "24817", mode: "disconnected", update_available: null });
			if (url === "/api/v1/stigman") return jsonResponse({ error: { code: "not_found", message: "No global STIG Manager connection is configured." } }, 404);
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
