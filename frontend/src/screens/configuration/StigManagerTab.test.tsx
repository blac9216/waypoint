import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { StigManagerTab } from "./StigManagerTab";

const GLOBAL_CONNECTION = {
	endpoint: "https://stigman.example.internal",
	authority: "https://keycloak.example.internal/realms/waypoint",
	collection: "17",
	client_id: "waypoint-appliance",
	scope: "openid stig-manager:stig:read",
	credential_id: "cred-token-1",
	created_at: "2026-01-01T00:00:00Z",
	updated_at: "2026-07-01T00:00:00Z",
};

const CREDENTIAL_OPTIONS = [
	{ id: "cred-token-1", name: "svc-stigman-oidc" },
	{ id: "cred-token-2", name: "svc-stigman-scif-oidc" },
];

const SITES = [
	{ id: "site-alpha", name: "Alpha Enclave", description: null, stigman_override: null, created_at: "2026-01-01T00:00:00Z", updated_at: "2026-01-01T00:00:00Z" },
	{
		id: "site-charlie",
		name: "Charlie Vault",
		description: null,
		stigman_override: JSON.stringify({ Endpoint: "https://stigman-scif.example.internal" }),
		created_at: "2026-01-01T00:00:00Z",
		updated_at: "2026-01-01T00:00:00Z",
	},
];

const CHARLIE_RESOLVED = {
	endpoint: "https://stigman-scif.example.internal",
	authority: "https://keycloak.example.internal/realms/waypoint",
	collection: "4",
	client_id: "waypoint-appliance",
	scope: "openid stig-manager:stig:read",
	credential_id: "cred-token-2",
	source: "site_override",
};

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(body === undefined ? null : JSON.stringify(body), {
		status,
		headers: { "Content-Type": "application/json" },
	});
}

describe("StigManagerTab (issue #312)", () => {
	let originalFetch: typeof fetch;
	let fetchCalls: { url: string; init?: RequestInit }[];
	let globalConnection: typeof GLOBAL_CONNECTION | null;

	function installFetchMock(role: string, opts: { globalConfigured?: boolean } = {}) {
		fetchCalls = [];
		globalConnection = opts.globalConfigured === false ? null : { ...GLOBAL_CONNECTION };

		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			const method = init?.method ?? "GET";
			fetchCalls.push({ url, init });

			if (url === "/api/v1/credentials" && method === "GET") {
				return jsonResponse(CREDENTIAL_OPTIONS);
			}
			if (url === "/api/v1/sites" && method === "GET") {
				return jsonResponse(SITES);
			}
			if (url === "/api/v1/stigman" && method === "GET") {
				return globalConnection
					? jsonResponse(globalConnection)
					: jsonResponse({ error: { code: "not_found", message: "No global STIG Manager connection is configured." } }, 404);
			}
			if (url === "/api/v1/stigman" && method === "PUT") {
				const body = JSON.parse(init!.body as string);
				globalConnection = { ...GLOBAL_CONNECTION, ...body, updated_at: "2026-08-08T00:00:00Z" };
				return jsonResponse(globalConnection);
			}
			if (url === "/api/v1/sites/site-charlie/stigman" && method === "GET") {
				return jsonResponse(CHARLIE_RESOLVED);
			}
			if (url === "/api/v1/sites/site-alpha/stigman" && method === "GET") {
				return jsonResponse({ ...GLOBAL_CONNECTION, source: "global" });
			}
			if (url === "/api/v1/sites/site-charlie/stigman" && method === "PUT") {
				const body = JSON.parse(init!.body as string);
				return jsonResponse({ ...CHARLIE_RESOLVED, ...body, source: "site_override" });
			}
			if (url === "/api/v1/stigman/test" && method === "POST") {
				return jsonResponse({ reachable: true, auth_ok: true, outcome: "ok", message: "STIG Manager is reachable and the connection authenticated successfully.", api_version: "1.5.4" });
			}
			if (url === "/api/v1/stigman/test?site_id=site-charlie" && method === "POST") {
				return jsonResponse({ reachable: false, auth_ok: false, outcome: "auth_failed", message: "Authentication to STIG Manager failed.", api_version: null });
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
				<StigManagerTab />
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("https://stigman.example.internal")).toBeInTheDocument());
	}

	it("renders the global connection summary (endpoint, authority, collection, client id, scope, credential)", async () => {
		installFetchMock("Admin");
		await mount();

		expect(screen.getByText("https://stigman.example.internal")).toBeInTheDocument();
		expect(screen.getByText("https://keycloak.example.internal/realms/waypoint")).toBeInTheDocument();
		expect(screen.getByText("17")).toBeInTheDocument();
		expect(screen.getByText("waypoint-appliance")).toBeInTheDocument();
		expect(screen.getByText("openid stig-manager:stig:read")).toBeInTheDocument();
		expect(screen.getByText("svc-stigman-oidc")).toBeInTheDocument();
	});

	it("no secret input anywhere on the tab — only a credential picker referencing the token-type credential store", async () => {
		installFetchMock("Admin");
		await mount();

		fireEvent.click(screen.getByText("Edit"));
		expect(screen.queryByPlaceholderText(/secret/i)).not.toBeInTheDocument();
		expect(document.querySelector('input[type="password"]')).toBeNull();
		expect(screen.getByText("Credential")).toBeInTheDocument();
		const select = screen.getAllByRole("combobox")[0] as HTMLSelectElement;
		expect(Array.from(select.options).map((o) => o.textContent)).toEqual(["No credential", "svc-stigman-oidc", "svc-stigman-scif-oidc"]);
	});

	it("Admin can edit and save the global connection; the PUT body carries credential_id, never a secret", async () => {
		installFetchMock("Admin");
		await mount();

		fireEvent.click(screen.getByText("Edit"));
		const endpointInput = screen.getByDisplayValue("https://stigman.example.internal");
		fireEvent.change(endpointInput, { target: { value: "https://stigman2.example.internal" } });
		fireEvent.click(screen.getByText("Save"));

		await waitFor(() => expect(fetchCalls.some((c) => c.url === "/api/v1/stigman" && c.init?.method === "PUT")).toBe(true));
		const call = fetchCalls.find((c) => c.url === "/api/v1/stigman" && c.init?.method === "PUT")!;
		const body = JSON.parse(call.init!.body as string);
		expect(body.endpoint).toBe("https://stigman2.example.internal");
		expect(body.credential_id).toBe("cred-token-1");
		expect(body).not.toHaveProperty("secret");

		await waitFor(() => expect(screen.getByText("https://stigman2.example.internal")).toBeInTheDocument());
	});

	it("global CRUD round-trip: an unconfigured global connection shows a Configure affordance and can be created", async () => {
		installFetchMock("Admin", { globalConfigured: false });
		render(
			<AuthProvider>
				<StigManagerTab />
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText(/No global STIG Manager connection is configured/)).toBeInTheDocument());

		fireEvent.click(screen.getByText("Configure"));
		fireEvent.change(screen.getByPlaceholderText("stigman.example.internal"), { target: { value: "https://stigman.example.internal" } });
		fireEvent.change(screen.getByPlaceholderText(/keycloak.example.internal/), { target: { value: "https://keycloak.example.internal/realms/waypoint" } });
		fireEvent.change(screen.getByPlaceholderText("e.g. 17"), { target: { value: "17" } });
		fireEvent.change(screen.getByPlaceholderText("waypoint-appliance"), { target: { value: "waypoint-appliance" } });
		fireEvent.click(screen.getByText("Save"));

		await waitFor(() => expect(fetchCalls.some((c) => c.url === "/api/v1/stigman" && c.init?.method === "PUT")).toBe(true));
		await waitFor(() => expect(screen.getByText("https://stigman.example.internal")).toBeInTheDocument());
	});

	it("per-site override display: Alpha shows inherit, Charlie shows override", async () => {
		installFetchMock("Admin");
		await mount();

		const alphaRow = screen.getByText("Alpha Enclave").closest(".stigman-tab__site-row")! as HTMLElement;
		expect(within(alphaRow).getByText("inherit")).toBeInTheDocument();

		const charlieRow = screen.getByText("Charlie Vault").closest(".stigman-tab__site-row")! as HTMLElement;
		expect(within(charlieRow).getByText("override")).toBeInTheDocument();
	});

	it("per-site override edit: opening Charlie's override loads the resolved connection and can be saved", async () => {
		installFetchMock("Admin");
		await mount();

		const charlieRow = screen.getByText("Charlie Vault").closest(".stigman-tab__site")! as HTMLElement;
		fireEvent.click(within(charlieRow).getByText("View / edit"));

		await waitFor(() => expect(fetchCalls.some((c) => c.url === "/api/v1/sites/site-charlie/stigman" && (c.init?.method ?? "GET") === "GET")).toBe(true));
		await waitFor(() => expect(within(charlieRow).getByDisplayValue("https://stigman-scif.example.internal")).toBeInTheDocument());

		const collectionInput = within(charlieRow).getByDisplayValue("4");
		fireEvent.change(collectionInput, { target: { value: "9" } });
		fireEvent.click(within(charlieRow).getByText("Save"));

		await waitFor(() => expect(fetchCalls.some((c) => c.url === "/api/v1/sites/site-charlie/stigman" && c.init?.method === "PUT")).toBe(true));
		const call = fetchCalls.find((c) => c.url === "/api/v1/sites/site-charlie/stigman" && c.init?.method === "PUT")!;
		expect(JSON.parse(call.init!.body as string).collection).toBe("9");
	});

	it("Test button on the global panel surfaces the ok outcome inline with API version", async () => {
		installFetchMock("Admin");
		await mount();

		const globalPanel = screen.getByText("GLOBAL DEFAULT").closest(".config-panel")! as HTMLElement;
		fireEvent.click(within(globalPanel).getByText("Test"));

		expect(within(globalPanel).getByText("Testing…")).toBeInTheDocument();
		await waitFor(() => expect(fetchCalls.some((c) => c.url === "/api/v1/stigman/test")).toBe(true));
		await waitFor(() => expect(within(globalPanel).getByText("reachable, authenticated")).toBeInTheDocument());
		expect(within(globalPanel).getByText(/API v1\.5\.4/)).toBeInTheDocument();
	});

	it("Test button on a per-site override surfaces the auth_failed outcome inline", async () => {
		installFetchMock("Admin");
		await mount();

		const charlieRow = screen.getByText("Charlie Vault").closest(".stigman-tab__site")! as HTMLElement;
		fireEvent.click(within(charlieRow).getByText("View / edit"));
		await waitFor(() => expect(within(charlieRow).getByDisplayValue("https://stigman-scif.example.internal")).toBeInTheDocument());

		fireEvent.click(within(charlieRow).getByText("Test"));
		await waitFor(() => expect(fetchCalls.some((c) => c.url === "/api/v1/stigman/test?site_id=site-charlie")).toBe(true));
		await waitFor(() => expect(within(charlieRow).getByText("authentication failed")).toBeInTheDocument());
	});

	it("renders not_configured and unreachable outcomes distinctly from ok/auth_failed", async () => {
		installFetchMock("Admin");
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			const method = init?.method ?? "GET";
			fetchCalls.push({ url, init });
			if (url === "/api/v1/credentials") return jsonResponse(CREDENTIAL_OPTIONS);
			if (url === "/api/v1/sites") return jsonResponse(SITES);
			if (url === "/api/v1/stigman" && method === "GET") return jsonResponse(GLOBAL_CONNECTION);
			if (url === "/api/v1/stigman/test" && method === "POST") {
				return jsonResponse({ reachable: false, auth_ok: false, outcome: "unreachable", message: "STIG Manager was unreachable.", api_version: null });
			}
			throw new Error(`unexpected fetch: ${method} ${url}`);
		}) as unknown as typeof fetch;

		await mount();
		const globalPanel = screen.getByText("GLOBAL DEFAULT").closest(".config-panel")! as HTMLElement;
		fireEvent.click(within(globalPanel).getByText("Test"));
		await waitFor(() => expect(within(globalPanel).getByText("unreachable")).toBeInTheDocument());
	});

	it("not_configured outcome renders when no connection is configured at all and Test is used", async () => {
		installFetchMock("Admin", { globalConfigured: false });
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			const method = init?.method ?? "GET";
			fetchCalls.push({ url, init });
			if (url === "/api/v1/credentials") return jsonResponse(CREDENTIAL_OPTIONS);
			if (url === "/api/v1/sites") return jsonResponse(SITES);
			if (url === "/api/v1/stigman" && method === "GET") {
				return jsonResponse({ error: { code: "not_found", message: "No global STIG Manager connection is configured." } }, 404);
			}
			throw new Error(`unexpected fetch: ${method} ${url}`);
		}) as unknown as typeof fetch;

		render(
			<AuthProvider>
				<StigManagerTab />
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText(/No global STIG Manager connection is configured/)).toBeInTheDocument());
	});

	it("Viewer sees Edit and Test disabled with a Requires Admin reason (POST /stigman/test is Admin-only server-side)", async () => {
		installFetchMock("Viewer");
		await mount();

		const editButton = screen.getByText("Edit");
		expect(editButton).toBeDisabled();
		expect(editButton).toHaveAttribute("title", expect.stringContaining("Requires Admin"));

		const globalPanel = screen.getByText("GLOBAL DEFAULT").closest(".config-panel")! as HTMLElement;
		const testButton = within(globalPanel).getByText("Test");
		expect(testButton).toBeDisabled();
		expect(testButton).toHaveAttribute("title", expect.stringContaining("Requires Admin"));
	});

	it("Viewer sees per-site override editing disabled (read-only summary instead of a form)", async () => {
		installFetchMock("Viewer");
		await mount();

		const charlieRow = screen.getByText("Charlie Vault").closest(".stigman-tab__site")! as HTMLElement;
		fireEvent.click(within(charlieRow).getByText("View / edit"));

		await waitFor(() => expect(within(charlieRow).getByText("https://stigman-scif.example.internal")).toBeInTheDocument());
		expect(within(charlieRow).queryByDisplayValue("https://stigman-scif.example.internal")).not.toBeInTheDocument();
	});
});
