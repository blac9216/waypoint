import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { SiteTargetsPanel } from "./SiteTargetsPanel";
import type { Target } from "./sites";

const TARGETS: Target[] = [
	{
		id: "target-1",
		site_id: "site-1",
		kind: "vsphere",
		name: "vcsa-01",
		connection: JSON.stringify({ host: "vcsa-01.example.internal" }),
		credential_ref: "cred-1",
		discovery_status: "discovered",
		last_refreshed: "2026-08-01T12:00:00Z",
		created_at: "2026-01-01T00:00:00Z",
		updated_at: "2026-01-01T00:00:00Z",
		bindings: [
			{
				purpose: "vsphere-api",
				credential_ref: "cred-1",
				credential_name: "svc-stig-scan",
				credential_type: "vcenter",
				created_at: "2026-01-01T00:00:00Z",
				updated_at: "2026-01-01T00:00:00Z",
			},
		],
	},
	{
		id: "target-2",
		site_id: "site-1",
		kind: "ssh",
		name: "photon-01",
		connection: JSON.stringify({ host: "photon-01.example.internal" }),
		credential_ref: null,
		discovery_status: "failed",
		last_refreshed: null,
		created_at: "2026-01-01T00:00:00Z",
		updated_at: "2026-01-01T00:00:00Z",
		bindings: [],
	},
];

const CREDENTIALS = [
	{ id: "cred-1", name: "svc-stig-scan", credential_type: "vcenter" },
	{ id: "cred-2", name: "svc-srg-ssh", credential_type: "ssh" },
];

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(body === undefined ? null : JSON.stringify(body), {
		status,
		headers: { "Content-Type": "application/json" },
	});
}

describe("SiteTargetsPanel (issue #258 slice: targets table + Targets CRUD)", () => {
	let originalFetch: typeof fetch;
	let fetchCalls: { url: string; init?: RequestInit }[];
	let targets: Target[];

	function installFetchMock(role: string) {
		fetchCalls = [];
		targets = TARGETS.map((t) => ({ ...t }));

		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			const method = init?.method ?? "GET";
			fetchCalls.push({ url, init });

			if (url === "/api/v1/sites/site-1/targets" && method === "GET") {
				return jsonResponse(targets);
			}
			if (url === "/api/v1/sites/site-1/targets" && method === "POST") {
				const body = JSON.parse(init!.body as string);
				const created: Target = {
					id: "target-new",
					site_id: "site-1",
					kind: body.kind,
					name: body.name,
					connection: JSON.stringify(body.connection ?? {}),
					credential_ref: body.credential_ref ?? null,
					discovery_status: "never_discovered",
					last_refreshed: null,
					created_at: "2026-08-08T00:00:00Z",
					updated_at: "2026-08-08T00:00:00Z",
					bindings: [],
				};
				targets = [...targets, created];
				return jsonResponse(created, 201);
			}
			const bindingMatch = /^\/api\/v1\/targets\/([^/]+)\/credential-bindings\/([^/]+)$/.exec(url);
			if (bindingMatch && method === "PUT") {
				const [, id, purpose] = bindingMatch;
				const body = JSON.parse(init!.body as string);
				const credential = CREDENTIALS.find((c) => c.id === body.credential_ref);
				targets = targets.map((t) =>
					t.id === id
						? {
								...t,
								bindings: [
									...t.bindings.filter((b) => b.purpose !== purpose),
									{
										purpose,
										credential_ref: body.credential_ref,
										credential_name: credential?.name ?? null,
										credential_type: credential?.credential_type ?? null,
										created_at: "2026-08-08T00:00:00Z",
										updated_at: "2026-08-08T00:00:00Z",
									},
								],
							}
						: t,
				);
				return jsonResponse(targets.find((t) => t.id === id));
			}
			if (bindingMatch && method === "DELETE") {
				const [, id, purpose] = bindingMatch;
				targets = targets.map((t) => (t.id === id ? { ...t, bindings: t.bindings.filter((b) => b.purpose !== purpose) } : t));
				return jsonResponse(targets.find((t) => t.id === id));
			}
			if (url.startsWith("/api/v1/targets/") && method === "PUT") {
				const id = url.split("/").pop()!;
				const body = JSON.parse(init!.body as string);
				targets = targets.map((t) =>
					t.id === id
						? {
								...t,
								kind: body.kind,
								name: body.name,
								connection: JSON.stringify(body.connection ?? {}),
								credential_ref: body.credential_ref ?? null,
							}
						: t,
				);
				return jsonResponse(targets.find((t) => t.id === id));
			}
			if (url.startsWith("/api/v1/targets/") && method === "DELETE") {
				const id = url.split("/").pop()!;
				targets = targets.filter((t) => t.id !== id);
				return jsonResponse(undefined, 204);
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
	});

	async function mount(onTargetsChanged?: () => void) {
		render(
			<AuthProvider>
				<SiteTargetsPanel
					siteId="site-1"
					siteName="Alpha Enclave"
					credentials={CREDENTIALS}
					onTargetsChanged={onTargetsChanged}
				/>
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("vcsa-01")).toBeInTheDocument());
	}

	it("renders the targets table from GET /sites/{id}/targets, including the credential name", async () => {
		installFetchMock("Admin");
		await mount();

		expect(screen.getByText("photon-01")).toBeInTheDocument();
		expect(screen.getByText("svc-stig-scan")).toBeInTheDocument();
	});

	it("shows discovery status, styling a failed target distinctly", async () => {
		installFetchMock("Admin");
		await mount();

		expect(screen.getByText("discovered")).toBeInTheDocument();
		const failedStatus = screen.getByText("discovery failed");
		expect(failedStatus.closest("td")).toHaveClass("config-table__discovery--bad");
	});

	it("Admin can create a target via the Add target form, POSTing connection as {host} only", async () => {
		installFetchMock("Admin");
		await mount();

		fireEvent.click(screen.getByText("Add target"));
		fireEvent.change(screen.getByPlaceholderText("e.g. vcsa-01"), { target: { value: "nsx-01" } });
		fireEvent.change(screen.getByPlaceholderText("vcsa-01.example.internal"), {
			target: { value: "nsx-01.example.internal" },
		});
		fireEvent.click(screen.getByText("Save"));

		await waitFor(() =>
			expect(fetchCalls.some((c) => c.url === "/api/v1/sites/site-1/targets" && c.init?.method === "POST")).toBe(
				true,
			),
		);
		const call = fetchCalls.find((c) => c.url === "/api/v1/sites/site-1/targets" && c.init?.method === "POST")!;
		const body = JSON.parse(call.init!.body as string);
		expect(body).toEqual({ kind: "vsphere", name: "nsx-01", connection: { host: "nsx-01.example.internal" } });
		// No path in the form can carry a raw secret — the payload has exactly
		// these three keys, never anything password/token/secret-shaped.
		expect(Object.keys(body).sort()).toEqual(["connection", "kind", "name"]);
	});

	it("Admin can edit a target's credential via the picker, PUTing credential_ref", async () => {
		installFetchMock("Admin");
		await mount();

		const row = screen.getByText("photon-01").closest("tr")!;
		fireEvent.click(within(row).getByText("Edit"));

		// The target's own credential_ref picker is the TargetForm's "Credential"
		// field — the first "No credential" select in document order. The
		// per-purpose credential-bindings panel (issue #584) renders its own
		// "No credential" select(s) below it, so this test disambiguates by
		// picking the first one rather than assuming there is only one.
		const credentialSelect = screen.getAllByDisplayValue("No credential")[0] as HTMLSelectElement;
		fireEvent.change(credentialSelect, { target: { value: "cred-1" } });
		fireEvent.click(screen.getByText("Save"));

		await waitFor(() =>
			expect(fetchCalls.some((c) => c.url === "/api/v1/targets/target-2" && c.init?.method === "PUT")).toBe(true),
		);
		const call = fetchCalls.find((c) => c.url === "/api/v1/targets/target-2" && c.init?.method === "PUT")!;
		const body = JSON.parse(call.init!.body as string);
		expect(body.credential_ref).toBe("cred-1");
		expect(body.clear_credential_ref).toBe(false);
	});

	it("issue #584: shows a coverage warning for a missing required purpose binding, and offers only compatible credentials", async () => {
		installFetchMock("Admin");
		await mount();

		const row = screen.getByText("photon-01").closest("tr")!;
		fireEvent.click(within(row).getByText("Edit"));

		expect(screen.getByText("SRG SSH")).toBeInTheDocument();
		expect(screen.getByText("Missing required binding")).toBeInTheDocument();

		// srg-ssh only accepts `ssh`-type credentials — svc-stig-scan (vcenter)
		// must not appear in this picker, only svc-srg-ssh (ssh).
		const bindingSelect = screen.getByLabelText("SRG SSH credential") as HTMLSelectElement;
		const labels = Array.from(bindingSelect.options).map((o) => o.text);
		expect(labels).toEqual(["No credential", "svc-srg-ssh"]);
	});

	it("issue #584: setting a purpose binding PUTs /targets/{id}/credential-bindings/{purpose} and refreshes coverage", async () => {
		installFetchMock("Admin");
		await mount();

		const row = screen.getByText("photon-01").closest("tr")!;
		fireEvent.click(within(row).getByText("Edit"));

		const bindingSelect = screen.getByLabelText("SRG SSH credential") as HTMLSelectElement;
		fireEvent.change(bindingSelect, { target: { value: "cred-2" } });

		await waitFor(() =>
			expect(
				fetchCalls.some((c) => c.url === "/api/v1/targets/target-2/credential-bindings/srg-ssh" && c.init?.method === "PUT"),
			).toBe(true),
		);
		const call = fetchCalls.find(
			(c) => c.url === "/api/v1/targets/target-2/credential-bindings/srg-ssh" && c.init?.method === "PUT",
		)!;
		expect(JSON.parse(call.init!.body as string)).toEqual({ credential_ref: "cred-2" });

		await waitFor(() => expect(screen.queryByText("Missing required binding")).not.toBeInTheDocument());
		await waitFor(() => expect(screen.getByText("Bound")).toBeInTheDocument());
	});

	it("issue #584: clearing a purpose binding DELETEs /targets/{id}/credential-bindings/{purpose}", async () => {
		installFetchMock("Admin");
		await mount();

		const row = screen.getByText("vcsa-01").closest("tr")!;
		fireEvent.click(within(row).getByText("Edit"));

		const bindingSelect = screen.getByLabelText("vSphere API credential") as HTMLSelectElement;
		expect(bindingSelect.value).toBe("cred-1");
		fireEvent.change(bindingSelect, { target: { value: "" } });

		await waitFor(() =>
			expect(
				fetchCalls.some(
					(c) => c.url === "/api/v1/targets/target-1/credential-bindings/vsphere-api" && c.init?.method === "DELETE",
				),
			).toBe(true),
		);
	});

	it("issue #584: a vsphere target's bindings panel shows both applicable purposes (vsphere-api, VCSA SSH)", async () => {
		installFetchMock("Admin");
		await mount();

		const row = screen.getByText("vcsa-01").closest("tr")!;
		fireEvent.click(within(row).getByText("Edit"));

		expect(screen.getByText("vSphere API")).toBeInTheDocument();
		expect(screen.getByText("VCSA SSH")).toBeInTheDocument();
	});

	it("Admin can delete a target via DELETE /targets/{id}", async () => {
		installFetchMock("Admin");
		vi.spyOn(window, "confirm").mockReturnValue(true);
		await mount();

		const row = screen.getByText("photon-01").closest("tr")!;
		fireEvent.click(within(row).getByText("Delete"));

		await waitFor(() =>
			expect(fetchCalls.some((c) => c.url === "/api/v1/targets/target-2" && c.init?.method === "DELETE")).toBe(
				true,
			),
		);
		await waitFor(() => expect(screen.queryByText("photon-01")).not.toBeInTheDocument());
	});

	it("Viewer sees Add target / Edit / Delete disabled with a Requires Admin reason, not hidden", async () => {
		installFetchMock("Viewer");
		await mount();

		const addTargetButton = screen.getByText("Add target");
		expect(addTargetButton).toBeDisabled();
		expect(addTargetButton).toHaveAttribute("title", expect.stringContaining("Requires Admin"));

		const row = screen.getByText("vcsa-01").closest("tr")!;
		expect(within(row).getByText("Edit")).toBeDisabled();
		expect(within(row).getByText("Delete")).toBeDisabled();
	});

	it("restricts the target kind selector to exactly vsphere/nsx-api/ssh", async () => {
		installFetchMock("Admin");
		await mount();

		fireEvent.click(screen.getByText("Add target"));
		const select = screen.getByDisplayValue("vSphere (vCenter)") as HTMLSelectElement;
		const values = Array.from(select.options).map((o) => o.value);
		expect(values).toEqual(["vsphere", "nsx-api", "ssh"]);
	});

	it("the credential picker only ever offers id/name pairs, never a raw-secret input", async () => {
		installFetchMock("Admin");
		await mount();

		fireEvent.click(screen.getByText("Add target"));
		const select = screen.getByDisplayValue("No credential") as HTMLSelectElement;
		const labels = Array.from(select.options).map((o) => o.text);
		expect(labels).toEqual(["No credential", "svc-stig-scan", "svc-srg-ssh"]);
		// No password/secret-shaped input exists anywhere in the form.
		expect(screen.queryByLabelText(/password|secret|token/i)).not.toBeInTheDocument();
	});

	it("notifies the parent (onTargetsChanged) after a create so the sidebar count can refresh", async () => {
		installFetchMock("Admin");
		const onTargetsChanged = vi.fn();
		await mount(onTargetsChanged);

		fireEvent.click(screen.getByText("Add target"));
		fireEvent.change(screen.getByPlaceholderText("e.g. vcsa-01"), { target: { value: "nsx-01" } });
		fireEvent.change(screen.getByPlaceholderText("vcsa-01.example.internal"), {
			target: { value: "nsx-01.example.internal" },
		});
		fireEvent.click(screen.getByText("Save"));

		await waitFor(() => expect(onTargetsChanged).toHaveBeenCalled());
	});

	it("notifies the parent (onTargetsChanged) after a delete", async () => {
		installFetchMock("Admin");
		vi.spyOn(window, "confirm").mockReturnValue(true);
		const onTargetsChanged = vi.fn();
		await mount(onTargetsChanged);

		const row = screen.getByText("photon-01").closest("tr")!;
		fireEvent.click(within(row).getByText("Delete"));

		await waitFor(() => expect(onTargetsChanged).toHaveBeenCalled());
	});
});

describe("SiteTargetsPanel Refresh Inventory action (issue #557)", () => {
	let originalFetch: typeof fetch;
	let fetchCalls: { url: string; init?: RequestInit }[];
	let targets: Target[];
	let runStates: Record<string, { state: string; job_count_failed: number; job_count_blocked: number }>;
	let discoverResponse: { run_id: string; job_id: string } | { error: { code: string; message: string } } | null;

	function queueRunState(runId: string, state: string, failed = 0, blocked = 0) {
		runStates[runId] = { state, job_count_failed: failed, job_count_blocked: blocked };
	}

	function jsonResponse(body: unknown, status = 200): Response {
		return new Response(body === undefined ? null : JSON.stringify(body), {
			status,
			headers: { "Content-Type": "application/json" },
		});
	}

	function installFetchMock(role: string) {
		fetchCalls = [];
		targets = TARGETS.map((t) => ({ ...t }));
		runStates = {};
		discoverResponse = { run_id: "run-1", job_id: "job-1" };

		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			const method = init?.method ?? "GET";
			fetchCalls.push({ url, init });

			if (url === "/api/v1/sites/site-1/targets" && method === "GET") {
				return jsonResponse(targets);
			}
			if (/^\/api\/v1\/targets\/[^/]+\/discover$/.test(url) && method === "POST") {
				if (discoverResponse && "error" in discoverResponse) {
					return jsonResponse(discoverResponse, 400);
				}
				return jsonResponse(discoverResponse, 202);
			}
			if (/^\/api\/v1\/runs\/[^/]+$/.test(url) && method === "GET") {
				const runId = url.split("/").pop()!;
				const run = runStates[runId] ?? { state: "running", job_count_failed: 0, job_count_blocked: 0 };
				return jsonResponse({
					id: runId,
					run_type: "discover",
					state: run.state,
					job_count_failed: run.job_count_failed,
					job_count_blocked: run.job_count_blocked,
				});
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
				<SiteTargetsPanel siteId="site-1" siteName="Alpha Enclave" credentials={CREDENTIALS} />
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("vcsa-01")).toBeInTheDocument());
	}

	it("shows Refresh Inventory for the vsphere (inventory-capable) target and not for the ssh target", async () => {
		installFetchMock("Admin");
		await mount();

		const vsphereRow = screen.getByText("vcsa-01").closest("tr")!;
		expect(within(vsphereRow).getByText("Refresh Inventory")).toBeInTheDocument();

		const sshRow = screen.getByText("photon-01").closest("tr")!;
		expect(within(sshRow).queryByText("Refresh Inventory")).not.toBeInTheDocument();
		expect(within(sshRow).queryByText(/Refresh/)).not.toBeInTheDocument();
	});

	it("calls POST /targets/{id}/discover exactly once and shows queued/running feedback via aria-live status", async () => {
		installFetchMock("Admin");
		queueRunState("run-1", "running");
		await mount();

		const row = screen.getByText("vcsa-01").closest("tr")!;
		fireEvent.click(within(row).getByText("Refresh Inventory"));

		await waitFor(() =>
			expect(fetchCalls.filter((c) => c.url === "/api/v1/targets/target-1/discover" && c.init?.method === "POST")).toHaveLength(1),
		);
		await waitFor(() => expect(within(row).getByText("Refreshing inventory…")).toBeInTheDocument());
		const status = within(row).getByText("Refreshing inventory…");
		expect(status).toHaveAttribute("aria-live", "polite");
	});

	it("disables the action while discovery is queued/running, preventing duplicate submissions", async () => {
		installFetchMock("Admin");
		queueRunState("run-1", "running");
		await mount();

		const row = screen.getByText("vcsa-01").closest("tr")!;
		const button = within(row).getByText("Refresh Inventory");
		fireEvent.click(button);

		await waitFor(() => expect(within(row).getByText("Refreshing…")).toBeDisabled());
		fireEvent.click(within(row).getByText("Refreshing…"));

		await waitFor(() =>
			expect(fetchCalls.filter((c) => c.url === "/api/v1/targets/target-1/discover" && c.init?.method === "POST")).toHaveLength(1),
		);
	});

	it("updates from running to discovered without a full-page reload, then re-enables the action", async () => {
		installFetchMock("Admin");
		queueRunState("run-1", "running");
		await mount();

		const row = screen.getByText("vcsa-01").closest("tr")!;
		fireEvent.click(within(row).getByText("Refresh Inventory"));
		await waitFor(() => expect(within(row).getByText("Refreshing inventory…")).toBeInTheDocument());

		// Flip the run terminal and let the next poll tick observe it — no
		// fake timers needed since the component's own 3s poll loop will pick
		// this up; wait generously for that real tick. On success the local
		// discovery state clears entirely (a fresh load() reflects the real
		// discovery_status), so the action reverts to its enabled idle label.
		queueRunState("run-1", "completed");
		await waitFor(() => expect(within(row).getByText("Refresh Inventory")).not.toBeDisabled(), { timeout: 6000 });
	}, 10000);

	it("a terminal failed run exposes a run-detail link and a Retry action", async () => {
		installFetchMock("Admin");
		queueRunState("run-1", "completed_with_failures", 1);
		await mount();

		const row = screen.getByText("vcsa-01").closest("tr")!;
		fireEvent.click(within(row).getByText("Refresh Inventory"));

		await waitFor(() => expect(within(row).getByText("Retry")).toBeInTheDocument(), { timeout: 6000 });
		const link = within(row).getByText("View run details") as HTMLAnchorElement;
		expect(link.getAttribute("href")).toBe("/live-jobs?run=run-1");
		expect(within(row).getByText("Retry")).not.toBeDisabled();
	}, 10000);

	it("surfaces a rejected POST /discover as an error without leaving the action stuck disabled", async () => {
		installFetchMock("Admin");
		discoverResponse = { error: { code: "unsupported_kind", message: "discover only supports 'vsphere' targets." } };
		await mount();

		const row = screen.getByText("vcsa-01").closest("tr")!;
		fireEvent.click(within(row).getByText("Refresh Inventory"));

		await waitFor(() => expect(screen.getByText(/discover only supports/)).toBeInTheDocument());
		expect(within(row).getByText("Refresh Inventory")).not.toBeDisabled();
	});

	it("Viewer sees Refresh Inventory disabled with a Requires Admin reason, matching the backend's Admin-only guard", async () => {
		installFetchMock("Viewer");
		await mount();

		const row = screen.getByText("vcsa-01").closest("tr")!;
		const button = within(row).getByText("Refresh Inventory");
		expect(button).toBeDisabled();
		expect(button).toHaveAttribute("title", expect.stringContaining("Requires Admin"));
	});
});
