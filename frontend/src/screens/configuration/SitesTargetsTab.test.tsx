import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { SitesTargetsTab } from "./SitesTargetsTab";
import type { Site, Target } from "./sites";

const SITES: Site[] = [
	{
		id: "site-1",
		name: "Alpha Enclave",
		description: "Primary enclave",
		stigman_override: null,
		created_at: "2026-01-01T00:00:00Z",
		updated_at: "2026-01-01T00:00:00Z",
	},
];

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
	},
];

const CREDENTIALS = [{ id: "cred-1", name: "svc-stig-scan" }];

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(body === undefined ? null : JSON.stringify(body), {
		status,
		headers: { "Content-Type": "application/json" },
	});
}

describe("SitesTargetsTab", () => {
	let originalFetch: typeof fetch;
	let fetchCalls: { url: string; init?: RequestInit }[];
	let sites: Site[];
	let targets: Target[];

	function installFetchMock(role: string) {
		fetchCalls = [];
		sites = SITES.map((s) => ({ ...s }));
		targets = TARGETS.map((t) => ({ ...t }));

		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			const method = init?.method ?? "GET";
			fetchCalls.push({ url, init });

			if (url === "/api/v1/sites" && method === "GET") {
				return jsonResponse(sites);
			}
			if (url === "/api/v1/sites" && method === "POST") {
				const body = JSON.parse(init!.body as string);
				const created: Site = {
					id: "site-new",
					name: body.name,
					description: body.description ?? null,
					stigman_override: null,
					created_at: "2026-08-08T00:00:00Z",
					updated_at: "2026-08-08T00:00:00Z",
				};
				sites = [...sites, created];
				return jsonResponse(created, 201);
			}
			if (url === "/api/v1/credentials" && method === "GET") {
				return jsonResponse(CREDENTIALS);
			}
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
				};
				targets = [...targets, created];
				return jsonResponse(created, 201);
			}
			if (url.startsWith("/api/v1/targets/") && method === "PUT") {
				const id = url.split("/").pop()!;
				const body = JSON.parse(init!.body as string);
				targets = targets.map((t) =>
					t.id === id
						? { ...t, kind: body.kind, name: body.name, connection: JSON.stringify(body.connection ?? {}), credential_ref: body.credential_ref ?? null }
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

	async function mount() {
		render(
			<AuthProvider>
				<SitesTargetsTab />
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("vcsa-01")).toBeInTheDocument());
	}

	it("renders the targets table from GET /sites/{id}/targets and the sites sidebar", async () => {
		installFetchMock("Admin");
		await mount();

		expect(screen.getByText("photon-01")).toBeInTheDocument();
		expect(screen.getByText("Alpha Enclave")).toBeInTheDocument();
		expect(screen.getByText("svc-stig-scan")).toBeInTheDocument();
	});

	it("shows discovery status, including a failed target distinctly", async () => {
		installFetchMock("Admin");
		await mount();

		expect(screen.getByText("discovered")).toBeInTheDocument();
		const failedCell = screen.getByText("discovery failed");
		expect(failedCell).toHaveClass("config-table__discovery--bad");
	});

	it("Admin can create a target via the Add target form, POSTing to /sites/{id}/targets", async () => {
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

	it("Viewer sees mutation buttons disabled with a reason, not hidden", async () => {
		installFetchMock("Viewer");
		await mount();

		const addSiteButton = screen.getByText("Add site");
		expect(addSiteButton).toBeDisabled();
		expect(addSiteButton).toHaveAttribute("title", expect.stringContaining("Requires Admin"));

		const addTargetButton = screen.getByText("Add target");
		expect(addTargetButton).toBeDisabled();
		expect(addTargetButton).toHaveAttribute("title", expect.stringContaining("Requires Admin"));

		const row = screen.getByText("vcsa-01").closest("tr")!;
		expect(within(row).getByText("Edit")).toBeDisabled();
		expect(within(row).getByText("Delete")).toBeDisabled();
	});

	it("restricts the target kind selector to vsphere/nsx-api/ssh", async () => {
		installFetchMock("Admin");
		await mount();

		fireEvent.click(screen.getByText("Add target"));
		const select = screen.getByDisplayValue("vSphere (vCenter)") as HTMLSelectElement;
		const values = Array.from(select.options).map((o) => o.value);
		expect(values).toEqual(["vsphere", "nsx-api", "ssh"]);
	});
});
