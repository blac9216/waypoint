import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { SystemProvider } from "../../lib/system";
import { ComplianceContentTab } from "./ComplianceContentTab";

const CONFIG = {
	repository_url: "https://github.com/vmware/dod-compliance-and-automation",
	ref_type: "tag",
	ref_value: "v2026.07.2",
	pulled_commit: "4f1c9ae1234567890abcdef1234567890abcdef",
	pulled_by: "a.okafor",
	pulled_at: "2026-07-14T09:02:00Z",
	created_at: "2026-01-01T00:00:00Z",
	updated_at: "2026-07-14T09:02:00Z",
};

const PULLS = [
	{
		id: "pull-1",
		job_id: "job-1",
		ref_type: "tag",
		ref_value: "v2026.07.2",
		commit: "4f1c9ae1234567890abcdef1234567890abcdef",
		status: "succeeded",
		note: null,
		initiated_by: "a.okafor",
		created_at: "2026-07-14T09:02:00Z",
	},
];

const PROFILES = [
	{
		id: "prof-1",
		profile_key: "dod-vsphere-8-esxi-stig",
		name: "VMware vSphere 8.0 ESXi STIG",
		version: "1.1",
		commit: "4f1c9ae1234567890abcdef1234567890abcdef",
		state: "current",
		updated_at: "2026-07-14T09:02:00Z",
	},
];

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(body === undefined ? null : JSON.stringify(body), {
		status,
		headers: { "Content-Type": "application/json" },
	});
}

describe("ComplianceContentTab (issue #40)", () => {
	let originalFetch: typeof fetch;
	let fetchCalls: { url: string; init?: RequestInit }[];
	let pullRunState: { state: string; job_count_failed: number; job_count_blocked: number };

	function installFetchMock(
		role: string,
		opts: {
			mode?: "connected" | "disconnected";
			configMissing?: boolean;
			profiles?: typeof PROFILES;
			pulls?: typeof PULLS;
		} = {},
	) {
		fetchCalls = [];
		pullRunState = { state: "completed", job_count_failed: 0, job_count_blocked: 0 };
		const profiles = opts.profiles ?? PROFILES;
		const pulls = opts.pulls ?? PULLS;

		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			const method = init?.method ?? "GET";
			fetchCalls.push({ url, init });

			if (url === "/api/v1/system") {
				return jsonResponse({ version: "2.4.1", build: "24817", mode: opts.mode ?? "connected", update_available: null });
			}
			if (url === "/api/v1/stigman") {
				return jsonResponse({ error: { code: "not_found", message: "not configured" } }, 404);
			}
			if (url === "/api/v1/compliance-content" && method === "GET") {
				return opts.configMissing
					? jsonResponse({ error: { code: "not_found", message: "No compliance-content repository is configured." } }, 404)
					: jsonResponse(CONFIG);
			}
			if (url === "/api/v1/compliance-content" && method === "PUT") {
				const body = JSON.parse(init!.body as string);
				return jsonResponse({ ...CONFIG, ...body, updated_at: "2026-08-08T00:00:00Z" });
			}
			if (url === "/api/v1/compliance-content/pulls" && method === "GET") {
				return jsonResponse(pulls);
			}
			if (url === "/api/v1/compliance-content/pull" && method === "POST") {
				return jsonResponse({ run_id: "run-pull-1" }, 202);
			}
			if (url === "/api/v1/profiles" && method === "GET") {
				return jsonResponse(profiles);
			}
			if (url === "/api/v1/runs/run-pull-1" && method === "GET") {
				return jsonResponse({ id: "run-pull-1", ...pullRunState });
			}
			throw new Error(`unexpected fetch: ${method} ${url}`);
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
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
		window.sessionStorage.clear();
		vi.useRealTimers();
	});

	async function mount() {
		render(
			<AuthProvider>
				<SystemProvider>
					<ComplianceContentTab />
				</SystemProvider>
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("https://github.com/vmware/dod-compliance-and-automation")).toBeInTheDocument());
	}

	it("renders repo config, pinned tag tracking, recorded commit, and pull metadata", async () => {
		installFetchMock("Admin");
		await mount();

		expect(screen.getByText("https://github.com/vmware/dod-compliance-and-automation")).toBeInTheDocument();
		expect(screen.getByText("Pinned tag — v2026.07.2")).toBeInTheDocument();
		expect(screen.getAllByText("4f1c9ae12345").length).toBeGreaterThan(0);
		expect(screen.getAllByText(/a.okafor/).length).toBeGreaterThan(0);
	});

	it("renders profile inventory with state, feeding what Benchmarks (#559) will consume", async () => {
		installFetchMock("Admin");
		await mount();

		expect(screen.getByText("VMware vSphere 8.0 ESXi STIG")).toBeInTheDocument();
		expect(screen.getByText("current")).toBeInTheDocument();
	});

	it("renders pull history rows: who/when/commit/state", async () => {
		installFetchMock("Admin");
		await mount();

		expect(screen.getByText("succeeded")).toBeInTheDocument();
		expect(screen.getAllByText("4f1c9ae12345").length).toBeGreaterThan(0);
	});

	it("shows an update-pending banner derived from profile inventory state, not a nonexistent changelog field", async () => {
		installFetchMock("Admin", {
			profiles: [{ ...PROFILES[0], state: "update_pending" }],
		});
		await mount();

		expect(screen.getByText(/1 profile pending update/)).toBeInTheDocument();
	});

	it("Admin can trigger Pull updates; a second click while in flight is prevented (duplicate-submission)", async () => {
		installFetchMock("Admin");
		await mount();

		pullRunState = { state: "running", job_count_failed: 0, job_count_blocked: 0 };
		const pullButton = screen.getByText("Pull updates");
		fireEvent.click(pullButton);
		fireEvent.click(pullButton); // duplicate click while queued/running

		await waitFor(() => expect(fetchCalls.filter((c) => c.url === "/api/v1/compliance-content/pull").length).toBe(1));
		await waitFor(() => expect(screen.getByText("Pull queued or running…")).toBeInTheDocument());

		pullRunState = { state: "completed", job_count_failed: 0, job_count_blocked: 0 };
		await waitFor(() => expect(screen.queryByText("Pull queued or running…")).not.toBeInTheDocument(), { timeout: 6000 });
	}, 10000);

	it("a failed pull run keeps the failure feedback visible (aria-live, not color-only)", async () => {
		installFetchMock("Admin");
		await mount();

		pullRunState = { state: "completed_with_failures", job_count_failed: 1, job_count_blocked: 0 };
		fireEvent.click(screen.getByText("Pull updates"));

		await waitFor(() => expect(screen.getByText("Last pull attempt failed — see history below.")).toBeInTheDocument(), { timeout: 6000 });
		const status = screen.getByText("Last pull attempt failed — see history below.");
		expect(status.getAttribute("aria-live")).toBe("polite");
	}, 10000);

	it("Viewer sees the config and inventory but the pull action is disabled with a reason", async () => {
		installFetchMock("Viewer");
		await mount();

		const pullButton = screen.getByText("Pull updates") as HTMLButtonElement;
		expect(pullButton.disabled).toBe(true);
		expect(pullButton.title).toMatch(/Requires Admin/);
	});

	it("air-gapped mode disables the pull action with an air-gap reason, but config/inventory stay visible", async () => {
		installFetchMock("Admin", { mode: "disconnected" });
		await mount();

		const pullButton = screen.getByText("Pull unavailable") as HTMLButtonElement;
		expect(pullButton.disabled).toBe(true);
		expect(pullButton.title).toMatch(/air-gapped mode/);
		// Config/inventory are still readable in air-gapped mode.
		expect(screen.getByText("https://github.com/vmware/dod-compliance-and-automation")).toBeInTheDocument();
		expect(screen.getByText("VMware vSphere 8.0 ESXi STIG")).toBeInTheDocument();
	});

	it("no repository configured yet: Admin sees a Configure affordance and can save tag/branch config", async () => {
		installFetchMock("Admin", { configMissing: true });
		render(
			<AuthProvider>
				<SystemProvider>
					<ComplianceContentTab />
				</SystemProvider>
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText(/No compliance-content repository is configured/)).toBeInTheDocument());

		fireEvent.click(screen.getByText("Configure"));
		fireEvent.change(screen.getByPlaceholderText(/git\.example\.internal/), {
			target: { value: "https://github.com/vmware/dod-compliance-and-automation" },
		});
		fireEvent.change(screen.getByPlaceholderText("v2026.08.1"), { target: { value: "v2026.08.1" } });
		fireEvent.click(screen.getByText("Save"));

		await waitFor(() => expect(fetchCalls.some((c) => c.url === "/api/v1/compliance-content" && c.init?.method === "PUT")).toBe(true));
		const call = fetchCalls.find((c) => c.url === "/api/v1/compliance-content" && c.init?.method === "PUT")!;
		const body = JSON.parse(call.init!.body as string);
		expect(body.ref_type).toBe("tag");
		expect(body.ref_value).toBe("v2026.08.1");
	});

	it("switching tracking to branch changes the ref field placeholder/label", async () => {
		installFetchMock("Admin");
		await mount();

		fireEvent.click(screen.getByText("Edit"));
		const select = screen.getByDisplayValue("Pinned tag");
		fireEvent.change(select, { target: { value: "branch" } });
		expect(screen.getByPlaceholderText("main")).toBeInTheDocument();
	});
});
