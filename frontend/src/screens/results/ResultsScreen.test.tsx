/**
 * ResultsScreen — issue #27. Covers: run list rendering + selection, KPI
 * tiles, the per-target artifacts table with severity pills, the
 * "not available yet" fallback when GET /runs/{id}/artifacts 404s, the
 * Remediate Admin gate, and the AC1 severity non-truncation guarantee
 * (design-brief "Layout Rules Learned the Hard Way" #4).
 */
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { ResultsScreen } from "./ResultsScreen";

const RUN_LIST = [
	{
		id: "RUN-2026-0802-0412",
		run_type: "scan",
		state: "completed",
		scope: JSON.stringify({ site_id: "Alpha Enclave" }),
		initiated_by: "j.moreno",
		created_at: "2026-08-02T04:12:00Z",
		started_at: "2026-08-02T04:12:00Z",
		completed_at: "2026-08-02T04:23:39Z",
		job_count: 2,
		job_count_queued: 0,
		job_count_running: 0,
		job_count_completed: 2,
		job_count_failed: 0,
		job_count_blocked: 0,
	},
];

const RUN_JOBS = [
	{
		id: "job-1",
		job_type: "scan",
		target_id: "target-1",
		target_name: "esxi-01.example.internal",
		state: "uploaded",
		stage: null,
		attempt_count: 1,
		created_at: "2026-08-02T04:12:00Z",
		started_at: "2026-08-02T04:12:05Z",
		finished_at: "2026-08-02T04:20:00Z",
	},
	{
		id: "job-2",
		job_type: "scan",
		target_id: "target-2",
		target_name: "esxi-02.example.internal",
		state: "failed",
		stage: null,
		attempt_count: 1,
		created_at: "2026-08-02T04:12:00Z",
		started_at: "2026-08-02T04:12:05Z",
		finished_at: "2026-08-02T04:19:00Z",
	},
];

const RUN_ARTIFACTS = [
	{
		target: "esxi-01.example.internal",
		benchmark: "VMware_vSphere_8.0_ESXi_STIG_V2R1",
		catIOpen: 1,
		catIIOpen: 8,
		catIIIOpen: 11,
		artifactKinds: ["ckl", "hdf"],
		uploadStatus: "uploaded",
	},
];

function jsonResponse(body: unknown, headers: Record<string, string> = {}): Response {
	return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json", ...headers } });
}

function installFetchMock(options: { artifactsAvailable?: boolean } = {}) {
	const artifactsAvailable = options.artifactsAvailable ?? true;
	globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
		const url = typeof input === "string" ? input : input.toString();

		if (url.startsWith("/api/v1/runs?")) {
			return jsonResponse(RUN_LIST, { "X-Total-Count": String(RUN_LIST.length) });
		}
		if (url === "/api/v1/runs/RUN-2026-0802-0412") {
			return jsonResponse(RUN_LIST[0]);
		}
		if (url === "/api/v1/runs/RUN-2026-0802-0412/jobs") {
			return jsonResponse(RUN_JOBS);
		}
		if (url === "/api/v1/runs/RUN-2026-0802-0412/artifacts") {
			return artifactsAvailable ? jsonResponse(RUN_ARTIFACTS) : new Response("not found", { status: 404 });
		}
		if (url.startsWith("/api/v1/config-docs/resolve")) {
			return jsonResponse([]);
		}
		if (url === "/api/v1/auth/me") {
			return jsonResponse({ username: "j.moreno", role: "Admin" });
		}
		throw new Error(`Unhandled fetch in test: ${url}`);
	}) as unknown as typeof fetch;
}

function renderWithAuth(role: "Viewer" | "Cyber" | "Operator" | "Admin" = "Admin") {
	sessionStorage.setItem(
		"waypoint.session",
		JSON.stringify({
			token: "tok",
			username: "j.moreno",
			role,
			expiresAt: new Date(Date.now() + 3600_000).toISOString(),
		}),
	);
	return render(
		<AuthProvider>
			<ResultsScreen />
		</AuthProvider>,
	);
}

describe("ResultsScreen", () => {
	beforeEach(() => {
		sessionStorage.clear();
	});

	afterEach(() => {
		vi.restoreAllMocks();
	});

	it("renders the run list and auto-selects the first run's detail", async () => {
		installFetchMock();
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("RUN-2026-0802-0412")).toBeInTheDocument());
		await waitFor(() => expect(screen.getAllByText("RUN-2026-0802-0412").length).toBeGreaterThan(1));
		expect(screen.getByText("Export CKL bundle")).toBeInTheDocument();
	});

	it("renders KPI tiles from the run and artifacts data", async () => {
		installFetchMock();
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("COMPLIANCE")).toBeInTheDocument());
		expect(screen.getByText("CAT I OPEN")).toBeInTheDocument();
		expect(screen.getByText("CAT II OPEN")).toBeInTheDocument();
		expect(screen.getByText("CAT III OPEN")).toBeInTheDocument();
		await waitFor(() => {
			const catITile = screen.getByText("CAT I OPEN").closest(".results__kpi-tile");
			expect(catITile).not.toBeNull();
			expect(within(catITile as HTMLElement).getByText("1")).toBeInTheDocument();
		});
	});

	it("renders the per-target artifacts table with full severity labels, never abbreviated", async () => {
		installFetchMock();
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("PER-TARGET ARTIFACTS")).toBeInTheDocument());

		// AC1 / design-brief layout rule 4: "Never let a status label truncate
		// silently — 'CAT II' clipped to 'CAT I' is a correctness bug." Assert
		// the FULL text is present for all three severities, not a bare numeral.
		await waitFor(() => {
			expect(screen.getByTitle("CAT I open: 1")).toHaveTextContent("CAT I");
			expect(screen.getByTitle("CAT II open: 8")).toHaveTextContent("CAT II");
			expect(screen.getByTitle("CAT III open: 11")).toHaveTextContent("CAT III");
		});

		// The CAT II pill's accessible text must contain "CAT II" in full — a
		// clip to "CAT I" (the literal bug the design brief calls out) would
		// make this assertion fail while a "CAT I"-only assertion would not
		// catch it, which is why both full strings are checked explicitly.
		const catTwoPill = screen.getByTitle("CAT II open: 8");
		expect(catTwoPill.textContent).toContain("CAT II");
		expect(catTwoPill.textContent).not.toBe("CAT I");

		// No severity pill in this table may declare an ellipsis/hidden-overflow
		// treatment on itself — that combination is exactly what produces a
		// silent clip. (Upload-status pills are a different component and are
		// allowed to ellipsis per the design brief; severity pills are not.)
		const severityPills = [
			screen.getByTitle("CAT I open: 1"),
			screen.getByTitle("CAT II open: 8"),
			screen.getByTitle("CAT III open: 11"),
		];
		for (const pill of severityPills) {
			expect(pill.className).toContain("results__severity");
			const style = window.getComputedStyle(pill);
			expect(style.textOverflow).not.toBe("ellipsis");
			// Full text (not a bare Roman numeral) must be present in the DOM.
			expect(pill.textContent).toMatch(/^CAT (I|II|III) \d+$/);
		}
	});

	it("shows a graceful fallback when GET /runs/{id}/artifacts is not yet implemented", async () => {
		installFetchMock({ artifactsAvailable: false });
		renderWithAuth();

		await waitFor(() => expect(screen.getByText(/not available yet/)).toBeInTheDocument());
	});

	it("gates the Remediate button to Admin and stubs it disabled regardless of role", async () => {
		installFetchMock();
		renderWithAuth("Cyber");

		await waitFor(() => expect(screen.getByText("Remediate findings…")).toBeInTheDocument());
		const button = screen.getByText("Remediate findings…") as HTMLButtonElement;
		expect(button).toBeDisabled();
		expect(button.title).toMatch(/Admin/);
		expect(button.title).toMatch(/M4/);
	});

	it("keeps the Remediate button disabled even for Admin (stubbed until M4)", async () => {
		installFetchMock();
		renderWithAuth("Admin");

		await waitFor(() => expect(screen.getByText("Remediate findings…")).toBeInTheDocument());
		const button = screen.getByText("Remediate findings…") as HTMLButtonElement;
		expect(button).toBeDisabled();
	});

	it("renders the STIG Manager retry stub as disabled, referencing issue #25", async () => {
		installFetchMock();
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("Retry failed uploads")).toBeInTheDocument());
		const retry = screen.getByText("Retry failed uploads") as HTMLButtonElement;
		expect(retry).toBeDisabled();
		expect(retry.title).toMatch(/#25/);
	});

	it("renders the Open in Benchmarks stub as disabled", async () => {
		installFetchMock();
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("Open in Benchmarks")).toBeInTheDocument());
		const openBenchmarks = screen.getByText("Open in Benchmarks") as HTMLButtonElement;
		expect(openBenchmarks).toBeDisabled();
	});

	it("filters the run list by search term", async () => {
		installFetchMock();
		renderWithAuth();

		await waitFor(() => expect(screen.getByLabelText("Search runs")).toBeInTheDocument());
		const search = screen.getByLabelText("Search runs") as HTMLInputElement;
		const runList = document.querySelector(".results__run-list") as HTMLElement;
		expect(within(runList).getByText("RUN-2026-0802-0412")).toBeInTheDocument();

		fireEvent.change(search, { target: { value: "no-such-run" } });
		expect(within(runList).queryByText("RUN-2026-0802-0412")).not.toBeInTheDocument();
		expect(within(runList).getByText(/No runs match/)).toBeInTheDocument();
	});
});
