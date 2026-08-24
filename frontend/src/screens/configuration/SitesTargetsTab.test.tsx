import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { SitesTargetsTab } from "./SitesTargetsTab";
import type { Site, Target } from "./sites";

function makeTarget(id: string, siteId: string, name: string): Target {
	return {
		id,
		site_id: siteId,
		kind: "vsphere",
		name,
		connection: JSON.stringify({ host: `${name}.example.internal` }),
		credential_ref: null,
		discovery_status: "never_discovered",
		last_refreshed: null,
		created_at: "2026-01-01T00:00:00Z",
		updated_at: "2026-01-01T00:00:00Z",
		bindings: [],
	};
}

const SITES: Site[] = [
	{
		id: "site-1",
		name: "Alpha Enclave",
		description: "Primary enclave",
		stigman_override: null,
		created_at: "2026-01-01T00:00:00Z",
		updated_at: "2026-01-01T00:00:00Z",
	},
	{
		id: "site-2",
		name: "Bravo Enclave",
		description: null,
		stigman_override: "https://stigman-bravo.example.internal",
		created_at: "2026-01-01T00:00:00Z",
		updated_at: "2026-01-01T00:00:00Z",
	},
];

const TARGETS_BY_SITE: Record<string, Target[]> = {
	"site-1": [makeTarget("t-1", "site-1", "vcsa-01"), makeTarget("t-2", "site-1", "vcsa-02")],
	"site-2": [],
};

const CREDENTIALS = [{ id: "cred-1", name: "svc-stig-scan" }];

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(body === undefined ? null : JSON.stringify(body), {
		status,
		headers: { "Content-Type": "application/json" },
	});
}

describe("SitesTargetsTab (shell + Sites CRUD + wired-in targets panel)", () => {
	let originalFetch: typeof fetch;
	let fetchCalls: { url: string; init?: RequestInit }[];
	let sites: Site[];

	function installFetchMock(role: string) {
		fetchCalls = [];
		sites = SITES.map((s) => ({ ...s }));

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
			if (url.startsWith("/api/v1/sites/") && method === "PUT") {
				const id = url.split("/").pop()!;
				const body = JSON.parse(init!.body as string);
				sites = sites.map((s) => (s.id === id ? { ...s, name: body.name, description: body.description ?? null } : s));
				return jsonResponse(sites.find((s) => s.id === id));
			}
			if (url.startsWith("/api/v1/sites/") && method === "DELETE") {
				const id = url.split("/")[4]?.split("?")[0];
				sites = sites.filter((s) => s.id !== id);
				return jsonResponse(undefined, 204);
			}
			if (url === "/api/v1/credentials" && method === "GET") {
				return jsonResponse(CREDENTIALS);
			}
			const targetsMatch = url.match(/^\/api\/v1\/sites\/([^/]+)\/targets$/);
			if (targetsMatch && method === "GET") {
				const siteId = targetsMatch[1];
				return jsonResponse(TARGETS_BY_SITE[siteId] ?? []);
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
		await waitFor(() => expect(screen.getByText("Alpha Enclave")).toBeInTheDocument());
	}

	it("renders the sites sidebar from GET /sites and selects the first site", async () => {
		installFetchMock("Admin");
		await mount();

		expect(screen.getByText("Alpha Enclave")).toBeInTheDocument();
		expect(screen.getByText("Primary enclave")).toBeInTheDocument();
		await waitFor(() => expect(screen.getByText("ALPHA ENCLAVE · TARGETS")).toBeInTheDocument());
	});

	it("Admin can create a site via the Add site form, POSTing to /sites", async () => {
		installFetchMock("Admin");
		await mount();

		fireEvent.click(screen.getByText("Add site"));
		fireEvent.change(screen.getByPlaceholderText("e.g. Alpha Enclave"), { target: { value: "Bravo Enclave" } });
		fireEvent.click(screen.getByText("Save"));

		await waitFor(() =>
			expect(fetchCalls.some((c) => c.url === "/api/v1/sites" && c.init?.method === "POST")).toBe(true),
		);
		const call = fetchCalls.find((c) => c.url === "/api/v1/sites" && c.init?.method === "POST")!;
		expect(JSON.parse(call.init!.body as string)).toEqual({ name: "Bravo Enclave" });
	});

	it("Admin can delete a site via DELETE /sites/{id} after confirmation", async () => {
		installFetchMock("Admin");
		vi.spyOn(window, "confirm").mockReturnValue(true);
		await mount();

		const row = screen.getByText("Alpha Enclave").closest(".config-sidebar__item") as HTMLElement;
		fireEvent.click(within(row).getByText("Delete"));

		await waitFor(() =>
			expect(fetchCalls.some((c) => c.url === "/api/v1/sites/site-1" && c.init?.method === "DELETE")).toBe(true),
		);
	});

	it("Viewer sees Add site / Edit / Delete disabled with a Requires Admin reason, not hidden", async () => {
		installFetchMock("Viewer");
		await mount();

		const addSiteButton = screen.getByText("Add site");
		expect(addSiteButton).toBeDisabled();
		expect(addSiteButton).toHaveAttribute("title", expect.stringContaining("Requires Admin"));

		const row = screen.getByText("Alpha Enclave").closest(".config-sidebar__item") as HTMLElement;
		const editButton = within(row).getByText("Edit");
		expect(editButton).toBeDisabled();
		const deleteButton = within(row).getByText("Delete");
		expect(deleteButton).toBeDisabled();
	});

	it("renders the selected site's targets table in the main pane", async () => {
		installFetchMock("Admin");
		await mount();

		// The main pane now hosts the real SiteTargetsPanel (issue #258),
		// not a placeholder — the selected site's targets are listed.
		await waitFor(() => expect(screen.getByText("vcsa-01")).toBeInTheDocument());
		expect(screen.getByText("vcsa-02")).toBeInTheDocument();
	});

	it("renders a per-site target count in the sidebar, derived from GET /sites/{id}/targets", async () => {
		installFetchMock("Admin");
		await mount();

		await waitFor(() => expect(screen.getByText("2 targets")).toBeInTheDocument());
		expect(screen.getByText("0 targets")).toBeInTheDocument();
	});

	it("shows the STIG Manager binding per site (inherit or override)", async () => {
		installFetchMock("Admin");
		await mount();

		const inheritSites = screen.getAllByText("STIG Manager: inherit");
		expect(inheritSites.length).toBeGreaterThan(0);
		expect(screen.getByText("STIG Manager: override")).toBeInTheDocument();
	});
});
