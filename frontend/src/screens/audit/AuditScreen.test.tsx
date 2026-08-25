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

// Issue #718: `secret.decrypted`'s `detail` carries `credential_name`
// (PR #775) alongside the pre-existing top-level `credential_id` — name
// resolvable, so the CREDENTIAL column should show the name with the id in
// the accessible tooltip.
const NAMED_CREDENTIAL_ENTRY = {
	id: "audit-3",
	event_type: "secret.decrypted",
	actor: "j.moreno",
	credential_id: "cred-named-1",
	job_id: "job-2",
	run_id: "run-2",
	detail: '{"master_key_id":"mk-1","credential_name":"vcenter-svc-account","credential_type":"vsphere"}',
	occurred_at: "2026-08-19T08:00:00Z",
};

// A decrypt event whose credential row was deleted between the two reads
// (PR #775's "best-effort" note) — `credential_name` absent, so the column
// must fall back to the bare id.
const UNNAMED_CREDENTIAL_ENTRY = {
	id: "audit-4",
	event_type: "secret.decrypted",
	actor: "j.moreno",
	credential_id: "cred-orphan-1",
	job_id: "job-3",
	run_id: "run-3",
	detail: '{"master_key_id":"mk-2"}',
	occurred_at: "2026-08-19T09:00:00Z",
};

function jsonResponse(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
	return new Response(JSON.stringify(body), {
		status,
		headers: { "Content-Type": "application/json", ...headers },
	});
}

describe("AuditScreen (issue #531)", () => {
	let originalFetch: typeof fetch;
	let fetchCalls: { url: string }[];

	function installFetchMock(options: { total?: number; csvBody?: string; entries?: typeof ENTRIES } = {}) {
		fetchCalls = [];
		const entries = options.entries ?? ENTRIES;
		const total = options.total ?? entries.length;

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
				return jsonResponse(entries, 200, { "X-Total-Count": String(total) });
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

	it("shows the credential name with the stable id in an accessible tooltip when resolvable (issue #718)", async () => {
		installFetchMock({ entries: [NAMED_CREDENTIAL_ENTRY, ENTRIES[1]] });
		await mount();

		const nameCell = await screen.findByText("vcenter-svc-account");
		expect(nameCell).toBeInTheDocument();
		expect(nameCell).toHaveAttribute("title", "id: cred-named-1");
		// The bare id must not also be rendered as visible text once a name resolves.
		expect(screen.queryByText("cred-named-1")).not.toBeInTheDocument();
	});

	it("falls back to the bare stable id when no credential name is resolvable (issue #718)", async () => {
		installFetchMock({ entries: [UNNAMED_CREDENTIAL_ENTRY, ENTRIES[1]] });
		await mount();

		const idCell = await screen.findByText("cred-orphan-1");
		expect(idCell).toBeInTheDocument();
		expect(idCell).toHaveAttribute("title", "cred-orphan-1");
	});

	it("renders no credential attribution for events with no credential_id (issue #718)", async () => {
		installFetchMock();
		await mount();

		const row = screen.getByText("credential.deleted").closest("tr");
		expect(row).not.toBeNull();
		// credential_id is null on this fixture; the CREDENTIAL cell renders the
		// same empty-value marker the RUN/JOB columns use, nothing else.
		const cells = row!.querySelectorAll("td");
		const credentialCell = cells[5];
		expect(credentialCell.textContent).toBe("—");
	});

	it("never renders secret-looking material in the credential column, even if detail carries decoy fields (issue #718)", async () => {
		// Fixture invented for this test only: `detail` deliberately includes
		// ciphertext/key-shaped decoy fields the real backend (PR #775) never
		// emits, to prove the renderer only ever reads `credential_name` and
		// never surfaces anything else from the JSON blob.
		const decoyEntry = {
			id: "audit-5",
			event_type: "secret.decrypted",
			actor: "j.moreno",
			credential_id: "cred-decoy-1",
			job_id: "job-4",
			run_id: "run-4",
			detail: JSON.stringify({
				master_key_id: "mk-3",
				credential_name: "prod-esxi-root",
				ciphertext: "SGVsbG8gc2VjcmV0IGRhdGE=",
				secret_value: "hunter2-super-secret",
				private_key: "-----BEGIN PRIVATE KEY-----abc123-----END PRIVATE KEY-----",
			}),
			occurred_at: "2026-08-19T10:00:00Z",
		};
		installFetchMock({ entries: [decoyEntry, ENTRIES[1]] });
		await mount();

		// Scoped to the CREDENTIAL cell specifically: the pre-existing DETAIL
		// column already renders the raw `detail` JSON verbatim (unrelated to
		// this issue), so a page-wide assertion would false-fail on that
		// column. The CREDENTIAL cell must only ever surface `credential_name`
		// (or the bare id) — never any other field from the blob.
		const nameCell = await screen.findByText("prod-esxi-root");
		expect(nameCell).toBeInTheDocument();
		expect(nameCell.textContent).toBe("prod-esxi-root");
		expect(nameCell.getAttribute("title")).toBe("id: cred-decoy-1");
		expect(nameCell.innerHTML).not.toContain("hunter2-super-secret");
		expect(nameCell.innerHTML).not.toContain("SGVsbG8gc2VjcmV0IGRhdGE=");
		expect(nameCell.innerHTML).not.toContain("BEGIN PRIVATE KEY");
	});
});
