import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { AuditScreen } from "./AuditScreen";

const ENTRIES = [
	{
		id: "audit-1",
		event_type: "secret.decrypted",
		actor: "j.moreno",
		credential_id: "cred-1",
		job_id: "job-1",
		run_id: "run-1",
		detail: '{"target":"esxi-01.example.internal"}',
		occurred_at: "2026-08-18T12:00:00Z",
	},
	{
		id: "audit-2",
		event_type: "credential.deleted",
		actor: "r.alvarez",
		credential_id: null,
		job_id: null,
		run_id: null,
		detail: "{}",
		occurred_at: "2026-08-17T09:30:00Z",
	},
];

function jsonResponse(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
	return new Response(JSON.stringify(body), {
		status,
		headers: { "Content-Type": "application/json", ...headers },
	});
}

describe("AuditScreen (issue #531)", () => {
	let originalFetch: typeof fetch;
	let fetchCalls: { url: string }[];

	function installFetchMock(options: { total?: number; csvBody?: string } = {}) {
		fetchCalls = [];
		const total = options.total ?? ENTRIES.length;

		globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
			const url = typeof input === "string" ? input : input.toString();
			fetchCalls.push({ url });

			if (url.startsWith("/api/v1/audit")) {
				if (url.includes("format=csv")) {
					return new Response(options.csvBody ?? "id,event_type\naudit-1,secret.decrypted\n", {
						status: 200,
						headers: { "Content-Type": "text/csv" },
					});
				}
				return jsonResponse(ENTRIES, 200, { "X-Total-Count": String(total) });
			}
			throw new Error(`unexpected fetch: ${url}`);
		}) as unknown as typeof fetch;

		window.sessionStorage.setItem(
			"waypoint.session",
			JSON.stringify({
				token: "tok-1",
				username: "j.moreno",
				role: "Cyber",
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
		vi.restoreAllMocks();
	});

	async function mount() {
		render(
			<AuthProvider>
				<AuditScreen />
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("secret.decrypted")).toBeInTheDocument());
	}

	it("lists audit entries with kind, actor, run/job, and detail", async () => {
		installFetchMock();
		await mount();

		expect(screen.getByText("secret.decrypted")).toBeInTheDocument();
		expect(screen.getByText("credential.deleted")).toBeInTheDocument();
		expect(screen.getByText("j.moreno")).toBeInTheDocument();
		expect(screen.getByText("r.alvarez")).toBeInTheDocument();
		expect(screen.getByText("run-1")).toBeInTheDocument();
	});

	it("round-trips kind/actor/from/to filters into the query string", async () => {
		installFetchMock();
		await mount();

		fireEvent.change(screen.getByPlaceholderText("e.g. secret.decrypted"), { target: { value: "secret.decrypted" } });
		fireEvent.change(screen.getByPlaceholderText("e.g. j.moreno"), { target: { value: "j.moreno" } });
		fireEvent.click(screen.getByText("Apply"));

		await waitFor(() =>
			expect(fetchCalls.some((c) => c.url.includes("kind=secret.decrypted") && c.url.includes("actor=j.moreno"))).toBe(
				true,
			),
		);
	});

	it("clearing filters re-fetches with no filter params", async () => {
		installFetchMock();
		await mount();

		fireEvent.change(screen.getByPlaceholderText("e.g. secret.decrypted"), { target: { value: "secret.decrypted" } });
		fireEvent.click(screen.getByText("Apply"));
		await waitFor(() => expect(fetchCalls.some((c) => c.url.includes("kind=secret.decrypted"))).toBe(true));

		fireEvent.click(screen.getByText("Clear"));
		await waitFor(() => {
			const last = fetchCalls[fetchCalls.length - 1];
			expect(last.url).not.toContain("kind=");
		});
	});

	it("paginates via limit/offset and shows the X-Total-Count-derived range", async () => {
		installFetchMock({ total: 120 });
		await mount();

		expect(screen.getByText("1–50 of 120")).toBeInTheDocument();
		expect(screen.getByText("Previous")).toBeDisabled();
		expect(screen.getByText("Next")).not.toBeDisabled();

		fireEvent.click(screen.getByText("Next"));

		await waitFor(() => expect(fetchCalls.some((c) => c.url.includes("offset=50"))).toBe(true));
	});

	it("exports the current filter as CSV via a client-side download", async () => {
		installFetchMock();
		await mount();

		const createObjectURL = vi.fn(() => "blob:mock-url");
		const revokeObjectURL = vi.fn();
		vi.stubGlobal("URL", { ...URL, createObjectURL, revokeObjectURL });

		fireEvent.change(screen.getByPlaceholderText("e.g. secret.decrypted"), { target: { value: "secret.decrypted" } });
		fireEvent.click(screen.getByText("Apply"));
		await waitFor(() => expect(fetchCalls.some((c) => c.url.includes("kind=secret.decrypted"))).toBe(true));

		fireEvent.click(screen.getByText("Export CSV"));

		await waitFor(() =>
			expect(fetchCalls.some((c) => c.url.includes("format=csv") && c.url.includes("kind=secret.decrypted"))).toBe(
				true,
			),
		);
		await waitFor(() => expect(createObjectURL).toHaveBeenCalled());
	});
});
