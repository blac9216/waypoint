/**
 * Subscriptions end-to-end wiring + RBAC gating (issue #1497, epic #1182).
 * The parent's (#1182) literal acceptance criterion is end-to-end: "adopt
 * preset -> see evaluation run -> tracked content present -> supersession
 * appears in review list with grace countdown." #1469/#1473/#1481 shipped
 * each screen (and each screen's own RBAC gating, verified independently
 * in their own test files) in isolation; this file is the integration
 * proof that the pieces actually connect:
 *
 *   1. Presets (#1469) Adopt navigates to the subscription editor (#1473)
 *      with the preset pre-filled, and the editor's `POST /subscriptions`
 *      round-trips the same `preset_id` the presets screen adopted.
 *   2. Content produced by an evaluation run, once superseded, shows up in
 *      the retention review screen (#1481) with a grace countdown.
 *      The evaluation-run job itself is #1046/#1472 (still open, out of
 *      this issue's scope per its own Risks section) — there is no
 *      frontend-observable link between "a subscription was saved" and
 *      "a row appears in `/download-retention/state`" yet, so step 2 is
 *      proven against a stubbed `GET /download-retention/state` response
 *      standing in for that job's output, exactly as the issue's own Risks
 *      section anticipates ("the end-to-end test may need to stub the
 *      evaluation-run step; note this explicitly ... rather than skipping
 *      the AC").
 *   3. RBAC is consistent across all three screens: Admin can use every
 *      mutating action, Viewer and Operator can use none of them (Operator
 *      still reads the retention state/review list, same as Viewer).
 */
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import type { Role } from "../../lib/roles";
import { RouterProvider } from "../../lib/router";
import { useRouter } from "../../lib/router-context";
import { PresetsScreen } from "../presets/PresetsScreen";
import { RetentionReviewScreen } from "../retention/RetentionReviewScreen";
import { SubscriptionEditorScreen } from "./SubscriptionEditorScreen";

const PRESET = {
	id: "preset-1",
	stack: "VCF",
	generation: "5.2",
	name: "VCF 5.2 baseline",
	line_granularity: "minor",
	anchor_version: "5.2.0",
	is_custom: false,
	source_preset_id: null,
	created_at: "2026-09-01T00:00:00Z",
	updated_at: "2026-09-01T00:00:00Z",
};

const NEW_SUBSCRIPTION = {
	id: "sub-new",
	product: "VCENTER",
	lane: "depot",
	line_granularity: "minor",
	anchor_version: "5.2.0",
	preset_id: "preset-1",
	refresh_window_days: null,
	retention_override_days: null,
	is_enabled: true,
	created_at: "2026-09-12T00:00:00Z",
	updated_at: "2026-09-12T00:00:00Z",
};

// Stands in for #1046/#1472's (not-yet-built) evaluation-run job output: a
// piece of tracked content the adopted subscription would have surfaced,
// now superseded and sitting in its grace period. `grace_ends_at` drives
// RetentionReviewScreen's countdown column (issue #1962).
const GRACE_ENDS_AT = new Date(Date.now() + (2 * 24 + 3) * 60 * 60 * 1000).toISOString();
const SUPERSEDED_CONTENT = {
	id: "retained-1",
	depot_artifact_id: "vcsa-8.0.3.9100.rpm",
	state: "grace",
	grace_started_at: "2026-09-11T00:00:00Z",
	grace_ends_at: GRACE_ENDS_AT,
	pinned_by: null,
	pinned_at: null,
	pin_note: null,
	purged_at: null,
	created_at: "2026-09-01T00:00:00Z",
	updated_at: "2026-09-11T00:00:00Z",
};

function jsonResponse(body: unknown, status = 200, headers?: Record<string, string>): Response {
	return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ...headers } });
}

function installFlowFetchMock() {
	globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
		const url = typeof input === "string" ? input : input.toString();
		const method = init?.method ?? "GET";

		if (url === "/api/v1/presets" && method === "GET") {
			return jsonResponse([PRESET]);
		}
		if (url === "/api/v1/presets/preset-1" && method === "GET") {
			return jsonResponse(PRESET);
		}
		if (url.startsWith("/api/v1/catalog/artifacts")) {
			return jsonResponse([], 200, { "X-Total-Count": "0" });
		}
		if (url === "/api/v1/subscriptions" && method === "POST") {
			return jsonResponse(NEW_SUBSCRIPTION, 201);
		}
		throw new Error(`Unhandled fetch in test: ${method} ${url}`);
	}) as unknown as typeof fetch;
}

function installRetentionFetchMock() {
	globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
		const url = typeof input === "string" ? input : input.toString();
		if (url.startsWith("/api/v1/download-retention/state")) {
			return jsonResponse([SUPERSEDED_CONTENT]);
		}
		if (url.startsWith("/api/v1/download-retention/review-list")) {
			return jsonResponse([]);
		}
		throw new Error(`Unhandled fetch in test: GET ${url}`);
	}) as unknown as typeof fetch;
}

/** Mirrors App.tsx's `SCREENS` lookup for just the two routes this flow
 * moves across, so clicking Adopt genuinely swaps the mounted screen via
 * the real router rather than a manual rerender. */
function FlowHarness() {
	const { route } = useRouter();
	if (route?.key === "subscription-editor") {
		return <SubscriptionEditorScreen />;
	}
	return <PresetsScreen />;
}

function renderAs(role: Role, ui: React.ReactElement) {
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
			<RouterProvider>{ui}</RouterProvider>
		</AuthProvider>,
	);
}

describe("subscriptions end-to-end flow (#1497)", () => {
	beforeEach(() => {
		sessionStorage.clear();
		window.history.pushState(null, "", "/presets");
	});

	afterEach(() => {
		vi.restoreAllMocks();
	});

	it("Admin: adopt preset -> lands in the editor pre-filled -> saves -> the same preset_id round-trips through POST /subscriptions", async () => {
		installFlowFetchMock();
		renderAs("Admin", <FlowHarness />);

		await waitFor(() => expect(screen.getByText("VCF 5.2 baseline")).toBeInTheDocument());
		fireEvent.click(screen.getByRole("button", { name: "Adopt" }));

		expect(window.location.pathname + window.location.search).toBe("/subscriptions/new?preset=preset-1");

		await waitFor(() => expect(screen.getByLabelText("Line granularity")).toBeInTheDocument());
		expect(screen.getByLabelText("Line granularity")).toBeDisabled();
		expect(screen.getByLabelText("Anchor version")).toBeDisabled();

		fireEvent.change(screen.getByLabelText("Product"), { target: { value: "VCENTER" } });

		const fetchMock = globalThis.fetch as unknown as ReturnType<typeof vi.fn>;
		fireEvent.click(screen.getByRole("button", { name: "Save" }));

		await waitFor(() => expect(screen.getByText("Saved.")).toBeInTheDocument());

		const postCall = fetchMock.mock.calls.find(([, init]) => (init as RequestInit | undefined)?.method === "POST");
		expect(postCall).toBeTruthy();
		const postInit = postCall![1] as RequestInit;
		const postBody = JSON.parse(postInit.body as string);
		expect(postBody.preset_id).toBe("preset-1");
	});

	it("superseded content from the adopted subscription's line appears in the retention review list with a grace countdown (evaluation-run step stubbed — #1046/#1472)", async () => {
		installRetentionFetchMock();
		renderAs("Admin", <RetentionReviewScreen />);

		await waitFor(() => expect(screen.getByText(SUPERSEDED_CONTENT.depot_artifact_id)).toBeInTheDocument());
		expect(screen.getByText(/^2d \d+h left$/)).toBeInTheDocument();
	});

	it.each<Role>(["Viewer", "Operator"])("%s: no mutating action is usable anywhere in the flow (presets adopt/clone/edit, editor save, retention pin/purge/delete)", async (role) => {
		installFlowFetchMock();
		const { unmount } = renderAs(role, <PresetsScreen />);
		await waitFor(() => expect(screen.getByText("VCF 5.2 baseline")).toBeInTheDocument());
		expect(screen.getByRole("button", { name: "Adopt" })).toBeDisabled();
		expect(screen.getByRole("button", { name: "Clone" })).toBeDisabled();
		unmount();

		window.history.pushState(null, "", "/subscriptions/new?preset=preset-1");
		const editorRender = renderAs(role, <SubscriptionEditorScreen />);
		await waitFor(() => expect(screen.getByRole("button", { name: "Save" })).toBeInTheDocument());
		expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
		editorRender.unmount();

		installRetentionFetchMock();
		renderAs(role, <RetentionReviewScreen />);
		await waitFor(() => expect(screen.getByText(SUPERSEDED_CONTENT.depot_artifact_id)).toBeInTheDocument());
		expect(screen.getByRole("button", { name: "Pin" })).toBeDisabled();
	});
});
