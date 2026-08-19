import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { UsersTab } from "./UsersTab";

const USERS = [
	{
		id: "user-1",
		oidc_sub: "sub-1",
		username: "j.moreno",
		role: "Admin",
		site_scope: "[]",
		auth_method: "PIV/CAC",
		last_seen_at: "2026-08-18T12:00:00Z",
		created_at: "2026-01-01T00:00:00Z",
		updated_at: "2026-08-18T12:00:00Z",
	},
	{
		id: "user-2",
		oidc_sub: "sub-2",
		username: "r.alvarez",
		role: "Cyber",
		site_scope: '["site-alpha"]',
		auth_method: "LDAP",
		last_seen_at: "2026-08-10T09:30:00Z",
		created_at: "2026-01-01T00:00:00Z",
		updated_at: "2026-08-10T09:30:00Z",
	},
];

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(body === undefined ? null : JSON.stringify(body), {
		status,
		headers: { "Content-Type": "application/json" },
	});
}

describe("UsersTab (issue #32)", () => {
	let originalFetch: typeof fetch;
	let fetchCalls: { url: string; init?: RequestInit }[];
	let users: typeof USERS;

	function installFetchMock(role: string) {
		fetchCalls = [];
		users = USERS.map((u) => ({ ...u }));

		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			const method = init?.method ?? "GET";
			fetchCalls.push({ url, init });

			if (url === "/api/v1/users" && method === "GET") {
				return jsonResponse(users);
			}
			const putMatch = url.match(/^\/api\/v1\/users\/(user-\d)$/);
			if (putMatch && method === "PUT") {
				const body = JSON.parse(init!.body as string);
				const id = putMatch[1];
				users = users.map((u) => (u.id === id ? { ...u, site_scope: body.site_scope, updated_at: "2026-08-19T00:00:00Z" } : u));
				return jsonResponse(users.find((u) => u.id === id));
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
				<UsersTab />
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("j.moreno")).toBeInTheDocument());
	}

	it("lists users with role pill, site scope, auth method, last seen, and role capability restatement", async () => {
		installFetchMock("Admin");
		await mount();

		expect(screen.getByText("j.moreno")).toBeInTheDocument();
		expect(screen.getByText("r.alvarez")).toBeInTheDocument();
		expect(screen.getByText("Admin")).toBeInTheDocument();
		expect(screen.getByText("Cyber")).toBeInTheDocument();
		expect(screen.getByText("site-alpha")).toBeInTheDocument();
		expect(screen.getByText("All sites")).toBeInTheDocument();
		expect(screen.getByText("PIV/CAC")).toBeInTheDocument();
		expect(screen.getByText("LDAP")).toBeInTheDocument();
		expect(screen.getByText(/Everything: sites, targets/)).toBeInTheDocument();
		expect(screen.getByText(/Viewer \+ initiate scans/)).toBeInTheDocument();
	});

	it("never offers a role edit control anywhere on the tab", async () => {
		installFetchMock("Admin");
		await mount();

		expect(screen.queryAllByRole("combobox")).toHaveLength(0);
		expect(screen.getByText(/Role reflects Keycloak group membership/)).toBeInTheDocument();
	});

	it("Admin can edit and save site scope; the PUT body carries only site_scope", async () => {
		installFetchMock("Admin");
		await mount();

		const editButtons = screen.getAllByText("Edit scope");
		fireEvent.click(editButtons[1]); // r.alvarez row
		const input = screen.getByPlaceholderText('[] or ["site-id"]');
		fireEvent.change(input, { target: { value: '["site-bravo"]' } });
		fireEvent.click(screen.getByText("Save"));

		await waitFor(() => expect(fetchCalls.some((c) => c.url === "/api/v1/users/user-2" && c.init?.method === "PUT")).toBe(true));
		const call = fetchCalls.find((c) => c.url === "/api/v1/users/user-2" && c.init?.method === "PUT")!;
		const body = JSON.parse(call.init!.body as string);
		expect(body).toEqual({ site_scope: '["site-bravo"]' });

		await waitFor(() => expect(screen.getByText("site-bravo")).toBeInTheDocument());
	});

	it("rejects a non-array site scope before submitting", async () => {
		installFetchMock("Admin");
		await mount();

		fireEvent.click(screen.getAllByText("Edit scope")[1]);
		const input = screen.getByPlaceholderText('[] or ["site-id"]');
		fireEvent.change(input, { target: { value: '{"not":"an array"}' } });
		fireEvent.click(screen.getByText("Save"));

		expect(await screen.findByText(/must be a JSON array/)).toBeInTheDocument();
		expect(fetchCalls.some((c) => c.init?.method === "PUT")).toBe(false);
	});

	it("Viewer sees the Edit scope action visibly disabled rather than hidden (roleGateProps)", async () => {
		installFetchMock("Viewer");
		await mount();

		const editButtons = screen.getAllByText("Edit scope");
		expect(editButtons.length).toBe(2);
		for (const button of editButtons) {
			expect(button).toBeDisabled();
			expect(button).toHaveAttribute("title", expect.stringContaining("Requires Admin"));
		}
	});
});
