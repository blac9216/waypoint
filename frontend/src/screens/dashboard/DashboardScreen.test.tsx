/**
 * DashboardScreen — issue #513. Covers: KPI tiles rendering from `GET
 * /dashboard`, the SITE POSTURE table (including the "no scan data" state
 * for a site with no completed run), RECENT RUNS, and the SCHEDULES/
 * ATTENTION sidebar cards (including their empty states) — matching the
 * "empty-state before schedules exist" acceptance criterion.
 */
import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { RouterProvider } from "../../lib/router";
import { SystemProvider } from "../../lib/system";
import { DashboardScreen } from "./DashboardScreen";

const DASHBOARD_DATA = {
	targets: { total: 42, site_count: 2, by_kind: { vsphere: 10, ssh: 32 } },
	findings: { counts_available: true, cat_i_open: 3, cat_ii_open: 12, cat_iii_open: 5 },
	sites: [
		{
			id: "site-1",
			name: "Alpha Enclave",
			target_count: 20,
			compliance_percent: 91.5,
			cat_i_open: 1,
			cat_ii_open: 4,
			cat_iii_open: 2,
			last_scan_at: "2026-08-19T04:12:00Z",
		},
		{
			id: "site-2",
			name: "Bravo Enclave",
			target_count: 22,
			compliance_percent: null,
			cat_i_open: null,
			cat_ii_open: null,
			cat_iii_open: null,
			last_scan_at: null,
		},
	],
	recent_runs: [
		{
			id: "RUN-2026-0819-0412",
			run_type: "scan",
			state: "completed",
			scope: JSON.stringify({ site_id: "Alpha Enclave" }),
			job_count: 20,
			created_at: "2026-08-19T04:12:00Z",
		},
	],
	attention: [
		{ kind: "credential_auth_failing", text: "Credential 'svc-stig-scan' is failing authentication", meta: "shared" },
		{ kind: "inventory_stale", text: "Target 'esxi-01' inventory is stale", meta: "last refreshed 2026-08-18 10:00" },
	],
};

const SYSTEM_INFO = {
	version: "2.4.1-24817",
	build: "abc123",
	mode: "connected" as const,
	update_available: null,
	runners: [],
};

function jsonResponse(body: unknown): Response {
	return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
}

function installFetchMock(options: { schedules?: unknown[]; dashboard?: unknown } = {}) {
	const schedules = options.schedules ?? [];
	const dashboard = options.dashboard ?? DASHBOARD_DATA;
	globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
		const url = typeof input === "string" ? input : input.toString();
		if (url === "/api/v1/dashboard") {
			return jsonResponse(dashboard);
		}
		if (url === "/api/v1/schedules") {
			return jsonResponse(schedules);
		}
		if (url === "/api/v1/system") {
			return jsonResponse(SYSTEM_INFO);
		}
		if (url === "/api/v1/stigman") {
			return new Response("not found", { status: 404 });
		}
		if (url === "/api/v1/auth/me") {
			return jsonResponse({ username: "j.moreno", role: "Admin" });
		}
		throw new Error(`Unhandled fetch in test: ${url}`);
	}) as unknown as typeof fetch;
}

function renderWithProviders(role: "Viewer" | "Cyber" | "Operator" | "Admin" = "Admin") {
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
			<SystemProvider>
				<RouterProvider>
					<DashboardScreen />
				</RouterProvider>
			</SystemProvider>
		</AuthProvider>,
	);
}

describe("DashboardScreen", () => {
	beforeEach(() => {
		sessionStorage.clear();
	});

	afterEach(() => {
		vi.restoreAllMocks();
	});

	it("renders the KPI tiles from GET /dashboard", async () => {
		installFetchMock();
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("OPEN FINDINGS")).toBeInTheDocument());
		expect(screen.getAllByText("TARGETS").length).toBeGreaterThan(0);
		expect(screen.getByText("42")).toBeInTheDocument();
		expect(screen.getByText("3")).toBeInTheDocument();
	});

	it("renders SITE POSTURE with a no-scan-data state for a site with no completed run", async () => {
		installFetchMock();
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("Alpha Enclave")).toBeInTheDocument());
		expect(screen.getByText("Bravo Enclave")).toBeInTheDocument();
		expect(screen.getByText("91.5%")).toBeInTheDocument();
		expect(screen.getByText("no scan data")).toBeInTheDocument();
	});

	it("renders RECENT RUNS rows", async () => {
		installFetchMock();
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("RUN-2026-0819-0412")).toBeInTheDocument());
	});

	it("renders the SCHEDULES card empty state when no schedules exist", async () => {
		installFetchMock({ schedules: [] });
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("No schedules configured yet.")).toBeInTheDocument());
	});

	it("renders SCHEDULES rows with next-run/last-result once schedules exist", async () => {
		installFetchMock({
			schedules: [
				{
					id: "sched-1",
					name: "Nightly Alpha scan",
					job_type: "scan",
					cron_expression: "0 2 * * *",
					scope: "{}",
					credential_id: null,
					enabled: true,
					paused_reason: null,
					next_run_at: "2026-08-20T02:00:00Z",
					last_run_at: "2026-08-19T02:00:00Z",
					last_run_id: "run-1",
					last_result: "completed",
					created_by: "j.moreno",
					created_at: "2026-08-01T00:00:00Z",
					updated_at: "2026-08-01T00:00:00Z",
				},
			],
		});
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("Nightly Alpha scan")).toBeInTheDocument());
		expect(screen.getByText(/last completed/)).toBeInTheDocument();
	});

	it("renders ATTENTION rows for the in-scope signal kinds", async () => {
		installFetchMock();
		renderWithProviders();

		await waitFor(() => expect(screen.getByText(/Credential 'svc-stig-scan' is failing authentication/)).toBeInTheDocument());
		expect(screen.getByText(/Target 'esxi-01' inventory is stale/)).toBeInTheDocument();
	});

	it("renders the ATTENTION empty state when nothing needs attention", async () => {
		installFetchMock({ dashboard: { ...DASHBOARD_DATA, attention: [] } });
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("Nothing needs attention.")).toBeInTheDocument());
	});

	it("shows an error state when GET /dashboard fails", async () => {
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
			const url = typeof input === "string" ? input : input.toString();
			if (url === "/api/v1/dashboard") {
				return new Response(JSON.stringify({ error: { code: "server_error", message: "boom" } }), { status: 500 });
			}
			if (url === "/api/v1/schedules") {
				return jsonResponse([]);
			}
			if (url === "/api/v1/system") {
				return jsonResponse(SYSTEM_INFO);
			}
			if (url === "/api/v1/stigman") {
				return new Response("not found", { status: 404 });
			}
			if (url === "/api/v1/auth/me") {
				return jsonResponse({ username: "j.moreno", role: "Admin" });
			}
			throw new Error(`Unhandled fetch in test: ${url}`);
		}) as unknown as typeof fetch;
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("boom")).toBeInTheDocument());
	});
});
