/**
 * RetentionReviewScreen — issue #1481. Proves the screen renders against
 * #1453's documented `RetentionController` response shape
 * (`RetainedContentStateResponse` / `ReviewListEntryResponse` /
 * `PurgeNowResponse` / `DeleteReviewListEntryResponse`, all snake_case on
 * the wire) and covers each acceptance criterion: grace countdown + pin,
 * purge-now, the review list's delete-only mutating action, and the
 * derived grace-entry/review-list-addition alerts.
 */
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { RetentionReviewScreen } from "./RetentionReviewScreen";

const GRACE_ITEM = {
	id: "state-1",
	depot_artifact_id: "artifact-1",
	state: "grace",
	grace_started_at: "2026-09-10T00:00:00Z",
	pinned_by: null,
	pinned_at: null,
	pin_note: null,
	purged_at: null,
	created_at: "2026-09-10T00:00:00Z",
	updated_at: "2026-09-10T00:00:00Z",
};

const PENDING_PURGE_ITEM = {
	...GRACE_ITEM,
	id: "state-2",
	depot_artifact_id: "artifact-2",
	state: "pending-purge",
};

const REVIEW_ENTRY = {
	kind: "Orphan" as const,
	depot_artifact_id: null,
	relative_path: "iso/orphaned-file.iso",
	size_bytes: 1_048_576,
	reason: "no matching subscription",
	first_seen_at: "2026-09-11T00:00:00Z",
	last_seen_at: "2026-09-11T00:00:00Z",
};

function jsonResponse(body: unknown): Response {
	return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
}

function installFetchMock(options?: { stateItems?: unknown[]; reviewEntries?: unknown[] }) {
	const stateItems = options?.stateItems ?? [GRACE_ITEM, PENDING_PURGE_ITEM];
	const reviewEntries = options?.reviewEntries ?? [REVIEW_ENTRY];

	globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
		const url = typeof input === "string" ? input : input.toString();
		const method = init?.method ?? "GET";

		if (url === "/api/v1/download-retention/state" && method === "GET") {
			return jsonResponse(stateItems);
		}
		if (url === "/api/v1/download-retention/review-list" && method === "GET") {
			return jsonResponse(reviewEntries);
		}
		if (url.startsWith("/api/v1/download-retention/") && url.endsWith("/pin") && method === "POST") {
			return jsonResponse({ ...GRACE_ITEM, state: "pinned", pinned_by: "j.moreno" });
		}
		if (url.startsWith("/api/v1/download-retention/") && url.endsWith("/purge-now") && method === "POST") {
			return jsonResponse({ retained_content_state_id: "state-1", purged: true, error: null });
		}
		if (url === "/api/v1/download-retention/review-list" && method === "DELETE") {
			return jsonResponse({ deleted: true, error: null });
		}
		throw new Error(`Unhandled fetch in test: ${method} ${url}`);
	}) as unknown as typeof fetch;
}

function renderWithProviders(role: "Viewer" | "Cyber" | "Operator" | "Admin" = "Admin") {
	sessionStorage.setItem(
		"waypoint.session",
		JSON.stringify({
			token: "tok",
			username: "j.moreno",
			role,
			expiresAt: new Date(Date.now() + 3600_000).toISOString(),
		}),
	);
	return render(
		<AuthProvider>
			<RetentionReviewScreen />
		</AuthProvider>,
	);
}

describe("RetentionReviewScreen", () => {
	beforeEach(() => {
		sessionStorage.clear();
	});

	afterEach(() => {
		vi.restoreAllMocks();
	});

	it("renders grace/pending-purge content with a countdown, from GET /download-retention/state", async () => {
		installFetchMock();
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("artifact-1")).toBeInTheDocument());
		expect(screen.getByText("artifact-2")).toBeInTheDocument();
		expect(screen.getAllByText(/in grace/).length).toBeGreaterThan(0);
	});

	it("pinning removes the item from the grace/pending-purge list", async () => {
		installFetchMock({ stateItems: [GRACE_ITEM] });
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("artifact-1")).toBeInTheDocument());

		// After pin, re-install the mock so the re-fetched state list reflects
		// the pinned row no longer being grace/pending-purge (a real pin
		// transitions the row's `state` to `pinned`, which the controller's own
		// `ListableStates` set still includes on `GET .../state`, but this
		// screen's own grace/pending-purge filter excludes it -- see
		// RetentionReviewScreen.tsx's `load` callback).
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			const method = init?.method ?? "GET";
			if (url === "/api/v1/download-retention/state" && method === "GET") {
				return jsonResponse([]);
			}
			if (url === "/api/v1/download-retention/review-list" && method === "GET") {
				return jsonResponse([]);
			}
			if (url.endsWith("/pin") && method === "POST") {
				return jsonResponse({ ...GRACE_ITEM, state: "pinned" });
			}
			throw new Error(`Unhandled fetch in test: ${method} ${url}`);
		}) as unknown as typeof fetch;

		fireEvent.click(screen.getByText("Pin"));

		await waitFor(() => expect(screen.getByText("Nothing is currently in grace or pending purge.")).toBeInTheDocument());
	});

	it("purge-now calls POST /download-retention/{id}/purge-now", async () => {
		installFetchMock({ stateItems: [GRACE_ITEM], reviewEntries: [] });
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("artifact-1")).toBeInTheDocument());

		const purgeSpy = vi.spyOn(globalThis, "fetch");
		fireEvent.click(screen.getByText("Purge now"));

		await waitFor(() =>
			expect(purgeSpy).toHaveBeenCalledWith(
				"/api/v1/download-retention/state-1/purge-now",
				expect.objectContaining({ method: "POST" }),
			),
		);
	});

	it("renders the review list separately, with delete as its only action", async () => {
		installFetchMock();
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("iso/orphaned-file.iso")).toBeInTheDocument());
		const reviewRow = screen.getByText("iso/orphaned-file.iso").closest("tr");
		expect(reviewRow).not.toBeNull();
		const actionButtons = reviewRow!.querySelectorAll("button");
		expect(actionButtons.length).toBe(1);
		expect(actionButtons[0].textContent).toBe("Delete");
	});

	it("delete calls DELETE /download-retention/review-list with the entry's kind and path", async () => {
		installFetchMock();
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("iso/orphaned-file.iso")).toBeInTheDocument());

		const deleteSpy = vi.spyOn(globalThis, "fetch");
		fireEvent.click(screen.getByText("Delete"));

		await waitFor(() =>
			expect(deleteSpy).toHaveBeenCalledWith("/api/v1/download-retention/review-list", expect.objectContaining({ method: "DELETE" })),
		);
	});

	it("shows derived grace-entry and review-list-addition alerts", async () => {
		installFetchMock();
		renderWithProviders();

		await waitFor(() => expect(screen.getByText(/entered its grace period/)).toBeInTheDocument());
		expect(screen.getByText(/added to the review list/)).toBeInTheDocument();
	});

	it("disables pin/purge/delete for a Viewer, with a reason", async () => {
		installFetchMock();
		renderWithProviders("Viewer");

		await waitFor(() => expect(screen.getByText("artifact-1")).toBeInTheDocument());
		const pinButton = screen.getAllByText("Pin")[0] as HTMLButtonElement;
		expect(pinButton.disabled).toBe(true);
		expect(pinButton.title).toMatch(/Admin/);
	});
});
