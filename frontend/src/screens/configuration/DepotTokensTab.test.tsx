/**
 * DepotTokensTab tests (issue #571, completing #560/#39's frontend half).
 * Fixture/mock shape mirrors CredentialsTab.test.tsx (SSE test fixture,
 * step-up 403 stash) and ComplianceContentTab.test.tsx (queued-run polling,
 * duplicate-submission guard) — this tab reuses both hooks' underlying
 * machinery, so its tests exercise the same contracts through this screen's
 * surface instead of re-deriving them.
 */
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider, __resetAuthConfigCacheForTests } from "../../lib/auth";
import { SystemProvider } from "../../lib/system";
import { DepotTokensTab } from "./DepotTokensTab";
import type { Credential } from "./credentials";
import type { DownloadReadiness, ManagedToolInstall } from "./depot";

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(body === undefined ? null : JSON.stringify(body), {
		status,
		headers: { "Content-Type": "application/json" },
	});
}

/** One `job.state` SSE frame (mirrors CredentialsTab.test.tsx's `jobStateFrame`). */
function jobStateFrame(seq: number, runId: string, jobId: string, to: string, note?: string): string {
	const envelope = { seq, ts: "2026-08-08T00:00:00Z", type: "job.state", run_id: runId, job_id: jobId, data: { to, note } };
	return `id: ${seq}\ndata: ${JSON.stringify(envelope)}\n\n`;
}

/** SSE stub Response (mirrors CredentialsTab.test.tsx's `sseResponse`). */
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

const NO_TOKEN_READINESS: DownloadReadiness = {
	ready: false,
	depot_token_configured: false,
	missing_prerequisites: ["depot_token", "tool_not_installed"],
};

const READY_READINESS: DownloadReadiness = {
	ready: true,
	depot_token_configured: true,
	depot_token_health: "valid",
	tool_installed: true,
	missing_prerequisites: [],
};

const AUTH_FAILING_READINESS: DownloadReadiness = {
	ready: false,
	depot_token_configured: true,
	depot_token_health: "auth_failing",
	tool_installed: false,
	missing_prerequisites: ["depot_token_auth_failing", "tool_not_installed"],
};

const INSTALLS: ManagedToolInstall[] = [
	{
		id: "install-2",
		source: "local-repository",
		source_path: "vcf-download-tool/vcf-download-tool-1.4.2.tar.gz",
		version: "1.4.2",
		sha256: "abc123",
		outcome: "installed",
		initiated_by: "a.okafor",
		created_at: "2026-08-10T00:00:00Z",
	},
	{
		id: "install-1",
		source: "upload",
		source_path: "e3b0c-tool.tar.gz",
		outcome: "rejected",
		rejected_reason: "Signature does not match the Broadcom release public key.",
		initiated_by: "j.moreno",
		created_at: "2026-08-01T00:00:00Z",
	},
];

describe("DepotTokensTab (issue #571)", () => {
	let originalFetch: typeof fetch;
	let fetchCalls: { url: string; init?: RequestInit }[];
	let credentials: Credential[];
	let readiness: DownloadReadiness;
	let installs: ManagedToolInstall[];
	let installRunState: { state: string; job_count_failed: number; job_count_blocked: number };

	function installFetchMock(
		role: string,
		opts: {
			mode?: "connected" | "disconnected";
			credentials?: Credential[];
			readiness?: DownloadReadiness;
			installs?: ManagedToolInstall[];
		} = {},
	) {
		fetchCalls = [];
		credentials = opts.credentials ?? [];
		readiness = opts.readiness ?? NO_TOKEN_READINESS;
		installs = opts.installs ?? INSTALLS;
		installRunState = { state: "completed", job_count_failed: 0, job_count_blocked: 0 };

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
			if (url === "/api/v1/auth/config") {
				return jsonResponse({ local_auth_enabled: true, oidc_authority: "/auth/realms/waypoint", oidc_client_id: "waypoint-frontend" });
			}
			if (url === "/api/v1/credentials" && method === "GET") {
				return jsonResponse(credentials);
			}
			if (url === "/api/v1/credentials" && method === "POST") {
				const body = JSON.parse(init!.body as string);
				const created: Credential = {
					id: "cred-depot-new",
					name: body.name,
					credential_type: "depot-token",
					owner: "shared",
					health: "unknown",
					sudo_enabled: false,
					has_secret: Boolean(body.secret),
					used_by_job_count: 0,
					created_at: "2026-08-08T00:00:00Z",
					updated_at: "2026-08-08T00:00:00Z",
					username: body.username,
				};
				credentials = [...credentials, created];
				return jsonResponse(created, 201);
			}
			if (url.startsWith("/api/v1/credentials/") && method === "PUT") {
				const id = url.split("/").pop()!;
				const body = JSON.parse(init!.body as string);
				credentials = credentials.map((c) => (c.id === id ? { ...c, name: body.name, username: body.username } : c));
				return jsonResponse(credentials.find((c) => c.id === id));
			}
			if (url.startsWith("/api/v1/credentials/") && url.endsWith("/test") && method === "POST") {
				return jsonResponse({ run_id: "run-depot-test", job_id: "job-depot-test" }, 202);
			}
			if (url === "/api/v1/runs/run-depot-test/events") {
				const frame = jobStateFrame(1, "run-depot-test", "job-depot-test", "done", "Connection opened and closed successfully.");
				return sseResponse(frame, init?.signal);
			}
			if (url.match(/^\/api\/v1\/credentials\/(cred-[\w-]+)$/) && method === "GET") {
				const [, id] = url.match(/^\/api\/v1\/credentials\/(cred-[\w-]+)$/)!;
				return jsonResponse(credentials.find((c) => c.id === id));
			}
			if (url === "/api/v1/downloads/readiness" && method === "GET") {
				return jsonResponse(readiness);
			}
			if (url === "/api/v1/downloads/tool/installs" && method === "GET") {
				return jsonResponse(installs);
			}
			if (url === "/api/v1/downloads/tool/install" && method === "POST") {
				return jsonResponse({ run_id: "run-install-1", job_id: "job-install-1" }, 202);
			}
			if (url === "/api/v1/downloads/tool/upload" && method === "POST") {
				return jsonResponse({ run_id: "run-upload-1", job_id: "job-upload-1" }, 202);
			}
			if (url === "/api/v1/downloads/tool/fetch" && method === "POST") {
				return jsonResponse({ run_id: "run-fetch-1", job_id: "job-fetch-1" }, 202);
			}
			if (url === "/api/v1/runs/run-install-1" && method === "GET") {
				return jsonResponse({ id: "run-install-1", ...installRunState });
			}
			if (url === "/api/v1/runs/run-upload-1" && method === "GET") {
				return jsonResponse({ id: "run-upload-1", ...installRunState });
			}
			if (url === "/api/v1/runs/run-fetch-1" && method === "GET") {
				return jsonResponse({ id: "run-fetch-1", ...installRunState });
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
		__resetAuthConfigCacheForTests();
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
					<DepotTokensTab />
				</SystemProvider>
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("DEPOT CREDENTIAL")).toBeInTheDocument());
		await waitFor(() => expect(screen.getByText("DOWNLOAD READINESS")).toBeInTheDocument());
	}

	it("no depot token configured: never invents secret material, shows an empty-state and an Add action", async () => {
		installFetchMock("Admin");
		await mount();

		expect(screen.getByText(/No Broadcom Support Portal depot token is configured yet/)).toBeInTheDocument();
		expect(screen.getByText("Add depot token")).toBeInTheDocument();
		// Nothing resembling a secret value is ever present on the page.
		expect(document.body.textContent).not.toMatch(/s3cr3t|token-value/);
	});

	it("a configured depot token never displays the secret: masked metadata, health, last-tested/rotated only", async () => {
		installFetchMock("Admin", {
			credentials: [
				{
					id: "cred-depot-1",
					name: "Broadcom Support Portal",
					credential_type: "depot-token",
					owner: "shared",
					health: "valid",
					sudo_enabled: false,
					has_secret: true,
					used_by_job_count: 2,
					rotated_at: "2026-07-01T12:00:00Z",
					last_tested_at: "2026-08-01T09:00:00Z",
					created_at: "2026-01-01T00:00:00Z",
					updated_at: "2026-07-01T12:00:00Z",
					username: "svc-depot@example.internal",
				},
			],
		});
		await mount();

		expect(screen.getByText("svc-depot@example.internal")).toBeInTheDocument();
		expect(screen.getByText("valid")).toBeInTheDocument();
		expect(screen.getByText("2026-08-01 09:00Z")).toBeInTheDocument();
		expect(screen.getByText("2026-07-01 12:00Z")).toBeInTheDocument();
		expect(screen.getByText("Replace")).toBeInTheDocument();
		// No has_secret-derived token value, no raw secret, ever rendered.
		expect(screen.queryByDisplayValue(/./)).toBeNull(); // no text input carries a pre-filled value
	});

	it("expiry renders 'unknown' rather than inventing a date when expires_at is absent", async () => {
		installFetchMock("Admin", {
			credentials: [
				{
					id: "cred-depot-1",
					name: "Broadcom Support Portal",
					credential_type: "depot-token",
					owner: "shared",
					health: "valid",
					sudo_enabled: false,
					has_secret: true,
					used_by_job_count: 0,
					created_at: "2026-01-01T00:00:00Z",
					updated_at: "2026-01-01T00:00:00Z",
				},
			],
		});
		await mount();

		expect(screen.getByText("expiry unknown")).toBeInTheDocument();
	});

	it("expiry renders the real date with a warning when a real expires_at is present and near", async () => {
		const soon = new Date(Date.now() + 5 * 24 * 60 * 60 * 1000).toISOString();
		installFetchMock("Admin", {
			credentials: [
				{
					id: "cred-depot-1",
					name: "Broadcom Support Portal",
					credential_type: "depot-token",
					owner: "shared",
					health: "valid",
					sudo_enabled: false,
					has_secret: true,
					used_by_job_count: 0,
					expires_at: soon,
					created_at: "2026-01-01T00:00:00Z",
					updated_at: "2026-01-01T00:00:00Z",
				},
			],
		});
		await mount();

		expect(screen.getByText(/expires soon/)).toBeInTheDocument();
	});

	it("Admin can create the depot token; the request never round-trips a rendered secret", async () => {
		installFetchMock("Admin");
		await mount();

		fireEvent.click(screen.getByText("Add depot token"));
		fireEvent.change(screen.getByPlaceholderText("e.g. svc-depot@example.internal"), { target: { value: "svc-depot@example.internal" } });
		fireEvent.change(screen.getByPlaceholderText("required — never displayed again after saving"), { target: { value: "top-secret-token" } });
		fireEvent.click(screen.getByText("Save"));

		await waitFor(() => expect(fetchCalls.some((c) => c.url === "/api/v1/credentials" && c.init?.method === "POST")).toBe(true));
		const call = fetchCalls.find((c) => c.url === "/api/v1/credentials" && c.init?.method === "POST")!;
		const body = JSON.parse(call.init!.body as string);
		expect(body.secret).toBe("top-secret-token");
		expect(body.credential_type).toBe("depot-token");

		// After the successful save, the just-typed secret is never rendered anywhere.
		await waitFor(() => expect(screen.queryByText("Add depot token")).not.toBeInTheDocument());
		expect(document.body.textContent).not.toContain("top-secret-token");
	});

	it("a step_up_required replace redirects for fresh auth instead of showing a generic error, and never stashes the plaintext secret", async () => {
		installFetchMock("Admin", {
			credentials: [
				{
					id: "cred-depot-1",
					name: "Broadcom Support Portal",
					credential_type: "depot-token",
					owner: "shared",
					health: "valid",
					sudo_enabled: false,
					has_secret: true,
					used_by_job_count: 0,
					created_at: "2026-01-01T00:00:00Z",
					updated_at: "2026-01-01T00:00:00Z",
					username: "svc-depot@example.internal",
				},
			],
		});
		await mount();

		const baseFetch = globalThis.fetch;
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			if (url === "/auth/realms/waypoint/.well-known/openid-configuration") {
				return jsonResponse({
					authorization_endpoint: "/auth/realms/waypoint/protocol/openid-connect/auth",
					token_endpoint: "/auth/realms/waypoint/protocol/openid-connect/token",
				});
			}
			if (url === "/api/v1/credentials/cred-depot-1" && init?.method === "PUT") {
				return jsonResponse({ error: { code: "step_up_required", message: "This action requires you to re-authenticate first." } }, 403);
			}
			return (baseFetch as unknown as typeof globalThis.fetch)(input, init);
		}) as unknown as typeof fetch;

		let assignedUrl: string | undefined;
		const originalAssign = window.location.assign;
		Object.defineProperty(window, "location", {
			configurable: true,
			value: {
				origin: window.location.origin,
				pathname: window.location.pathname,
				search: window.location.search,
				assign: (url: string) => {
					assignedUrl = url;
				},
			},
		});

		try {
			fireEvent.click(screen.getByText("Replace"));
			fireEvent.change(screen.getByPlaceholderText("required — never displayed again after saving"), {
				target: { value: "rotated-secret-value" },
			});
			fireEvent.click(screen.getByText("Save"));

			await waitFor(() => expect(assignedUrl).toBeTruthy());
			expect(new URL(assignedUrl!, window.location.origin).searchParams.get("prompt")).toBe("login");
			expect(screen.queryByText("This action requires you to re-authenticate first.")).not.toBeInTheDocument();

			const rawStash = window.sessionStorage.getItem("waypoint.stepup.retry") as string;
			expect(rawStash).not.toContain("rotated-secret-value");
			const stashed = JSON.parse(rawStash);
			expect(stashed.kind).toBe("depot-token-replace");
			expect(stashed.payload).not.toHaveProperty("secret");
		} finally {
			Object.defineProperty(window, "location", { configurable: true, value: { ...window.location, assign: originalAssign } });
		}
	});

	it("Test follows the queued job via SSE to a terminal state and reports success", async () => {
		installFetchMock("Admin", {
			credentials: [
				{
					id: "cred-depot-1",
					name: "Broadcom Support Portal",
					credential_type: "depot-token",
					owner: "shared",
					health: "unknown",
					sudo_enabled: false,
					has_secret: true,
					used_by_job_count: 0,
					created_at: "2026-01-01T00:00:00Z",
					updated_at: "2026-01-01T00:00:00Z",
				},
			],
		});
		await mount();

		fireEvent.click(screen.getByText("Test"));
		await waitFor(() => expect(screen.getByText("Connection opened and closed successfully.")).toBeInTheDocument());
	});

	it("Viewer sees the credential panel read-only: Replace/Add and Test are disabled with an Admin-required reason", async () => {
		installFetchMock("Viewer");
		await mount();

		const addButton = screen.getByText("Add depot token") as HTMLButtonElement;
		expect(addButton.disabled).toBe(true);
		expect(addButton.title).toMatch(/Requires Admin/);
	});

	it("readiness: nothing configured reports both prerequisites missing with a plain-language reason for each", async () => {
		installFetchMock("Admin", { readiness: NO_TOKEN_READINESS });
		await mount();

		expect(screen.getByText("Not ready — see missing prerequisites below.")).toBeInTheDocument();
		expect(screen.getByText("not configured")).toBeInTheDocument();
		expect(screen.getByText(/unknown — no runner has reported yet/)).toBeInTheDocument();
	});

	it("readiness: token auth-failing is reported distinctly from 'not configured'", async () => {
		installFetchMock("Admin", { readiness: AUTH_FAILING_READINESS });
		await mount();

		expect(screen.getByText("configured, but authentication is failing")).toBeInTheDocument();
		expect(screen.queryByText("not configured")).not.toBeInTheDocument();
	});

	it("readiness: tool_installed omitted (unknown) renders distinctly from a real false", async () => {
		installFetchMock("Admin", {
			readiness: { ready: false, depot_token_configured: false, missing_prerequisites: ["depot_token", "tool_not_installed"] },
		});
		await mount();

		expect(screen.getByText(/unknown — no runner has reported yet/)).toBeInTheDocument();
	});

	it("readiness: fully ready reports both prerequisites satisfied", async () => {
		installFetchMock("Admin", { readiness: READY_READINESS });
		await mount();

		expect(screen.getByText("Ready — depot downloads can run.")).toBeInTheDocument();
		expect(screen.getByText("installed and verified")).toBeInTheDocument();
	});

	it("install history renders rejected attempts with their reason alongside installed ones, newest-first as returned", async () => {
		installFetchMock("Admin");
		await mount();

		expect(screen.getByText(/Signature does not match the Broadcom release public key\./)).toBeInTheDocument();
		const historyTable = document.querySelector(".config-table")!;
		expect(within(historyTable as HTMLElement).getByText(/rejected/)).toBeInTheDocument();
		expect(within(historyTable as HTMLElement).getByText(/installed/)).toBeInTheDocument();
	});

	it("Operator can install from the local repository; a second click while in flight is prevented (duplicate-submission)", async () => {
		installFetchMock("Operator");
		await mount();

		installRunState = { state: "running", job_count_failed: 0, job_count_blocked: 0 };
		fireEvent.change(screen.getByPlaceholderText("vcf-download-tool/vcf-download-tool-1.4.2.tar.gz"), {
			target: { value: "vcf-download-tool/vcf-download-tool-1.4.2.tar.gz" },
		});
		const installButton = screen.getByText("Install");
		fireEvent.click(installButton);
		fireEvent.click(installButton); // duplicate click while queued/running

		await waitFor(() => expect(fetchCalls.filter((c) => c.url === "/api/v1/downloads/tool/install").length).toBe(1));
		await waitFor(() => expect(screen.getByText("Install queued or running…")).toBeInTheDocument());

		installRunState = { state: "completed", job_count_failed: 0, job_count_blocked: 0 };
		await waitFor(() => expect(screen.getByText("Install succeeded.")).toBeInTheDocument(), { timeout: 6000 });
	}, 10000);

	it("a rejected/failed install keeps the failure feedback visible (aria-live, not color-only)", async () => {
		installFetchMock("Operator");
		await mount();

		installRunState = { state: "completed_with_failures", job_count_failed: 1, job_count_blocked: 0 };
		fireEvent.change(screen.getByPlaceholderText("vcf-download-tool/vcf-download-tool-1.4.2.tar.gz"), {
			target: { value: "vcf-download-tool/bad.tar.gz" },
		});
		fireEvent.click(screen.getByText("Install"));

		await waitFor(
			() => expect(screen.getByText("Last install attempt failed or was rejected — see history below.")).toBeInTheDocument(),
			{ timeout: 6000 },
		);
		const status = screen.getByText("Last install attempt failed or was rejected — see history below.");
		expect(status.getAttribute("aria-live")).toBe("polite");
	}, 10000);

	it("Viewer sees install actions disabled with an Operator-required reason, but readiness/history stay visible", async () => {
		installFetchMock("Viewer", { readiness: READY_READINESS });
		await mount();

		fireEvent.change(screen.getByPlaceholderText("vcf-download-tool/vcf-download-tool-1.4.2.tar.gz"), { target: { value: "x.tar.gz" } });
		const installButton = screen.getByText("Install") as HTMLButtonElement;
		expect(installButton.disabled).toBe(true);
		expect(installButton.title).toMatch(/Requires Operator/);

		expect(screen.getByText("DOWNLOAD READINESS")).toBeInTheDocument();
		expect(screen.getByText("installed and verified")).toBeInTheDocument();
	});

	it("depot-fetch action is disabled with a connected-mode reason while disconnected, even with a healthy credential", async () => {
		installFetchMock("Operator", { mode: "disconnected", readiness: READY_READINESS });
		await mount();

		const depotFetchButton = screen.getByRole("button", { name: "Fetch from depot" }) as HTMLButtonElement;
		expect(depotFetchButton.disabled).toBe(true);
		expect(depotFetchButton.title).toMatch(/connected mode/);
	});

	it("depot-fetch action is disabled with a credential reason when connected but no depot token is configured", async () => {
		installFetchMock("Operator", { mode: "connected", readiness: NO_TOKEN_READINESS });
		await mount();

		const depotFetchButton = screen.getByRole("button", { name: "Fetch from depot" }) as HTMLButtonElement;
		expect(depotFetchButton.disabled).toBe(true);
		expect(depotFetchButton.title).toMatch(/depot credential/);
	});

	it("depot-fetch action is disabled when the depot token is configured but auth-failing", async () => {
		installFetchMock("Operator", { mode: "connected", readiness: AUTH_FAILING_READINESS });
		await mount();

		const depotFetchButton = screen.getByRole("button", { name: "Fetch from depot" }) as HTMLButtonElement;
		expect(depotFetchButton.disabled).toBe(true);
		expect(depotFetchButton.title).toMatch(/depot credential/);
	});

	it("depot-fetch action is enabled when connected with a healthy depot credential, and queues a depot-sourced install", async () => {
		installFetchMock("Operator", { mode: "connected", readiness: READY_READINESS });
		await mount();

		const depotFetchButton = screen.getByRole("button", { name: "Fetch from depot" }) as HTMLButtonElement;
		expect(depotFetchButton.disabled).toBe(false);

		fireEvent.click(depotFetchButton);

		await waitFor(() => expect(fetchCalls.some((c) => c.url === "/api/v1/downloads/tool/fetch" && c.init?.method === "POST")).toBe(true));
		const call = fetchCalls.find((c) => c.url === "/api/v1/downloads/tool/fetch")!;
		expect(JSON.parse(call.init!.body as string)).toEqual({});

		installRunState = { state: "completed", job_count_failed: 0, job_count_blocked: 0 };
		await waitFor(() => expect(screen.getByText("Install succeeded.")).toBeInTheDocument(), { timeout: 6000 });
	}, 10000);

	it("depot-fetch action is disabled with an Operator-required reason for a Viewer, even when connected and healthy", async () => {
		installFetchMock("Viewer", { mode: "connected", readiness: READY_READINESS });
		await mount();

		const depotFetchButton = screen.getByRole("button", { name: "Fetch from depot" }) as HTMLButtonElement;
		expect(depotFetchButton.disabled).toBe(true);
		expect(depotFetchButton.title).toMatch(/Requires Operator/);
	});

	it("manual upload requires both the artifact and the signature file before Upload is enabled", async () => {
		installFetchMock("Operator");
		await mount();

		const uploadButton = within(screen.getByText("Manual upload").closest("form")!).getByText("Upload & install") as HTMLButtonElement;
		expect(uploadButton.disabled).toBe(true);
	});
});
