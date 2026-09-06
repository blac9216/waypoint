import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { SystemProvider } from "../../lib/system";
import type { WaypointEvent } from "../../lib/events";
import { DownloadCatalogScreen } from "./DownloadCatalogScreen";
import type { CatalogArtifact, CatalogPullStatus } from "./catalog";

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
		status: "indexed",
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

/** A dominant-VKR fixture (issue #796's discovery case: VKR is 433 of the
 * real catalog's 1,088 entries) — two core-infrastructure products plus a
 * disproportionately large VKR group, to prove the Kubernetes group
 * collapses by default while core products stay visible without scrolling
 * past it. */
function dominantVkrArtifacts(vkrCount: number): CatalogArtifact[] {
	const vkr: CatalogArtifact[] = Array.from({ length: vkrCount }, (_, i) => ({
		id: `vkr-${i}`,
		name: `vkr-release-${i}.tar`,
		sha256: `${"a".repeat(63)}${(i % 10).toString()}`,
		product: "VKR",
		version: `1.${i}.0`,
		size_bytes: 1_000_000,
		status: "indexed" as const,
	}));
	return [
		{
			id: "art-vcenter",
			name: "VCSA-8.0U3.iso",
			sha256: "b".repeat(64),
			product: "VCENTER",
			version: "8.0U3",
			size_bytes: 2_000_000,
			status: "indexed",
		},
		{
			id: "art-esx",
			name: "ESXi-8.0U3.zip",
			sha256: "c".repeat(64),
			product: "ESX_HOST",
			version: "8.0U3",
			size_bytes: 1_500_000,
			status: "indexed",
		},
		...vkr,
	];
}

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

/** A paged `/catalog/artifacts` response: slices `artifacts` by the
 * request's `limit`/`offset` query params and sets `X-Total-Count` to the
 * full array length, mirroring the real `CatalogController.ListArtifacts`
 * (`Waypoint.Core.Pagination.PageRequest`) — issue #796 finding 1. A mock
 * that ignores `limit`/`offset` and always returns the whole fixture cannot
 * catch a regression to a single unpaged fetch; this one can. */
function pagedArtifactsResponse(url: string, artifacts: CatalogArtifact[]): Response {
	const params = new URL(url, "http://localhost").searchParams;
	const limit = Number(params.get("limit") ?? artifacts.length);
	const offset = Number(params.get("offset") ?? 0);
	const page = artifacts.slice(offset, offset + limit);
	return new Response(JSON.stringify(page), {
		status: 200,
		headers: { "Content-Type": "application/json", "X-Total-Count": String(artifacts.length) },
	});
}

const READY_PULL_STATUS: CatalogPullStatus = { ready: true };
const NOT_READY_PULL_STATUS: CatalogPullStatus = {
	ready: false,
	not_ready_reason:
		"Connected catalog pull is disabled until the managed tool is installed, a Software Depot ID is generated, and a matching Activation Code has been validated (see Depot & Tokens enrollment).",
};

describe("DownloadCatalogScreen", () => {
	let originalFetch: typeof fetch;
	let sse: ReturnType<typeof createDriveableSse>;
	let fetchCalls: { url: string; init?: RequestInit }[];
	let queuePostBody: unknown;
	let binariesPostBody: unknown;
	let binariesPostResponse: { status: number; body: unknown };
	/** Seed for `GET /api/v1/downloads` — the whole legacy queue,
	 * unfiltered by state, mirroring `DownloadsController.ListDownloads`
	 * (review round 2 finding C: a terminal legacy row for an artifact must
	 * not block that artifact's fresh binaries-download "queued" badge). */
	let legacyQueueSeed: unknown[];
	let pullPostCount: number;
	let pullStatus: CatalogPullStatus;
	let pullPostResponse: { status: number; body: unknown };

	function installFetchMock(
		role: string,
		initialPullStatus: CatalogPullStatus = READY_PULL_STATUS,
		artifacts: CatalogArtifact[] = ARTIFACTS,
	) {
		fetchCalls = [];
		sse = createDriveableSse();
		pullPostCount = 0;
		pullStatus = initialPullStatus;
		pullPostResponse = { status: 202, body: { run_id: "pull-run-1", job_id: "pull-job-1" } };
		binariesPostResponse = { status: 202, body: { run_id: "bin-run-1", depot_artifact_ids: [] } };
		legacyQueueSeed = [];
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			fetchCalls.push({ url, init });

			if (url.startsWith("/api/v1/events") || /^\/api\/v1\/runs\/[^/]+\/events/.test(url)) {
				// Two independent SSE consumers share the one driveable stream in
				// this mock: useDownloadQueue.ts's global `/events`, and
				// useCatalogPull.ts's per-run `/runs/{run_id}/events` (same
				// per-run convention useCredentialTest.ts already uses) — both
				// resolve to the same `sse` fixture so a single `deliver(...)`
				// frame reaches whichever hook is listening for it.
				return sse.response;
			}
			if (url.startsWith("/api/v1/catalog/artifacts")) {
				// The real CatalogController.ListArtifacts returns a bare array
				// (`return Ok(items...)`), not an envelope with index_synced_at —
				// see catalog.ts's fetchCatalogArtifacts doc comment (issue #468
				// found the mismatch live). Mocking the real shape here, paged the
				// same way the real backend pages (issue #796 finding 1).
				return pagedArtifactsResponse(url, artifacts);
			}
			if (url === "/api/v1/catalog/pull" && (!init || init.method === undefined || init.method === "GET")) {
				return jsonResponse(pullStatus);
			}
			if (url === "/api/v1/catalog/pull" && init?.method === "POST") {
				pullPostCount += 1;
				return jsonResponse(pullPostResponse.body, pullPostResponse.status);
			}
			if (url === "/api/v1/downloads" && (!init || init.method === undefined || init.method === "GET")) {
				return jsonResponse(legacyQueueSeed);
			}
			if (url === "/api/v1/downloads" && init?.method === "POST") {
				queuePostBody = JSON.parse(init.body as string);
				return jsonResponse({ run_id: "run-1", job_ids: ["job-1", "job-2"] });
			}
			if (url === "/api/v1/downloads/binaries" && init?.method === "POST") {
				binariesPostBody = JSON.parse(init.body as string);
				return jsonResponse(binariesPostResponse.body, binariesPostResponse.status);
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
		binariesPostBody = undefined;
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

	it("selecting rows shows the sticky footer and the legacy path still queues N downloads via POST /downloads", async () => {
		installFetchMock("Operator");
		await mount();

		fireEvent.click(screen.getByLabelText("Select VCF-Installer-5.2.1.iso"));
		fireEvent.click(screen.getByLabelText("Select ESXi-8.0U3-patch.zip"));

		expect(screen.getByText("Legacy download (UMDS-only) — 2")).toBeInTheDocument();

		fireEvent.click(screen.getByText("Legacy download (UMDS-only) — 2"));

		await waitFor(() => expect(queuePostBody).toEqual({ artifact_ids: ["art-1", "art-2"] }));
	});

	it("issue #1487: the new Download action queues the selection via POST /downloads/binaries", async () => {
		installFetchMock("Operator");
		await mount();
		binariesPostResponse = { status: 202, body: { run_id: "bin-run-1", depot_artifact_ids: ["art-1", "art-2"] } };

		fireEvent.click(screen.getByLabelText("Select VCF-Installer-5.2.1.iso"));
		fireEvent.click(screen.getByLabelText("Select ESXi-8.0U3-patch.zip"));

		const button = screen.getByText("Download 2");
		expect(button).toBeInTheDocument();
		fireEvent.click(button);

		await waitFor(() => expect(binariesPostBody).toEqual({ depot_artifact_ids: ["art-1", "art-2"] }));
		// Optimistic: the selection clears on success without waiting for SSE.
		await waitFor(() => expect(screen.queryByText("Download 2")).not.toBeInTheDocument());
	});

	it("issue #1487 finding 1: a successful Download surfaces the run id from the response and marks the rows queued", async () => {
		installFetchMock("Operator");
		await mount();
		binariesPostResponse = { status: 202, body: { run_id: "bin-run-7", depot_artifact_ids: ["art-1", "art-2"] } };

		fireEvent.click(screen.getByLabelText("Select VCF-Installer-5.2.1.iso"));
		fireEvent.click(screen.getByLabelText("Select ESXi-8.0U3-patch.zip"));
		fireEvent.click(screen.getByText("Download 2"));

		await waitFor(() => expect(binariesPostBody).toEqual({ depot_artifact_ids: ["art-1", "art-2"] }));

		// The run notice renders with the id straight from the mocked response,
		// and links to the Live Jobs run view.
		await waitFor(() => expect(screen.getByText(/Queued run bin-run-7/)).toBeInTheDocument());
		expect(screen.getByText("View in Live Jobs")).toHaveAttribute("href", "/live-jobs?run=bin-run-7");

		// Both previously-selected rows now show "queued" — client-side, from
		// this response, not a re-fetch (the backend does not touch
		// depot_artifacts yet — issue #1482).
		expect(screen.getAllByTitle("queued").length).toBe(2);
	});

	it("review round 2 finding C: a fresh Download still shows queued for an artifact with a terminal legacy GET /downloads row", async () => {
		installFetchMock("Operator");
		// A prior legacy download of this same artifact left a TERMINAL row in
		// GET /downloads (DownloadsController.ListDownloads lists the whole
		// queue, unfiltered by state) — this must not block the fresh
		// binaries-download enqueue's own "queued" badge for the same artifact.
		legacyQueueSeed = [
			{
				id: "q-legacy-1",
				artifact_id: "art-1",
				job_id: "job-legacy-1",
				run_id: "run-legacy-1",
				state: "verified",
				progress_percent: 100,
				rate_bytes_per_sec: null,
				eta_seconds: null,
				retries: 0,
			},
		];
		await mount();
		await waitFor(() => expect(screen.getByTitle("verified")).toBeInTheDocument());

		binariesPostResponse = { status: 202, body: { run_id: "bin-run-8", depot_artifact_ids: ["art-1"] } };
		fireEvent.click(screen.getByLabelText("Select VCF-Installer-5.2.1.iso"));
		fireEvent.click(screen.getByText("Download 1"));

		await waitFor(() => expect(binariesPostBody).toEqual({ depot_artifact_ids: ["art-1"] }));
		await waitFor(() => expect(screen.getByTitle("queued")).toBeInTheDocument());
		expect(screen.queryByTitle("verified")).not.toBeInTheDocument();
	});

	it("issue #1487 finding 1: an errored Download leaves nothing marked queued and no run notice", async () => {
		installFetchMock("Operator");
		await mount();
		binariesPostResponse = {
			status: 403,
			body: { error: { code: "forbidden", message: "Operator or Admin role required." } },
		};

		fireEvent.click(screen.getByLabelText("Select VCF-Installer-5.2.1.iso"));
		fireEvent.click(screen.getByText("Download 1"));

		await waitFor(() => expect(screen.getByText("Operator or Admin role required.")).toBeInTheDocument());
		expect(screen.queryByText(/Queued run/)).not.toBeInTheDocument();
		expect(screen.queryByTitle("queued")).not.toBeInTheDocument();
	});

	it("issue #1487 finding 1: the run notice and queued badges clear once the run reaches a terminal state", async () => {
		installFetchMock("Operator");
		await mount();
		binariesPostResponse = { status: 202, body: { run_id: "bin-run-9", depot_artifact_ids: ["art-1"] } };

		fireEvent.click(screen.getByLabelText("Select VCF-Installer-5.2.1.iso"));
		fireEvent.click(screen.getByText("Download 1"));

		await waitFor(() => expect(screen.getByText(/Queued run bin-run-9/)).toBeInTheDocument());
		expect(screen.getByTitle("queued")).toBeInTheDocument();

		await deliver(
			frame({
				seq: 1,
				ts: "2026-08-08T12:00:00Z",
				type: "run.progress",
				run_id: "bin-run-9",
				data: { state: "completed", completed_count: 1 },
			}),
		);

		await waitFor(() => expect(screen.queryByText(/Queued run bin-run-9/)).not.toBeInTheDocument());
		expect(screen.queryByTitle("queued")).not.toBeInTheDocument();
	});

	it("issue #1487 finding 3: a stale Download error clears when the selection changes", async () => {
		installFetchMock("Operator");
		await mount();
		binariesPostResponse = {
			status: 403,
			body: { error: { code: "forbidden", message: "Operator or Admin role required." } },
		};

		fireEvent.click(screen.getByLabelText("Select VCF-Installer-5.2.1.iso"));
		fireEvent.click(screen.getByText("Download 1"));
		await waitFor(() => expect(screen.getByText("Operator or Admin role required.")).toBeInTheDocument());

		fireEvent.click(screen.getByText("Clear"));
		fireEvent.click(screen.getByLabelText("Select ESXi-8.0U3-patch.zip"));

		expect(screen.queryByText("Operator or Admin role required.")).not.toBeInTheDocument();
	});

	it("issue #1487: the Download action is disabled with a reason below Operator, same as the legacy path", async () => {
		installFetchMock("Cyber");
		await mount();

		fireEvent.click(screen.getByLabelText("Select VCF-Installer-5.2.1.iso"));

		const download = screen.getByText("Download 1");
		expect(download).toBeDisabled();
		expect(download).toHaveAttribute("title", expect.stringContaining("Requires Operator"));

		const legacy = screen.getByText("Legacy download (UMDS-only) — 1");
		expect(legacy).toBeDisabled();
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

	it("disables the legacy queue action with a reason below Operator", async () => {
		installFetchMock("Cyber");
		await mount();

		fireEvent.click(screen.getByLabelText("Select VCF-Installer-5.2.1.iso"));

		const button = screen.getByText("Legacy download (UMDS-only) — 1");
		expect(button).toBeDisabled();
		expect(button).toHaveAttribute("title", expect.stringContaining("Requires Operator"));
	});

	it("does not show the retry affordance for a role below Operator", async () => {
		installFetchMock("Cyber");
		await mount();

		const row = screen.getByTitle("failed — checksum mismatch").closest("tr")!;
		expect(within(row).queryByRole("button", { name: "Retry" })).not.toBeInTheDocument();
	});

	it("separates local re-index from the vendor pull action", async () => {
		installFetchMock("Admin");
		await mount();

		expect(screen.getByText("Local re-index")).toBeInTheDocument();
		expect(screen.getByText("Local re-index")).toHaveAttribute("title", expect.stringContaining("no Broadcom contact"));
		expect(screen.getByText("Pull vendor catalog")).toBeInTheDocument();
		expect(screen.getByText(/Contacts Broadcom via the installed download tool/)).toBeInTheDocument();
	});

	it("disables Pull vendor catalog with the server's not_ready_reason until the enrollment gate is satisfied", async () => {
		installFetchMock("Admin", NOT_READY_PULL_STATUS);
		await mount();

		await waitFor(() => expect(screen.getByText(NOT_READY_PULL_STATUS.not_ready_reason!)).toBeInTheDocument());

		const button = screen.getByText("Pull vendor catalog");
		expect(button).toBeDisabled();
		expect(button).toHaveAttribute("title", NOT_READY_PULL_STATUS.not_ready_reason);
	});

	it("disables Pull vendor catalog with a role reason below Admin even when the server reports ready", async () => {
		installFetchMock("Operator", READY_PULL_STATUS);
		await mount();

		const button = screen.getByText("Pull vendor catalog");
		await waitFor(() => expect(button).toBeDisabled());
		expect(button).toHaveAttribute("title", expect.stringContaining("Requires Admin"));
	});

	it("runs a successful pull: POST, follows job.log/job.state SSE, then shows item count and last-success", async () => {
		installFetchMock("Admin", READY_PULL_STATUS);
		await mount();

		fireEvent.click(screen.getByText("Pull vendor catalog"));

		await waitFor(() => expect(pullPostCount).toBe(1));
		expect(screen.getByText("Pulling…")).toBeInTheDocument();

		await deliver(
			frame({
				seq: 1,
				ts: "2026-08-24T12:00:00Z",
				type: "job.log",
				job_id: "pull-job-1",
				run_id: "pull-run-1",
				data: { line: "Downloading productVersionCatalog.json…" },
			}),
		);
		await waitFor(() => expect(screen.getByText("Downloading productVersionCatalog.json…")).toBeInTheDocument());

		pullStatus = {
			ready: true,
			last_attempt_at: "2026-08-24T12:00:30Z",
			last_outcome: "succeeded",
			last_success_at: "2026-08-24T12:00:30Z",
			last_success_item_count: 42,
		};

		await deliver(
			frame({
				seq: 2,
				ts: "2026-08-24T12:00:30Z",
				type: "job.state",
				job_id: "pull-job-1",
				run_id: "pull-run-1",
				data: { to: "done", note: "Indexed 42 artifact(s)." },
			}),
		);

		await waitFor(() => expect(screen.getByText("Last pull succeeded — indexed 42 item(s).")).toBeInTheDocument());
		expect(screen.queryByText("Pulling…")).not.toBeInTheDocument();
	});

	it("reports a genuine zero-item success honestly, not as a silent no-op", async () => {
		installFetchMock("Admin", READY_PULL_STATUS);
		await mount();

		fireEvent.click(screen.getByText("Pull vendor catalog"));
		await waitFor(() => expect(pullPostCount).toBe(1));

		pullStatus = {
			ready: true,
			last_attempt_at: "2026-08-24T12:00:30Z",
			last_outcome: "succeeded",
			last_success_at: "2026-08-24T12:00:30Z",
			last_success_item_count: 0,
		};

		await deliver(
			frame({
				seq: 1,
				ts: "2026-08-24T12:00:30Z",
				type: "job.state",
				job_id: "pull-job-1",
				run_id: "pull-run-1",
				data: { to: "done" },
			}),
		);

		await waitFor(() =>
			expect(screen.getByText("Last pull succeeded — the vendor catalog reported 0 items.")).toBeInTheDocument(),
		);
	});

	it("surfaces a failed pull's reason and allows retry via the same action", async () => {
		installFetchMock("Admin", READY_PULL_STATUS);
		await mount();

		fireEvent.click(screen.getByText("Pull vendor catalog"));
		await waitFor(() => expect(pullPostCount).toBe(1));

		pullStatus = {
			ready: true,
			last_attempt_at: "2026-08-24T12:00:30Z",
			last_outcome: "failed",
			last_failure_reason: "metadata download exited nonzero",
		};

		await deliver(
			frame({
				seq: 1,
				ts: "2026-08-24T12:00:30Z",
				type: "job.state",
				job_id: "pull-job-1",
				run_id: "pull-run-1",
				data: { to: "failed", note: "metadata download exited nonzero" },
			}),
		);

		await waitFor(() =>
			expect(screen.getByText("Last pull failed: metadata download exited nonzero")).toBeInTheDocument(),
		);

		// Retry is simply clicking the same action again.
		pullStatus = { ready: true };
		fireEvent.click(screen.getByText("Pull vendor catalog"));
		await waitFor(() => expect(pullPostCount).toBe(2));
	});

	it("surfaces a 409 catalog_pull_not_ready if the readiness gate is raced between load and click", async () => {
		installFetchMock("Admin", READY_PULL_STATUS);
		await mount();

		pullPostResponse = {
			status: 409,
			body: { error: { code: "catalog_pull_not_ready", message: "Connected catalog pull is disabled until enrollment is validated." } },
		};

		fireEvent.click(screen.getByText("Pull vendor catalog"));

		await waitFor(() =>
			expect(screen.getByText("Connected catalog pull is disabled until enrollment is validated.")).toBeInTheDocument(),
		);
		expect(screen.queryByText("Pulling…")).not.toBeInTheDocument();
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

	it("groups artifacts by product with friendly names, catalog keys, and version counts", async () => {
		installFetchMock("Operator", READY_PULL_STATUS, dominantVkrArtifacts(1));
		render(
			<AuthProvider>
				<SystemProvider>
					<DownloadCatalogScreen />
				</SystemProvider>
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("VCSA-8.0U3.iso")).toBeInTheDocument());

		expect(screen.getByText("vCenter Server")).toBeInTheDocument();
		expect(screen.getAllByText("VCENTER").length).toBeGreaterThan(0);
		expect(screen.getByText("ESXi")).toBeInTheDocument();
		expect(screen.getAllByText("ESX_HOST").length).toBeGreaterThan(0);
		expect(screen.getAllByText("1 version · 1 artifact").length).toBe(3);
	});

	/** Issue #1588 AC 2, review round 1 finding 5: the test above uses a fixture
	 * with `product: "VCENTER"` -> friendly name "vCenter Server", a case where
	 * the humanised form actually differs from the raw key — unlike this
	 * suite's default `ARTIFACTS` fixture, whose raw key ("VCF Installer")
	 * already equals its own friendly form, so a bare
	 * `getAllByText("VCF Installer")` there would pass even if
	 * `friendlyProductName` were never called at all (the review's proof: the
	 * raw PRODUCT filter cell alone renders that text). This asserts the
	 * humanised name specifically inside the rendered group header AND inside
	 * the product filter dropdown's option text — the two render sites
	 * `friendlyProductName` actually feeds — rather than a same-string
	 * coincidence anywhere on the page. */
	it("renders the humanised product name in the group header and the product filter option (issue #1588 AC 2)", async () => {
		installFetchMock("Operator", READY_PULL_STATUS, dominantVkrArtifacts(1));
		const { container } = render(
			<AuthProvider>
				<SystemProvider>
					<DownloadCatalogScreen />
				</SystemProvider>
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("VCSA-8.0U3.iso")).toBeInTheDocument());

		const groupHeader = container.querySelector(".product-group__header");
		expect(groupHeader).not.toBeNull();
		expect(within(groupHeader as HTMLElement).getByText("vCenter Server")).toBeInTheDocument();
		// The raw catalog key is a sibling element in the same header, never the
		// humanised text itself.
		expect(within(groupHeader as HTMLElement).getByText("VCENTER")).toBeInTheDocument();

		const productFilter = screen.getByLabelText("Filter by product") as HTMLSelectElement;
		const vcenterOption = within(productFilter).getByRole("option", { name: "vCenter Server (VCENTER)" });
		expect(vcenterOption).toBeInTheDocument();
	});

	it("filters to just the Kubernetes-stack products via the type filter", async () => {
		installFetchMock("Operator", READY_PULL_STATUS, dominantVkrArtifacts(3));
		render(
			<AuthProvider>
				<SystemProvider>
					<DownloadCatalogScreen />
				</SystemProvider>
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("vCenter Server")).toBeInTheDocument());

		fireEvent.change(screen.getByLabelText("Filter by type"), { target: { value: "kubernetes" } });

		await waitFor(() => expect(screen.queryByText("vCenter Server")).not.toBeInTheDocument());
		expect(screen.getByText("VKR (Kubernetes Release)")).toBeInTheDocument();

		fireEvent.change(screen.getByLabelText("Filter by type"), { target: { value: "core" } });
		await waitFor(() => expect(screen.getByText("vCenter Server")).toBeInTheDocument());
		expect(screen.queryByText("VKR (Kubernetes Release)")).not.toBeInTheDocument();
	});

	it("dominant-product case: collapses the 433-strong VKR group by default without hiding core products", async () => {
		installFetchMock("Operator", READY_PULL_STATUS, dominantVkrArtifacts(40));
		render(
			<AuthProvider>
				<SystemProvider>
					<DownloadCatalogScreen />
				</SystemProvider>
			</AuthProvider>,
		);

		// Core-infrastructure products are visible without any expand click.
		await waitFor(() => expect(screen.getByText("VCSA-8.0U3.iso")).toBeInTheDocument());
		expect(screen.getByText("ESXi-8.0U3.zip")).toBeInTheDocument();

		// The Kubernetes group header shows its true count but its rows are
		// not rendered until expanded.
		const kubernetesHeader = screen.getByText("VKR (Kubernetes Release)").closest("button")!;
		expect(within(kubernetesHeader).getByText("40 versions · 40 artifacts")).toBeInTheDocument();
		expect(kubernetesHeader).toHaveAttribute("aria-expanded", "false");
		expect(screen.queryByText("vkr-release-0.tar")).not.toBeInTheDocument();

		fireEvent.click(kubernetesHeader);
		await waitFor(() => expect(screen.getByText("vkr-release-0.tar")).toBeInTheDocument());
		expect(kubernetesHeader).toHaveAttribute("aria-expanded", "true");
	});

	it("fetches every page of a >200-row catalog (issue #796 finding 1) so grouping and counts describe the whole catalog, not the first page", async () => {
		// 302 rows total (2 core + 300 VKR) against the client's 200-row page
		// size — this can only pass if fetchCatalogArtifacts pages past the
		// first response instead of trusting a single GET.
		installFetchMock("Operator", READY_PULL_STATUS, dominantVkrArtifacts(300));
		render(
			<AuthProvider>
				<SystemProvider>
					<DownloadCatalogScreen />
				</SystemProvider>
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("VCSA-8.0U3.iso")).toBeInTheDocument());

		// The Kubernetes group's count reflects all 300 VKR rows, not just
		// however many landed on the first 200-row page.
		const kubernetesHeader = await waitFor(() => screen.getByText("VKR (Kubernetes Release)").closest("button")!);
		await waitFor(() => expect(within(kubernetesHeader).getByText("300 versions · 300 artifacts")).toBeInTheDocument());

		// More than one page was actually requested, at increasing offsets,
		// each capped at the backend's 200-row MaxLimit.
		const artifactCalls = fetchCalls.filter((c) => c.url.startsWith("/api/v1/catalog/artifacts"));
		const offsets = artifactCalls.map((c) => Number(new URL(c.url, "http://localhost").searchParams.get("offset")));
		expect(artifactCalls.length).toBeGreaterThan(1);
		expect(offsets).toContain(0);
		expect(offsets).toContain(200);
		for (const call of artifactCalls) {
			expect(new URL(call.url, "http://localhost").searchParams.get("limit")).toBe("200");
		}
	});

	/**
	 * Issue #1592: replaces the mock's `/catalog/artifacts` branch with one
	 * whose promises the test resolves by hand, so a superseded walk can be
	 * made to resolve strictly after the walk that superseded it — the exact
	 * race the stale-response guard exists for. Also records every request's
	 * `AbortSignal` so a test can assert a superseded walk's own signal was
	 * aborted, independent of whether/when its promise ever resolves.
	 */
	function installDeferredArtifactsFetchMock(): {
		deferred: Array<(response: Response) => void>;
		signals: AbortSignal[];
	} {
		const deferred: Array<(response: Response) => void> = [];
		const signals: AbortSignal[] = [];
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			fetchCalls.push({ url, init });
			if (url.startsWith("/api/v1/catalog/artifacts")) {
				if (init?.signal) {
					signals.push(init.signal as AbortSignal);
				}
				return new Promise<Response>((resolve) => {
					deferred.push(resolve);
				});
			}
			if (url.startsWith("/api/v1/events") || /^\/api\/v1\/runs\/[^/]+\/events/.test(url)) {
				return sse.response;
			}
			if (url === "/api/v1/downloads" && (!init || init.method === undefined || init.method === "GET")) {
				return jsonResponse([]);
			}
			throw new Error(`unexpected fetch (deferred artifacts mock): ${url}`);
		}) as unknown as typeof fetch;
		return { deferred, signals };
	}

	it("issue #1592: a slower earlier catalog walk resolving after a newer one does not overwrite state", async () => {
		installFetchMock("Operator");
		await mount();

		const { deferred } = installDeferredArtifactsFetchMock();

		fireEvent.change(screen.getByLabelText("Filter by product"), { target: { value: "VCF Installer" } });
		await waitFor(() => expect(deferred.length).toBe(1));

		fireEvent.change(screen.getByLabelText("Filter by product"), { target: { value: "ESXi" } });
		await waitFor(() => expect(deferred.length).toBe(2));

		// Resolve the NEWER (second) request first...
		await act(async () => {
			deferred[1](pagedArtifactsResponse("/api/v1/catalog/artifacts?product=ESXi&limit=200&offset=0", [ARTIFACTS[1]]));
		});
		await waitFor(() => expect(screen.getByText("ESXi-8.0U3-patch.zip")).toBeInTheDocument());

		// ...then the OLDER, superseded request, whose result must be discarded.
		await act(async () => {
			deferred[0](
				pagedArtifactsResponse("/api/v1/catalog/artifacts?product=VCF+Installer&limit=200&offset=0", [ARTIFACTS[0]]),
			);
			await new Promise((resolve) => setTimeout(resolve, 0));
		});

		expect(screen.getByText("ESXi-8.0U3-patch.zip")).toBeInTheDocument();
		expect(screen.queryByText("VCF-Installer-5.2.1.iso")).not.toBeInTheDocument();
	});

	it("issue #1592: a superseded walk's request signal is aborted by the next load", async () => {
		installFetchMock("Operator");
		await mount();

		const { deferred, signals } = installDeferredArtifactsFetchMock();

		fireEvent.change(screen.getByLabelText("Filter by product"), { target: { value: "VCF Installer" } });
		await waitFor(() => expect(signals.length).toBe(1));
		expect(signals[0].aborted).toBe(false);

		fireEvent.change(screen.getByLabelText("Filter by product"), { target: { value: "ESXi" } });
		await waitFor(() => expect(signals.length).toBe(2));

		expect(signals[0].aborted).toBe(true);
		expect(signals[1].aborted).toBe(false);

		// Resolve both so no promise/act warning leaks past this test.
		await act(async () => {
			deferred[0](
				pagedArtifactsResponse("/api/v1/catalog/artifacts?product=VCF+Installer&limit=200&offset=0", [ARTIFACTS[0]]),
			);
			deferred[1](pagedArtifactsResponse("/api/v1/catalog/artifacts?product=ESXi&limit=200&offset=0", [ARTIFACTS[1]]));
		});
	});

	it("issue #1592: changing only the search filter issues no additional catalog fetches", async () => {
		installFetchMock("Operator");
		await mount();

		const countBefore = fetchCalls.filter((c) => c.url.startsWith("/api/v1/catalog/artifacts")).length;

		fireEvent.change(screen.getByLabelText("Search artifacts"), { target: { value: "ESXi" } });
		await waitFor(() => expect(screen.queryByText("VCF-Installer-5.2.1.iso")).not.toBeInTheDocument());

		const countAfter = fetchCalls.filter((c) => c.url.startsWith("/api/v1/catalog/artifacts")).length;
		expect(countAfter).toBe(countBefore);
	});

	it("issue #1780: a failing GET /downloads seed renders an inline notice instead of an empty queue, without throwing", async () => {
		fetchCalls = [];
		sse = createDriveableSse();
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			fetchCalls.push({ url, init });
			if (url.startsWith("/api/v1/events") || /^\/api\/v1\/runs\/[^/]+\/events/.test(url)) return sse.response;
			if (url.startsWith("/api/v1/catalog/artifacts")) return pagedArtifactsResponse(url, ARTIFACTS);
			if (url === "/api/v1/catalog/pull" && (!init || init.method === undefined || init.method === "GET")) {
				return jsonResponse(READY_PULL_STATUS);
			}
			if (url === "/api/v1/downloads" && (!init || init.method === undefined || init.method === "GET")) {
				return jsonResponse({ error: { code: "internal", message: "Could not load the download queue." } }, 500);
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

		await mount();
		await waitFor(() => expect(screen.getByText("Could not load the download queue.")).toBeInTheDocument());
		expect(screen.queryByText("No active downloads.")).not.toBeInTheDocument();
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
