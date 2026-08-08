import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { CredentialsTab } from "./CredentialsTab";
import type { Credential } from "./credentials";

const CREDENTIALS: Credential[] = [
	{
		id: "cred-1",
		name: "Alpha vCenter service account",
		credential_type: "vcenter",
		owner: "shared",
		health: "valid",
		sudo_enabled: false,
		has_secret: true,
		used_by_job_count: 3,
		rotated_at: "2026-07-01T12:00:00Z",
		created_at: "2026-01-01T00:00:00Z",
		updated_at: "2026-07-01T12:00:00Z",
		username: "svc-stig@example.internal",
	},
	{
		id: "cred-2",
		name: "svc-stig-vm",
		credential_type: "ssh",
		owner: "shared",
		health: "auth_failing",
		sudo_enabled: true,
		has_secret: true,
		used_by_job_count: 0,
		created_at: "2026-01-01T00:00:00Z",
		updated_at: "2026-01-01T00:00:00Z",
	},
];

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(body === undefined ? null : JSON.stringify(body), {
		status,
		headers: { "Content-Type": "application/json" },
	});
}

/** One `job.state` SSE frame in the same `id:`/`data:` grammar
 * lib/events.ts's `connectEventStream` parses (mirrors
 * liverun/LiveRunScreen.test.tsx's `frameText`). */
function jobStateFrame(seq: number, runId: string, jobId: string, to: string, note?: string): string {
	const envelope = {
		seq,
		ts: "2026-08-08T00:00:00Z",
		type: "job.state",
		run_id: runId,
		job_id: jobId,
		data: { to, note },
	};
	return `id: ${seq}\ndata: ${JSON.stringify(envelope)}\n\n`;
}

/** A `Response`-shaped stub whose body streams the given SSE frames once,
 * then hangs open until the request is aborted (matching a healthy
 * long-lived stream) — same shape LiveRunScreen.test.tsx uses for
 * `useLiveRun`'s `connectEventStream` calls. */
function sseResponse(frames: string, signal: AbortSignal | null | undefined): Response {
	const encoder = new TextEncoder();
	const chunk = encoder.encode(frames);
	let sent = false;
	const body = {
		getReader() {
			return {
				read(): Promise<{ value: Uint8Array | undefined; done: boolean }> {
					if (!sent) {
						sent = true;
						return Promise.resolve({ value: chunk, done: false });
					}
					return new Promise((_resolve, reject) => {
						if (signal?.aborted) {
							reject(new DOMException("aborted", "AbortError"));
							return;
						}
						signal?.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError")));
					});
				},
				releaseLock() {},
			};
		},
	};
	return { ok: true, status: 200, body } as unknown as Response;
}

describe("CredentialsTab (issue #247)", () => {
	let originalFetch: typeof fetch;
	let fetchCalls: { url: string; init?: RequestInit }[];
	let credentials: Credential[];

	/**
	 * Per-credential test-job fixture: `POST .../test` returns the queued
	 * `202 {run_id, job_id}` (issue #245/#322), and the run's SSE stream
	 * delivers one `job.state` frame carrying `to`/`note` for that job — the
	 * terminal event `doTest` waits on. `health` is what the row is expected
	 * to read as once `GET /credentials/{id}` is re-fetched after that
	 * terminal event, matching how `CredentialTestJobHandler` actually flips
	 * `credentials.health` server-side (not the 202 response itself).
	 */
	const TEST_JOBS: Record<string, { runId: string; jobId: string; to: string; note: string; health: string }> = {
		"cred-1": { runId: "run-cred-1", jobId: "job-cred-1", to: "done", note: "Connection opened and closed successfully.", health: "valid" },
		"cred-2": {
			runId: "run-cred-2",
			jobId: "job-cred-2",
			to: "auth-failed",
			note: "No secret is stored for this credential.",
			health: "auth_failing",
		},
	};

	function installFetchMock(role: string) {
		fetchCalls = [];
		credentials = CREDENTIALS.map((c) => ({ ...c }));

		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			const method = init?.method ?? "GET";
			fetchCalls.push({ url, init });

			if (url === "/api/v1/credentials" && method === "GET") {
				return jsonResponse(credentials);
			}
			if (url === "/api/v1/credentials" && method === "POST") {
				const body = JSON.parse(init!.body as string);
				const created: Credential = {
					id: "cred-new",
					name: body.name,
					credential_type: body.credential_type,
					owner: "shared",
					health: "unknown",
					sudo_enabled: body.sudo_enabled ?? false,
					has_secret: Boolean(body.secret),
					used_by_job_count: 0,
					created_at: "2026-08-08T00:00:00Z",
					updated_at: "2026-08-08T00:00:00Z",
					username: body.username,
				};
				credentials = [...credentials, created];
				return jsonResponse(created, 201);
			}
			if (url.startsWith("/api/v1/credentials/") && url.endsWith("/test") && method === "POST") {
				const id = url.split("/")[4];
				const job = TEST_JOBS[id];
				if (!job) {
					throw new Error(`no test-job fixture for credential ${id}`);
				}
				return jsonResponse({ run_id: job.runId, job_id: job.jobId }, 202);
			}
			if (url.match(/^\/api\/v1\/runs\/(run-cred-\d+)\/events$/)) {
				const [, runId] = url.match(/^\/api\/v1\/runs\/(run-cred-\d+)\/events$/)!;
				const job = Object.values(TEST_JOBS).find((j) => j.runId === runId)!;
				const frame = jobStateFrame(1, job.runId, job.jobId, job.to, job.note);
				return sseResponse(frame, init?.signal);
			}
			if (url.match(/^\/api\/v1\/credentials\/(cred-\d+)$/) && method === "GET") {
				const [, id] = url.match(/^\/api\/v1\/credentials\/(cred-\d+)$/)!;
				const job = TEST_JOBS[id];
				if (job) {
					credentials = credentials.map((c) => (c.id === id ? { ...c, health: job.health } : c));
				}
				return jsonResponse(credentials.find((c) => c.id === id));
			}
			if (url.startsWith("/api/v1/credentials/") && method === "PUT") {
				const id = url.split("/").pop()!;
				const body = JSON.parse(init!.body as string);
				credentials = credentials.map((c) => (c.id === id ? { ...c, name: body.name, username: body.username } : c));
				return jsonResponse(credentials.find((c) => c.id === id));
			}
			if (url.startsWith("/api/v1/credentials/") && method === "DELETE") {
				const id = url.split("/").pop()!;
				if (id === "cred-1") {
					return jsonResponse(
						{ error: { code: "credential_in_use", message: "Jobs or runs still reference this credential." } },
						409,
					);
				}
				credentials = credentials.filter((c) => c.id !== id);
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
				<CredentialsTab />
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("Alpha vCenter service account")).toBeInTheDocument());
	}

	it("renders the table with type, owner always 'shared', health badge, used-by count, and last rotated", async () => {
		installFetchMock("Admin");
		await mount();

		expect(screen.getByText("svc-stig-vm")).toBeInTheDocument();
		expect(screen.getAllByText("shared")).toHaveLength(2);
		expect(screen.getByText("valid")).toBeInTheDocument();
		expect(screen.getByText("auth failing")).toBeInTheDocument();
		expect(screen.getByText("3")).toBeInTheDocument();
		expect(screen.getByText("2026-07-01 12:00Z")).toBeInTheDocument();
		// cred-2 has never been rotated.
		expect(screen.getAllByText("—").length).toBeGreaterThan(0);
	});

	it("no personal-credential UI anywhere: owner is always 'shared', never rendered as an editable field", async () => {
		installFetchMock("Admin");
		await mount();

		expect(screen.queryByText(/personal/i)).not.toBeInTheDocument();
		fireEvent.click(screen.getByText("Add credential"));
		// The create form has no owner field of any kind.
		expect(screen.queryByLabelText(/owner/i)).not.toBeInTheDocument();
	});

	it("Admin can create a credential; the request never includes an 'owner' field", async () => {
		installFetchMock("Admin");
		await mount();

		fireEvent.click(screen.getByText("Add credential"));
		fireEvent.change(screen.getByPlaceholderText("e.g. Alpha vCenter service account"), {
			target: { value: "New Cred" },
		});
		fireEvent.change(screen.getByPlaceholderText("required to enable Test"), { target: { value: "s3cr3t" } });
		fireEvent.click(screen.getByText("Save"));

		await waitFor(() => expect(fetchCalls.some((c) => c.url === "/api/v1/credentials" && c.init?.method === "POST")).toBe(true));
		const call = fetchCalls.find((c) => c.url === "/api/v1/credentials" && c.init?.method === "POST")!;
		const body = JSON.parse(call.init!.body as string);
		expect(body.name).toBe("New Cred");
		expect(body.secret).toBe("s3cr3t");
		expect(body).not.toHaveProperty("owner");
	});

	it("the sudo_enabled toggle is only shown for credential_type ssh", async () => {
		installFetchMock("Admin");
		await mount();

		fireEvent.click(screen.getByText("Add credential"));
		expect(screen.queryByText("Sudo enabled")).not.toBeInTheDocument();

		const typeSelect = screen.getByDisplayValue("vCenter") as HTMLSelectElement;
		fireEvent.change(typeSelect, { target: { value: "ssh" } });
		expect(screen.getByText("Sudo enabled")).toBeInTheDocument();
	});

	it("edit form never pre-fills the secret field, and the secret is cleared from state after a successful submit", async () => {
		installFetchMock("Admin");
		await mount();

		const row = screen.getByText("Alpha vCenter service account").closest("tr")!;
		fireEvent.click(within(row).getByText("Edit"));

		const secretInput = screen.getByPlaceholderText("leave blank to keep current secret") as HTMLInputElement;
		expect(secretInput.value).toBe("");
		expect(secretInput.type).toBe("password");

		fireEvent.change(secretInput, { target: { value: "rotated-secret" } });
		fireEvent.click(screen.getByText("Save"));

		await waitFor(() => expect(fetchCalls.some((c) => c.url === "/api/v1/credentials/cred-1" && c.init?.method === "PUT")).toBe(true));
		const call = fetchCalls.find((c) => c.url === "/api/v1/credentials/cred-1" && c.init?.method === "PUT")!;
		expect(JSON.parse(call.init!.body as string).secret).toBe("rotated-secret");

		// The edit form closes and re-opening it (or opening any other) never
		// shows the just-typed secret again — form state was reset, not reused.
		await waitFor(() => expect(screen.queryByPlaceholderText("leave blank to keep current secret")).not.toBeInTheDocument());
		fireEvent.click(within(row).getByText("Edit"));
		const reopened = screen.getByPlaceholderText("leave blank to keep current secret") as HTMLInputElement;
		expect(reopened.value).toBe("");
	});

	it("no secret value is ever rendered as visible text anywhere in the document after submit", async () => {
		installFetchMock("Admin");
		await mount();

		const row = screen.getByText("Alpha vCenter service account").closest("tr")!;
		fireEvent.click(within(row).getByText("Edit"));
		fireEvent.change(screen.getByPlaceholderText("leave blank to keep current secret"), {
			target: { value: "super-secret-value-xyz" },
		});
		fireEvent.click(screen.getByText("Save"));

		await waitFor(() => expect(fetchCalls.some((c) => c.url === "/api/v1/credentials/cred-1" && c.init?.method === "PUT")).toBe(true));
		expect(screen.queryByText("super-secret-value-xyz")).not.toBeInTheDocument();
		expect(screen.queryByDisplayValue("super-secret-value-xyz")).not.toBeInTheDocument();
	});

	it("Test action calls POST /credentials/{id}/test (202), shows a spinner while the job runs, then follows it to done and updates the pill (issue #323)", async () => {
		installFetchMock("Admin");
		await mount();

		const row = screen.getByText("Alpha vCenter service account").closest("tr")!;
		// Starts as "valid" per the CREDENTIALS fixture — the regression this
		// test guards is the pill silently freezing after a 202 response.
		expect(within(row).getByText("valid")).toBeInTheDocument();

		fireEvent.click(within(row).getByText("Test"));

		expect(within(row).getByText("Testing…")).toBeInTheDocument();
		await waitFor(() => expect(fetchCalls.some((c) => c.url === "/api/v1/credentials/cred-1/test")).toBe(true));
		// The 202 body carries no outcome; the job is followed over the run's
		// SSE stream to its terminal job.state.
		await waitFor(() => expect(fetchCalls.some((c) => c.url === "/api/v1/runs/run-cred-1/events")).toBe(true));
		await waitFor(() => expect(within(row).getByText("Connection opened and closed successfully.")).toBeInTheDocument());
		// Terminal state re-fetches GET /credentials/cred-1 — the health pill's
		// source of truth — and the spinner clears.
		await waitFor(() => expect(fetchCalls.some((c) => c.url === "/api/v1/credentials/cred-1" && c.init?.method !== "DELETE")).toBe(true));
		await waitFor(() => expect(within(row).queryByText("Testing…")).not.toBeInTheDocument());
		expect(within(row).getByText("valid")).toBeInTheDocument();
	});

	it("an auth-failed job terminal state surfaces the auth_failing health and message on its own row", async () => {
		installFetchMock("Admin");
		await mount();

		const row = screen.getByText("svc-stig-vm").closest("tr")!;
		expect(within(row).getByText("auth failing")).toBeInTheDocument();

		fireEvent.click(within(row).getByText("Test"));

		await waitFor(() => expect(within(row).getByText("No secret is stored for this credential.")).toBeInTheDocument());
		await waitFor(() => expect(within(row).queryByText("Testing…")).not.toBeInTheDocument());
		expect(within(row).getByText("auth failing")).toBeInTheDocument();
	});

	it("a 4xx/5xx from POST /credentials/{id}/test itself (queueing failure) surfaces a message without ever following a job", async () => {
		installFetchMock("Admin");
		await mount();

		// Point Test at a credential id with no TEST_JOBS fixture — the mock
		// throws, standing in for a queueing-time failure (e.g. 404/500) that
		// never reaches the job-follow branch at all.
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			if (url === "/api/v1/credentials" && (init?.method ?? "GET") === "GET") {
				return jsonResponse(credentials);
			}
			if (url === "/api/v1/credentials/cred-1/test") {
				return jsonResponse({ error: { code: "not_found", message: "No credential exists with id 'cred-1'." } }, 404);
			}
			throw new Error(`unexpected fetch: ${url}`);
		}) as unknown as typeof fetch;

		const row = screen.getByText("Alpha vCenter service account").closest("tr")!;
		fireEvent.click(within(row).getByText("Test"));

		await waitFor(() => expect(within(row).getByText(/No credential exists with id/)).toBeInTheDocument());
		await waitFor(() => expect(within(row).queryByText("Testing…")).not.toBeInTheDocument());
	});

	it("Delete surfaces the 409 credential_in_use error clearly", async () => {
		installFetchMock("Admin");
		vi.spyOn(window, "confirm").mockReturnValue(true);
		await mount();

		const row = screen.getByText("Alpha vCenter service account").closest("tr")!;
		fireEvent.click(within(row).getByText("Delete"));

		await waitFor(() => expect(screen.getByText(/still referenced by jobs or runs/i)).toBeInTheDocument());
		// The credential is still present — the 409 did not silently remove it.
		expect(screen.getByText("Alpha vCenter service account")).toBeInTheDocument();
	});

	it("Admin can delete a credential with no in-use conflict", async () => {
		installFetchMock("Admin");
		vi.spyOn(window, "confirm").mockReturnValue(true);
		await mount();

		const row = screen.getByText("svc-stig-vm").closest("tr")!;
		fireEvent.click(within(row).getByText("Delete"));

		await waitFor(() => expect(screen.queryByText("svc-stig-vm")).not.toBeInTheDocument());
	});

	it("Viewer sees Add credential / Edit / Delete disabled with a Requires Admin reason, not hidden; Test stays enabled", async () => {
		installFetchMock("Viewer");
		await mount();

		const addButton = screen.getByText("Add credential");
		expect(addButton).toBeDisabled();
		expect(addButton).toHaveAttribute("title", expect.stringContaining("Requires Admin"));

		const row = screen.getByText("Alpha vCenter service account").closest("tr")!;
		expect(within(row).getByText("Edit")).toBeDisabled();
		expect(within(row).getByText("Delete")).toBeDisabled();
		expect(within(row).getByText("Test")).not.toBeDisabled();
	});

	it("unmounting mid-test tears down the SSE stream and never sets state afterward (issue #324 finding 1)", async () => {
		installFetchMock("Admin");
		const consoleError = vi.spyOn(console, "error").mockImplementation(() => {});

		// A stream that never delivers a terminal frame and hangs open, so the
		// component is guaranteed to still be "testing" (stream open, no
		// setState yet) at the moment of unmount — the exact window the
		// blocker described (leaked stream + late setState).
		let streamAborted = false;
		let releaseHang: (() => void) | undefined;
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			const method = init?.method ?? "GET";
			if (url === "/api/v1/credentials" && method === "GET") {
				return jsonResponse(CREDENTIALS.map((c) => ({ ...c })));
			}
			if (url === "/api/v1/credentials/cred-1/test") {
				return jsonResponse({ run_id: "run-cred-1", job_id: "job-cred-1" }, 202);
			}
			if (url === "/api/v1/runs/run-cred-1/events") {
				const signal = init?.signal;
				const body = {
					getReader() {
						return {
							read(): Promise<{ value: Uint8Array | undefined; done: boolean }> {
								return new Promise((_resolve, reject) => {
									if (signal?.aborted) {
										streamAborted = true;
										reject(new DOMException("aborted", "AbortError"));
										return;
									}
									signal?.addEventListener("abort", () => {
										streamAborted = true;
										reject(new DOMException("aborted", "AbortError"));
									});
									releaseHang = () => reject(new DOMException("aborted", "AbortError"));
								});
							},
							releaseLock() {},
						};
					},
				};
				return { ok: true, status: 200, body } as unknown as Response;
			}
			throw new Error(`unexpected fetch: ${method} ${url}`);
		}) as unknown as typeof fetch;

		window.sessionStorage.setItem(
			"waypoint.session",
			JSON.stringify({
				token: "tok-1",
				username: "j.moreno",
				role: "Admin",
				expiresAt: new Date(Date.now() + 60_000).toISOString(),
			}),
		);

		const { unmount } = render(
			<AuthProvider>
				<CredentialsTab />
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("Alpha vCenter service account")).toBeInTheDocument());

		const row = screen.getByText("Alpha vCenter service account").closest("tr")!;
		fireEvent.click(within(row).getByText("Test"));
		await waitFor(() => expect(within(row).getByText("Testing…")).toBeInTheDocument());
		// The stream is open (POST /test resolved, SSE GET issued) — this is
		// the mid-test window the finding describes.
		await waitFor(() => expect(releaseHang).toBeDefined());

		unmount();

		// The unmount-scoped cleanup must abort the still-open stream rather
		// than leaving it to leak.
		await waitFor(() => expect(streamAborted).toBe(true));
		// No React "state update on an unmounted component" warning fired —
		// i.e. nothing in the (now-aborted) stream's callbacks touched state
		// after unmount.
		expect(consoleError).not.toHaveBeenCalledWith(expect.stringContaining("unmounted component"));

		consoleError.mockRestore();
	});

	it("the credential_type dropdown is restricted to exactly vcenter/nsx/ssh/token", async () => {
		installFetchMock("Admin");
		await mount();

		fireEvent.click(screen.getByText("Add credential"));
		const select = screen.getByDisplayValue("vCenter") as HTMLSelectElement;
		const values = Array.from(select.options).map((o) => o.value);
		expect(values).toEqual(["vcenter", "nsx", "ssh", "token"]);
	});
});
