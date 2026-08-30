/**
 * RunHistoryDeletionPanel — issue #592, epic #588's last child. Covers:
 * typed-DELETE confirmation gating, Admin role gating, non-terminal-run
 * gating, the successful deletion flow, the 409 `requires_domain_purge_first`
 * request-error path (the honest refusal message routing back to purge), the
 * tombstone rendering once deleted, and loading an already-deleted run's
 * tombstone on mount without firing a new DELETE. Mirrors
 * `PurgeRunPanel.test.tsx`'s structure/mocking idiom.
 */
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { RunHistoryDeletionPanel } from "./RunHistoryDeletionPanel";
import type { RunListItem } from "./results";

const TERMINAL_RUN: RunListItem = {
	id: "RUN-2026-0802-0412",
	run_type: "scan",
	state: "completed",
	scope: JSON.stringify({ site_id: "Alpha Enclave" }),
	initiated_by: "j.moreno",
	created_at: "2026-08-02T04:12:00Z",
	started_at: "2026-08-02T04:12:00Z",
	completed_at: "2026-08-02T04:23:39Z",
	job_count: 2,
	job_count_queued: 0,
	job_count_running: 0,
	job_count_completed: 2,
	job_count_failed: 0,
	job_count_blocked: 0,
	coverage_incomplete: false,
};

const NON_TERMINAL_RUN: RunListItem = { ...TERMINAL_RUN, id: "RUN-RUNNING", state: "running" };

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(body === undefined ? null : JSON.stringify(body), {
		status,
		headers: { "Content-Type": "application/json" },
	});
}

function errorResponse(status: number, code: string, message: string): Response {
	return jsonResponse({ error: { code, message } }, status);
}

interface FetchCall {
	url: string;
	method: string;
}

function mount(role: "Viewer" | "Cyber" | "Operator" | "Admin", run: RunListItem) {
	sessionStorage.setItem(
		"waypoint.session",
		JSON.stringify({ token: "tok", username: "j.moreno", role, expiresAt: new Date(Date.now() + 3600_000).toISOString() }),
	);
	return render(
		<AuthProvider>
			<RunHistoryDeletionPanel run={run} />
		</AuthProvider>,
	);
}

describe("RunHistoryDeletionPanel", () => {
	let fetchCalls: FetchCall[];

	beforeEach(() => {
		sessionStorage.clear();
		fetchCalls = [];
	});

	afterEach(() => {
		vi.restoreAllMocks();
	});

	function installFetchMock(handler: (url: string, method: string) => Response | Promise<Response>) {
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			const method = init?.method ?? "GET";
			fetchCalls.push({ url, method });
			return handler(url, method);
		}) as unknown as typeof fetch;
	}

	it("gates the action to Admin with a reason, and does not enable it for a non-terminal run", async () => {
		installFetchMock(() => errorResponse(404, "not_found", "no deletion requested"));
		mount("Operator", TERMINAL_RUN);

		const button = await screen.findByText("Delete history…");
		expect((button as HTMLButtonElement).disabled).toBe(true);
		expect((button as HTMLButtonElement).title).toMatch(/Requires Admin/);
	});

	it("disables deletion for a non-terminal run even for Admin, with a reason", async () => {
		installFetchMock(() => errorResponse(404, "not_found", "no deletion requested"));
		mount("Admin", NON_TERMINAL_RUN);

		const button = await screen.findByText("Delete history…");
		expect((button as HTMLButtonElement).disabled).toBe(true);
		expect((button as HTMLButtonElement).title).toMatch(/terminal state/);
	});

	it("requires the literal DELETE confirmation before the submit button enables", async () => {
		installFetchMock(() => errorResponse(404, "not_found", "no deletion requested"));
		mount("Admin", TERMINAL_RUN);

		fireEvent.click(await screen.findByText("Delete history…"));
		const submit = (await screen.findByText("Delete history")) as HTMLButtonElement;
		expect(submit.disabled).toBe(true);

		const input = screen.getByLabelText("Type DELETE to confirm");
		fireEvent.change(input, { target: { value: "delete" } });
		expect(submit.disabled).toBe(true); // wrong case is not accepted

		fireEvent.change(input, { target: { value: "DELETE" } });
		expect(submit.disabled).toBe(false);
	});

	it("runs the successful deletion flow and renders the tombstone", async () => {
		installFetchMock((url, method) => {
			if (url.endsWith("/history") && method === "GET") {
				return errorResponse(404, "not_found", "no deletion requested");
			}
			if (url.endsWith("/history") && method === "DELETE") {
				return jsonResponse({
					run_id: TERMINAL_RUN.id,
					outcome: "Completed",
					actor: "j.moreno",
					prior_state: "completed",
					occurred_at: "2026-08-24T00:00:00Z",
				});
			}
			throw new Error(`Unhandled: ${method} ${url}`);
		});

		mount("Admin", TERMINAL_RUN);
		fireEvent.click(await screen.findByText("Delete history…"));
		fireEvent.change(screen.getByLabelText("Type DELETE to confirm"), { target: { value: "DELETE" } });
		fireEvent.click(screen.getByText("Delete history"));

		await waitFor(() => expect(screen.getByText("OPERATIONAL HISTORY DELETED")).toBeInTheDocument());
		expect(screen.getByText(/deleted by j\.moreno/)).toBeInTheDocument();
		expect(screen.getByText(/prior/)).toBeInTheDocument();
	});

	it("surfaces the 409 requires_domain_purge_first refusal with an honest message routing back to purge", async () => {
		installFetchMock((url, method) => {
			if (url.endsWith("/history") && method === "GET") {
				return errorResponse(404, "not_found", "no deletion requested");
			}
			if (url.endsWith("/history") && method === "DELETE") {
				return errorResponse(409, "requires_domain_purge_first", "Run must be purged before its history can be deleted.");
			}
			throw new Error(`Unhandled: ${method} ${url}`);
		});

		mount("Admin", TERMINAL_RUN);
		fireEvent.click(await screen.findByText("Delete history…"));
		fireEvent.change(screen.getByLabelText("Type DELETE to confirm"), { target: { value: "DELETE" } });
		fireEvent.click(screen.getByText("Delete history"));

		await waitFor(() => expect(screen.getByText(/use the purge action above/)).toBeInTheDocument());
	});

	it("surfaces a 409 run_not_terminal request error verbatim (not the purge-specific message)", async () => {
		installFetchMock((url, method) => {
			if (url.endsWith("/history") && method === "GET") {
				return errorResponse(404, "not_found", "no deletion requested");
			}
			if (url.endsWith("/history") && method === "DELETE") {
				return errorResponse(409, "run_not_terminal", "Run cannot have its history deleted.");
			}
			throw new Error(`Unhandled: ${method} ${url}`);
		});

		mount("Admin", TERMINAL_RUN);
		fireEvent.click(await screen.findByText("Delete history…"));
		fireEvent.change(screen.getByLabelText("Type DELETE to confirm"), { target: { value: "DELETE" } });
		fireEvent.click(screen.getByText("Delete history"));

		await waitFor(() => expect(screen.getByText("Run cannot have its history deleted.")).toBeInTheDocument());
	});

	it("loads an already-deleted run's tombstone on mount without firing a new DELETE", async () => {
		installFetchMock((url, method) => {
			if (url.endsWith("/history") && method === "GET") {
				return jsonResponse({
					run_id: TERMINAL_RUN.id,
					outcome: "AlreadyDeleted",
					actor: "a.admin",
					prior_state: "completed",
					occurred_at: "2026-08-20T00:00:00Z",
				});
			}
			throw new Error(`Unhandled DELETE: ${method} ${url}`);
		});

		mount("Admin", TERMINAL_RUN);

		await waitFor(() => expect(screen.getByText("OPERATIONAL HISTORY DELETED")).toBeInTheDocument());
		expect(fetchCalls.some((c) => c.method === "DELETE")).toBe(false);
	});

	it("guards against duplicate submission — a second click while deleting in flight does not double-DELETE", async () => {
		const resolveHolder: { current: (() => void) | null } = { current: null };
		installFetchMock(async (url, method) => {
			if (url.endsWith("/history") && method === "GET") {
				return errorResponse(404, "not_found", "no deletion requested");
			}
			if (url.endsWith("/history") && method === "DELETE") {
				await new Promise<void>((resolve) => {
					resolveHolder.current = resolve;
				});
				return jsonResponse({
					run_id: TERMINAL_RUN.id,
					outcome: "Completed",
					actor: "j.moreno",
					prior_state: "completed",
					occurred_at: "2026-08-24T00:00:00Z",
				});
			}
			throw new Error(`Unhandled: ${method} ${url}`);
		});

		mount("Admin", TERMINAL_RUN);
		fireEvent.click(await screen.findByText("Delete history…"));
		fireEvent.change(screen.getByLabelText("Type DELETE to confirm"), { target: { value: "DELETE" } });
		const submit = screen.getByText("Delete history");
		fireEvent.click(submit);
		fireEvent.click(submit); // duplicate click while the DELETE is in flight

		await waitFor(() => expect(screen.getByText("Deleting…")).toBeInTheDocument());
		resolveHolder.current?.();

		await waitFor(() => expect(fetchCalls.filter((c) => c.url.endsWith("/history") && c.method === "DELETE").length).toBe(1));
	});
});
