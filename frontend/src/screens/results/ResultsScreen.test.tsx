/**
 * ResultsScreen — issue #27. Covers: run list rendering + selection, KPI
 * tiles, the per-target artifacts table with severity pills, the load
 * failure fallback when GET /runs/{id}/artifacts errors, the Remediate
 * Admin gate, and the AC1 severity non-truncation guarantee (design-brief
 * "Layout Rules Learned the Hard Way" #4). Issue #307 regressions (snake_case
 * wire shapes, counts_available) and issue #335 (persisted at-scan-time
 * attestations-applied ledger, PR #336 — replacing the old live-resolution
 * shape) are grouped near the bottom of the describe block.
 */
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { RouterProvider } from "../../lib/router";
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

/** Snake_case, matching `RunArtifactResponse` (RunContracts.cs) exactly —
 * the shape the backend actually emits (issue #307). */
const RUN_ARTIFACTS = [
	{
		job_id: "job-1",
		target: "esxi-01.example.internal",
		benchmark: "VMware_vSphere_8.0_ESXi_STIG_V2R1",
		counts_available: true,
		cat_i_open: 1,
		cat_ii_open: 8,
		cat_iii_open: 11,
		artifact_kinds: ["ckl", "hdf"],
		upload_status: "uploaded",
	},
];

/** A row where the HDF was absent/unparseable: `counts_available:false` and
 * the CAT count properties are omitted from the payload entirely (server
 * `WhenWritingNull` behavior), not present as `0`. */
const RUN_ARTIFACTS_UNCOUNTABLE = [
	{
		job_id: "job-2",
		target: "esxi-02.example.internal",
		benchmark: "VMware_vSphere_8.0_ESXi_STIG_V2R1",
		counts_available: false,
		artifact_kinds: [],
		upload_status: "not-uploaded",
	},
];

/** `AppliedAttestationResponse` shape (RunContracts.cs, wire updated by
 * issue #306/PR #336) — persisted at-scan-time snapshot: genuine `applied_at`,
 * no `derivation`/`resolved_at`, layer:ref `scope` form unchanged. */
const ATTESTATIONS_APPLIED = [
	{
		control: "attestation-profile-a",
		scope: "target:11111111-1111-1111-1111-111111111111",
		coverage: "full",
		justification: "Compensating control documented in POA&M #42.",
		author: "j.moreno",
		version: 3,
		applied_at: "2026-08-02T04:15:00Z",
		attestation_updated_at: "2026-08-01T10:00:00Z",
		expired: false,
	},
];

/** An expired snapshot — the attestation had already lapsed at scan time
 * (`applied: false, expired: true` server-side, RunContracts.cs), still
 * recorded and reported as a row here rather than omitted. */
const ATTESTATIONS_APPLIED_EXPIRED = [
	{
		control: "attestation-profile-b",
		scope: "global",
		coverage: "full",
		justification: "Waiver lapsed before this scan ran.",
		author: "j.moreno",
		version: 2,
		applied_at: "2026-08-02T04:16:00Z",
		attestation_updated_at: "2026-07-01T09:00:00Z",
		expired: true,
	},
];

function jsonResponse(body: unknown, headers: Record<string, string> = {}): Response {
	return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json", ...headers } });
}

/** `RunResultRollupResponse` (RunContracts.cs) — issue #745 remainder. Two
 * status buckets (completed, execution_error) matching RUN_JOBS' one
 * uploaded + one failed job, with a coverage gap (planned 3, resulted 2) so
 * the honest "not yet resulted" ledger line has something to show. */
const COMPONENT_RESULTS_ROLLUP = {
	run_id: "RUN-2026-0802-0412",
	planned_component_count: 3,
	by_status: [
		{
			status: "completed",
			component_count: 1,
			cat_i_open: 1,
			cat_ii_open: 8,
			cat_iii_open: 11,
			passed_count: 40,
			not_applicable_count: 2,
			not_reviewed_count: 0,
			skipped_count: 0,
		},
		{
			status: "execution_error",
			component_count: 1,
			cat_i_open: 0,
			cat_ii_open: 0,
			cat_iii_open: 0,
			passed_count: 0,
			not_applicable_count: 0,
			not_reviewed_count: 5,
			skipped_count: 0,
		},
	],
};

/** `UploadAttemptResponse[]` (RunContracts.cs) — issue #744 remainder. */
const UPLOAD_ATTEMPTS = [
	{
		attempt_number: 1,
		endpoint: "https://stigman.example.internal/api",
		collection: "vcf-collection",
		status: "uploaded",
		error_detail: null,
		attempted_at: "2026-08-02T04:20:00Z",
	},
];

function installFetchMock(
	options: {
		artifactsAvailable?: boolean;
		artifacts?: unknown[];
		attestationsApplied?: unknown[] | "unavailable";
		runList?: unknown[];
		componentResultsRollup?: unknown | "unavailable";
		uploadAttempts?: unknown[] | "unavailable";
	} = {},
) {
	const artifactsAvailable = options.artifactsAvailable ?? true;
	const artifacts = options.artifacts ?? RUN_ARTIFACTS;
	const attestationsApplied = options.attestationsApplied ?? ATTESTATIONS_APPLIED;
	const runList = options.runList ?? RUN_LIST;
	const componentResultsRollup = options.componentResultsRollup ?? COMPONENT_RESULTS_ROLLUP;
	const uploadAttempts = options.uploadAttempts ?? UPLOAD_ATTEMPTS;
	globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
		const url = typeof input === "string" ? input : input.toString();

		if (url.startsWith("/api/v1/runs?")) {
			return jsonResponse(runList, { "X-Total-Count": String(runList.length) });
		}
		if (url === "/api/v1/runs/RUN-2026-0802-0412") {
			return jsonResponse(RUN_LIST[0]);
		}
		if (url === "/api/v1/runs/RUN-2026-0802-0412/jobs") {
			return jsonResponse(RUN_JOBS);
		}
		if (url === "/api/v1/runs/RUN-2026-0802-0412/artifacts") {
			return artifactsAvailable ? jsonResponse(artifacts) : new Response("not found", { status: 404 });
		}
		if (url === "/api/v1/runs/RUN-2026-0802-0412/attestations-applied") {
			return attestationsApplied === "unavailable"
				? new Response("not found", { status: 404 })
				: jsonResponse(attestationsApplied);
		}
		if (url === "/api/v1/runs/RUN-2026-0802-0412/component-results/summary") {
			return componentResultsRollup === "unavailable"
				? new Response("not found", { status: 404 })
				: jsonResponse(componentResultsRollup);
		}
		if (url === "/api/v1/jobs/job-1/upload-attempts") {
			return uploadAttempts === "unavailable" ? new Response("not found", { status: 404 }) : jsonResponse(uploadAttempts);
		}
		if (url === "/api/v1/jobs/job-2/upload-attempts") {
			return jsonResponse([]);
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
			<RouterProvider>
				<ResultsScreen />
			</RouterProvider>
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

	it("shows a graceful fallback when GET /runs/{id}/artifacts fails", async () => {
		installFetchMock({ artifactsAvailable: false });
		renderWithAuth();

		await waitFor(() => expect(screen.getByText(/could not be loaded/)).toBeInTheDocument());
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

	it("counts the failed job in the Not uploaded stat (regression: label/token mismatch)", async () => {
		installFetchMock();
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("Not uploaded")).toBeInTheDocument());
		// RUN_JOBS holds one uploaded and one failed job; the failed one must
		// be counted, not silently dropped by a "not uploaded" vs "not-uploaded"
		// string-convention mismatch (round-1 review blocker on PR #300).
		const statValue = screen.getByText("Not uploaded").nextElementSibling;
		expect(statValue?.textContent).toBe("1");
	});

	it("renders the STIG Manager retry stub as disabled, referencing issue #25", async () => {
		installFetchMock();
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("Retry failed uploads")).toBeInTheDocument());
		const retry = screen.getByText("Retry failed uploads") as HTMLButtonElement;
		expect(retry).toBeDisabled();
		expect(retry.title).toMatch(/#25/);
	});

	it("enables Open in Benchmarks and navigates with profile+target when an applied attestation carries a target scope (issue #559)", async () => {
		installFetchMock();
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("Open in Benchmarks")).toBeInTheDocument());
		const openBenchmarks = screen.getByText("Open in Benchmarks") as HTMLButtonElement;
		expect(openBenchmarks).not.toBeDisabled();

		fireEvent.click(openBenchmarks);
		await waitFor(() =>
			expect(window.location.pathname + window.location.search).toBe(
				"/benchmarks?profile=attestation-profile-a&target=11111111-1111-1111-1111-111111111111",
			),
		);
	});

	it("renders Open in Benchmarks as disabled when no attestation identifiers exist for the run", async () => {
		installFetchMock({ attestationsApplied: [] });
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("Open in Benchmarks")).toBeInTheDocument());
		const openBenchmarks = screen.getByText("Open in Benchmarks") as HTMLButtonElement;
		expect(openBenchmarks).toBeDisabled();
		expect(openBenchmarks.title).toMatch(/no attestation identifiers/i);
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

	// --- Issue #307 regressions: results.ts diverged from the snake_case wire ---

	it("deserializes a snake_case artifact payload with populated counts (issue #307)", async () => {
		installFetchMock();
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("PER-TARGET ARTIFACTS")).toBeInTheDocument());

		// RUN_ARTIFACTS is snake_case (job_id, cat_i_open, artifact_kinds,
		// upload_status) exactly as RunArtifactResponse emits it. If results.ts
		// still read camelCase fields, these counts would be undefined/NaN
		// instead of the seeded 1/8/11.
		await waitFor(() => {
			expect(screen.getByTitle("CAT I open: 1")).toBeInTheDocument();
			expect(screen.getByTitle("CAT II open: 8")).toBeInTheDocument();
			expect(screen.getByTitle("CAT III open: 11")).toBeInTheDocument();
		});
		expect(screen.getByText("CKL · HDF")).toBeInTheDocument();
		expect(screen.getByText("uploaded", { selector: ".results__upload-pill" })).toBeInTheDocument();
	});

	it("renders an uncountable row (counts_available:false) as n/a, never a fabricated 0", async () => {
		installFetchMock({ artifacts: RUN_ARTIFACTS_UNCOUNTABLE });
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("PER-TARGET ARTIFACTS")).toBeInTheDocument());

		// The uncountable row has no cat_i_open/cat_ii_open/cat_iii_open
		// properties at all (server omits them) — the table must render an
		// explicit "n/a" state, never silently treat the missing count as 0
		// (which would read as a clean, compliant target on a corrupt scan).
		await waitFor(() => {
			const pill = screen.getByTitle("CAT I: not available (could not count)");
			expect(pill).toHaveTextContent("n/a");
			expect(pill.className).toContain("results__severity--na");
		});
		expect(screen.getByTitle("CAT II: not available (could not count)")).toHaveTextContent("n/a");
		expect(screen.getByTitle("CAT III: not available (could not count)")).toHaveTextContent("n/a");

		// The KPI tiles must not silently sum an uncountable row's counts as 0
		// either — same rule applied to the aggregate.
		const catITile = screen.getByText("CAT I OPEN").closest(".results__kpi-tile");
		expect(within(catITile as HTMLElement).getByText("n/a")).toBeInTheDocument();
	});

	it("shows the recorded-at-scan-time framing and applied waivers in the attestations sidebar", async () => {
		installFetchMock();
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("ATTESTATIONS APPLIED")).toBeInTheDocument());

		// The sidebar must say plainly that this is recorded, scan-time history —
		// GET /runs/{id}/attestations-applied is now a persisted ledger
		// (issue #306/PR #336), not a live re-resolution. The old #307/#299
		// "live resolution, not scan-time history" caveat must be gone.
		expect(screen.getByText(/Recorded at scan time/)).toBeInTheDocument();
		expect(screen.queryByText(/Live resolution, not scan-time history/)).not.toBeInTheDocument();
		expect(screen.queryByText(/issue #306/)).not.toBeInTheDocument();

		// The applied waiver row itself renders (control/coverage/author/version),
		// parsed from the target:{guid} scope form rather than a bare "target" union.
		await waitFor(() => expect(screen.getByText("attestation-profile-a")).toBeInTheDocument());
		expect(screen.getByText(/full · v3 · j\.moreno/)).toBeInTheDocument();
		// scope "target:{guid}" parses into a "TARGET · {guid}" pill, not a bare
		// "target" string from a stale union type.
		expect(screen.getByText(/TARGET · 11111111-1111-1111-1111-111111111111/)).toBeInTheDocument();
		// The genuine scan-time applied_at is shown, not a request-time "resolved" label.
		expect(screen.getByText(/applied/)).toBeInTheDocument();
	});

	it("renders an expired attestation snapshot row", async () => {
		installFetchMock({ attestationsApplied: ATTESTATIONS_APPLIED_EXPIRED });
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("ATTESTATIONS APPLIED")).toBeInTheDocument());
		await waitFor(() => expect(screen.getByText("attestation-profile-b")).toBeInTheDocument());
		expect(screen.getByText("EXPIRED")).toBeInTheDocument();
		expect(screen.getByText(/full · v2 · j\.moreno/)).toBeInTheDocument();
	});

	it("handles a run with no persisted attestation snapshots (empty ledger)", async () => {
		installFetchMock({ attestationsApplied: [] });
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("ATTESTATIONS APPLIED")).toBeInTheDocument());
		// An empty array (e.g. a pre-#306 run with nothing recorded) must render
		// gracefully — falls back to the expired-only view, not a crash.
		expect(screen.getByText("No expired attestations resolved for this run.")).toBeInTheDocument();
	});

	it("falls back gracefully when GET /runs/{id}/attestations-applied is unavailable", async () => {
		installFetchMock({ attestationsApplied: "unavailable" });
		renderWithAuth();

		await waitFor(() => expect(screen.getByText("ATTESTATIONS APPLIED")).toBeInTheDocument());
		// No applied rows loaded — the panel still renders without crashing and
		// falls back to its expired-only view (empty here, since RUN_ARTIFACTS'
		// single row has no matching config-doc resolution in this fixture).
		expect(screen.getByText("No expired attestations resolved for this run.")).toBeInTheDocument();
	});

	/**
	 * Issue #591 AC: "Compliance Results lists only scan/remediation-owned
	 * results". `GET /runs` is not itself filtered server-side by run_type
	 * (it returns every run) — the filter is this screen's own
	 * `useRunList.ts`/`COMPLIANCE_RUN_TYPES`, so a non-compliance run
	 * (`download`, `discover`, etc.) in the raw response must never reach the
	 * rendered list or become selectable.
	 */
	describe("Compliance-owned run type filtering (issue #591)", () => {
		const DOWNLOAD_RUN = {
			...RUN_LIST[0],
			id: "RUN-2026-0810-0000",
			run_type: "download",
			scope: JSON.stringify({ site_id: "Bravo Enclave" }),
		};
		const DISCOVER_RUN = {
			...RUN_LIST[0],
			id: "RUN-2026-0811-0000",
			run_type: "discover",
			scope: JSON.stringify({ site_id: "Charlie Enclave" }),
		};

		it("does not list a non-compliance run (download) even though GET /runs returned it", async () => {
			installFetchMock({ runList: [...RUN_LIST, DOWNLOAD_RUN] });
			renderWithAuth();

			await waitFor(() => expect(screen.getByText("RUN-2026-0802-0412")).toBeInTheDocument());
			expect(screen.queryByText("RUN-2026-0810-0000")).not.toBeInTheDocument();
			expect(screen.queryByText("Bravo Enclave")).not.toBeInTheDocument();
		});

		it("does not list a discovery run either — only scan/remediate are compliance-owned", async () => {
			installFetchMock({ runList: [...RUN_LIST, DISCOVER_RUN] });
			renderWithAuth();

			await waitFor(() => expect(screen.getByText("RUN-2026-0802-0412")).toBeInTheDocument());
			expect(screen.queryByText("RUN-2026-0811-0000")).not.toBeInTheDocument();
		});

		it("still lists a scan run normally when a non-compliance run is mixed into the same response", async () => {
			installFetchMock({ runList: [DOWNLOAD_RUN, ...RUN_LIST] });
			renderWithAuth();

			await waitFor(() => expect(screen.getByText("RUN-2026-0802-0412")).toBeInTheDocument());
		});
	});

	// --- Issue #745/#744 remainders: component-results panel ---

	describe("Component results panel", () => {
		it("renders the six-status vocabulary honestly: execution_error is never presented as a plain pass/fail", async () => {
			installFetchMock();
			renderWithAuth();

			await waitFor(() => expect(screen.getByText("COMPONENT RESULTS")).toBeInTheDocument());
			expect(screen.getByText("Completed")).toBeInTheDocument();
			expect(screen.getByText("Execution error")).toBeInTheDocument();

			const errorBucket = screen.getByText("Execution error").closest(".results__cresult-status");
			expect(errorBucket?.className).toContain("results__cresult-status--error");
			const completedBucket = screen.getByText("Completed").closest(".results__cresult-status");
			expect(completedBucket?.className).toContain("results__cresult-status--completed");
			// The two buckets never share the same status-class modifier.
			expect(errorBucket?.className).not.toBe(completedBucket?.className);
		});

		it("renders the honest coverage ledger: planned vs resulted vs not-yet-resulted, never a fabricated 100%", async () => {
			installFetchMock();
			renderWithAuth();

			await waitFor(() => expect(screen.getByText("Coverage")).toBeInTheDocument());
			// COMPONENT_RESULTS_ROLLUP: 2 resulted (1 completed + 1 execution_error),
			// 1 not yet resulted, 3 planned total.
			expect(screen.getByText("2 resulted / 1 not yet resulted / 3 planned")).toBeInTheDocument();
		});

		it("renders CAT severity totals summed across every status bucket", async () => {
			installFetchMock();
			renderWithAuth();

			await waitFor(() => expect(screen.getByText("COMPONENT RESULTS")).toBeInTheDocument());
			const panel = screen.getByText("COMPONENT RESULTS").closest(".results__panel") as HTMLElement;
			expect(within(panel).getByText("1", { selector: ".results__cresult-severity-totals .mono" })).toBeInTheDocument();
		});

		it("shows a graceful empty state (not an error) when a run has no component results yet", async () => {
			installFetchMock({ componentResultsRollup: { run_id: "RUN-2026-0802-0412", planned_component_count: 5, by_status: [] } });
			renderWithAuth();

			await waitFor(() => expect(screen.getByText("COMPONENT RESULTS")).toBeInTheDocument());
			expect(screen.getByText(/No component results yet/)).toBeInTheDocument();
			expect(screen.queryByText(/could not be loaded/)).not.toBeInTheDocument();
		});

		it("shows an explicit unavailable state (distinct from 'no results yet') when the summary request fails", async () => {
			installFetchMock({ componentResultsRollup: "unavailable" });
			renderWithAuth();

			await waitFor(() => expect(screen.getByText("COMPONENT RESULTS")).toBeInTheDocument());
			expect(screen.getByText(/component-results\/summary failed/)).toBeInTheDocument();
			expect(screen.queryByText(/No component results yet/)).not.toBeInTheDocument();
		});

		it("loads upload-attempt history only after a component is selected, and never before", async () => {
			installFetchMock();
			renderWithAuth();

			await waitFor(() => expect(screen.getByText("COMPONENT RESULTS")).toBeInTheDocument());
			expect(screen.getByText("Select a component to view artifacts and upload history.")).toBeInTheDocument();

			const fetchMock = globalThis.fetch as unknown as ReturnType<typeof vi.fn>;
			expect(fetchMock.mock.calls.some((call) => String(call[0]).includes("upload-attempts"))).toBe(false);

			fireEvent.click(screen.getByText("esxi-01.example.internal", { selector: ".results__cresult-row span" }));

			await waitFor(() => expect(screen.getByText("UPLOAD ATTEMPT HISTORY")).toBeInTheDocument());
			const attemptsTable = await screen.findByText("vcf-collection");
			const row = attemptsTable.closest("tr") as HTMLElement;
			expect(within(row).getByText("uploaded")).toBeInTheDocument();
		});

		it("renders an honest empty state for a component with no recorded upload attempts", async () => {
			installFetchMock();
			renderWithAuth();

			await waitFor(() => expect(screen.getByText("COMPONENT RESULTS")).toBeInTheDocument());
			fireEvent.click(screen.getByText("esxi-02.example.internal", { selector: ".results__cresult-row span" }));

			await waitFor(() => expect(screen.getByText("No upload attempts recorded for this component.")).toBeInTheDocument());
		});
	});
});
