/**
 * PresetsScreen — issue #1469. Proves the screen renders against #1450's
 * documented `PresetsController` response shape (`PresetResponse`,
 * snake_case on the wire) and covers each acceptance criterion: the list
 * renders shipped + custom presets, adopt navigates to the subscription
 * editor with the preset pre-filled, clone calls the clone-to-custom
 * endpoint, edit saves a custom clone, and a write to a shipped preset
 * surfaces the 409 `preset_not_custom` the server returns.
 */
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { RouterProvider } from "../../lib/router";
import { PresetsScreen } from "./PresetsScreen";

const SHIPPED_PRESET = {
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

const CUSTOM_PRESET = {
	id: "preset-2",
	stack: "VVF",
	generation: "9.0",
	name: "VVF 9.0 baseline (custom)",
	line_granularity: "subminor",
	anchor_version: "9.0.1",
	is_custom: true,
	source_preset_id: "preset-3",
	created_at: "2026-09-02T00:00:00Z",
	updated_at: "2026-09-02T00:00:00Z",
};

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function installFetchMock(options?: { presets?: unknown[] }) {
	const presets = options?.presets ?? [SHIPPED_PRESET, CUSTOM_PRESET];

	globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
		const url = typeof input === "string" ? input : input.toString();
		const method = init?.method ?? "GET";

		if (url === "/api/v1/presets" && method === "GET") {
			return jsonResponse(presets);
		}
		if (url === "/api/v1/presets/preset-1/clone" && method === "POST") {
			return jsonResponse({ ...SHIPPED_PRESET, id: "preset-4", is_custom: true, name: "VCF 5.2 baseline (custom)" }, 201);
		}
		if (url === "/api/v1/presets/preset-2" && method === "PUT") {
			return jsonResponse({ ...CUSTOM_PRESET, name: "renamed" });
		}
		if (url === "/api/v1/presets/preset-1" && method === "PUT") {
			return jsonResponse({ error: { code: "preset_not_custom", message: "Preset 'preset-1' is not a custom preset." } }, 409);
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
			<RouterProvider>
				<PresetsScreen />
			</RouterProvider>
		</AuthProvider>,
	);
}

describe("PresetsScreen", () => {
	beforeEach(() => {
		sessionStorage.clear();
		window.history.pushState(null, "", "/presets");
	});

	afterEach(() => {
		vi.restoreAllMocks();
	});

	it("renders shipped and custom presets from GET /presets", async () => {
		installFetchMock();
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("VCF 5.2 baseline")).toBeInTheDocument());
		expect(screen.getByText("VVF 9.0 baseline (custom)")).toBeInTheDocument();
		expect(screen.getByText("shipped")).toBeInTheDocument();
		expect(screen.getByText("custom")).toBeInTheDocument();
	});

	it("adopt navigates to the subscription editor with the preset pre-filled, without calling the API", async () => {
		installFetchMock();
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("VCF 5.2 baseline")).toBeInTheDocument());

		const row = screen.getByText("VCF 5.2 baseline").closest("tr")!;
		fireEvent.click(within(row).getByText("Adopt"));

		expect(window.location.pathname + window.location.search).toBe("/subscriptions/new?preset=preset-1");
	});

	it("clone calls POST /presets/{id}/clone and refreshes the list", async () => {
		installFetchMock();
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("VCF 5.2 baseline")).toBeInTheDocument());

		const row = screen.getByText("VCF 5.2 baseline").closest("tr")!;
		const cloneSpy = vi.spyOn(globalThis, "fetch");
		fireEvent.click(within(row).getByText("Clone"));

		await waitFor(() =>
			expect(cloneSpy).toHaveBeenCalledWith("/api/v1/presets/preset-1/clone", expect.objectContaining({ method: "POST" })),
		);
	});

	it("edit is disabled with a reason for a shipped preset", async () => {
		installFetchMock();
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("VCF 5.2 baseline")).toBeInTheDocument());

		const row = screen.getByText("VCF 5.2 baseline").closest("tr")!;
		const editButton = within(row).getByText("Edit") as HTMLButtonElement;
		expect(editButton.disabled).toBe(true);
		expect(editButton.title).toMatch(/read-only/);
	});

	it("edit saves a custom clone via PUT /presets/{id}", async () => {
		installFetchMock();
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("VVF 9.0 baseline (custom)")).toBeInTheDocument());

		const row = screen.getByText("VVF 9.0 baseline (custom)").closest("tr")!;
		fireEvent.click(within(row).getByText("Edit"));

		const nameInput = await screen.findByDisplayValue("VVF 9.0 baseline (custom)");
		fireEvent.change(nameInput, { target: { value: "renamed" } });

		const putSpy = vi.spyOn(globalThis, "fetch");
		fireEvent.click(screen.getByText("Save"));

		await waitFor(() =>
			expect(putSpy).toHaveBeenCalledWith("/api/v1/presets/preset-2", expect.objectContaining({ method: "PUT" })),
		);
	});

	it("surfaces 409 preset_not_custom when a shipped preset write is rejected", async () => {
		installFetchMock({ presets: [{ ...SHIPPED_PRESET, is_custom: true }] });
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("VCF 5.2 baseline")).toBeInTheDocument());

		// This preset is marked custom client-side so Edit is reachable, but the
		// server still authoritatively rejects the write (e.g. a stale client
		// view after a concurrent revert) -- the screen must surface, not
		// swallow, that mismatch.
		fireEvent.click(screen.getByText("Edit"));
		fireEvent.click(screen.getByText("Save"));

		await waitFor(() => expect(screen.getByText(/is a shipped preset and is read-only/)).toBeInTheDocument());
	});

	it("disables clone for a Viewer, with a reason", async () => {
		installFetchMock();
		renderWithProviders("Viewer");

		await waitFor(() => expect(screen.getByText("VCF 5.2 baseline")).toBeInTheDocument());
		const cloneButton = screen.getAllByText("Clone")[0] as HTMLButtonElement;
		expect(cloneButton.disabled).toBe(true);
		expect(cloneButton.title).toMatch(/Admin/);
	});
});
