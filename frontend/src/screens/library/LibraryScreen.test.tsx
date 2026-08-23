/**
 * LibraryScreen — issue #36. Covers: item table rendering from GET
 * /library/items with mode-aware presence, the product-family rail, filtering,
 * and the mode-dependent primary action ("Export request manifest" when
 * air-gapped, "Queue missing in catalog" when connected).
 */
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { RouterProvider } from "../../lib/router";
import { SystemProvider } from "../../lib/system";
import { LibraryScreen } from "./LibraryScreen";

const CONNECTED_ITEMS = {
	mode: "connected" as const,
	items: [
		{
			id: "item-1",
			external_id: "VMware-VCSA-all-8.0.3-24022515.iso",
			product: "vCenter Server Appliance",
			version: "8.0 U3",
			status: "present",
			presence: "present" as const,
			size_bytes: 9_924_000_000,
			provenance: "depot · indexed 2026-07-11",
			indexed_at: "2026-07-11T00:00:00Z",
			updated_at: "2026-07-11T00:00:00Z",
		},
		{
			id: "item-2",
			external_id: "nsx-unified-appliance-4.2.1.0.0.24304122.ova",
			product: "NSX",
			version: "4.2.1",
			status: "indexed",
			presence: "in_depot" as const,
			size_bytes: null,
			provenance: "indexed at depot, not yet downloaded",
			indexed_at: "2026-07-01T00:00:00Z",
			updated_at: "2026-07-01T00:00:00Z",
		},
	],
	families: [
		{ name: "vCenter Server Appliance", present_count: 1, missing_count: 0 },
		{ name: "NSX", present_count: 0, missing_count: 1 },
	],
};

const DISCONNECTED_ITEMS = {
	...CONNECTED_ITEMS,
	mode: "disconnected" as const,
	items: [
		CONNECTED_ITEMS.items[0],
		{ ...CONNECTED_ITEMS.items[1], presence: "missing" as const },
	],
};

const SYSTEM_INFO = {
	version: "2.4.1",
	build: "24817",
	mode: "connected" as const,
	update_available: null,
	runners: [],
};

function jsonResponse(body: unknown): Response {
	return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
}

function installFetchMock(libraryResponse: unknown, systemMode: "connected" | "disconnected" = "connected") {
	globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
		const url = typeof input === "string" ? input : input.toString();
		if (url === "/api/v1/library/items") {
			return jsonResponse(libraryResponse);
		}
		if (url === "/api/v1/library/request-manifest") {
			return jsonResponse({
				generated_at: "2026-08-23T00:00:00Z",
				appliance_mode: systemMode,
				wanted: [{ external_id: "nsx-unified-appliance-4.2.1.0.0.24304122.ova", product: "NSX", version: "4.2.1", reason: "missing" }],
			});
		}
		if (url === "/api/v1/system") {
			return jsonResponse({ ...SYSTEM_INFO, mode: systemMode });
		}
		if (url === "/api/v1/stigman") {
			return new Response("not found", { status: 404 });
		}
		throw new Error(`Unhandled fetch in test: ${url}`);
	}) as unknown as typeof fetch;
}

function renderWithProviders(role: "Viewer" | "Cyber" | "Operator" | "Admin" = "Operator") {
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
			<SystemProvider>
				<RouterProvider>
					<LibraryScreen />
				</RouterProvider>
			</SystemProvider>
		</AuthProvider>,
	);
}

describe("LibraryScreen", () => {
	beforeEach(() => {
		sessionStorage.clear();
	});

	afterEach(() => {
		vi.restoreAllMocks();
	});

	it("renders items with presence badges from GET /library/items (connected)", async () => {
		installFetchMock(CONNECTED_ITEMS, "connected");
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("VMware-VCSA-all-8.0.3-24022515.iso")).toBeInTheDocument());
		expect(screen.getByText("present")).toBeInTheDocument();
		expect(screen.getByText("in depot")).toBeInTheDocument();
	});

	it("renders 'missing' presence when air-gapped", async () => {
		installFetchMock(DISCONNECTED_ITEMS, "disconnected");
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("missing")).toBeInTheDocument());
		expect(screen.getByText(/Air-gapped/)).toBeInTheDocument();
	});

	it("renders the product-family rail with per-family counts", async () => {
		installFetchMock(CONNECTED_ITEMS, "connected");
		renderWithProviders();

		await waitFor(() => expect(screen.getAllByText("vCenter Server Appliance").length).toBeGreaterThan(0));
		expect(screen.getAllByText("NSX").length).toBeGreaterThan(0);
	});

	it("filters to present-only items via the presence select", async () => {
		installFetchMock(CONNECTED_ITEMS, "connected");
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("VMware-VCSA-all-8.0.3-24022515.iso")).toBeInTheDocument());
		fireEvent.change(screen.getByLabelText("Filter by presence"), { target: { value: "present" } });

		expect(screen.getByText("VMware-VCSA-all-8.0.3-24022515.iso")).toBeInTheDocument();
		expect(screen.queryByText("nsx-unified-appliance-4.2.1.0.0.24304122.ova")).not.toBeInTheDocument();
	});

	it("shows 'Queue missing in catalog' when connected", async () => {
		installFetchMock(CONNECTED_ITEMS, "connected");
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("Queue missing in catalog")).toBeInTheDocument());
		expect(screen.queryByText("Export request manifest")).not.toBeInTheDocument();
	});

	it("shows 'Export request manifest' when air-gapped and triggers GET /library/request-manifest", async () => {
		installFetchMock(DISCONNECTED_ITEMS, "disconnected");
		renderWithProviders();

		await waitFor(() => expect(screen.getByText("Export request manifest")).toBeInTheDocument());

		// jsdom has no real Blob download machinery; just prove the manifest
		// fetch fires and the button doesn't error out synchronously.
		const originalCreateObjectURL = URL.createObjectURL;
		URL.createObjectURL = vi.fn(() => "blob:mock");
		URL.revokeObjectURL = vi.fn();
		fireEvent.click(screen.getByText("Export request manifest"));

		await waitFor(() => expect(URL.createObjectURL).toHaveBeenCalled());
		URL.createObjectURL = originalCreateObjectURL;
	});
});
