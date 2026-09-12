/**
 * ContentLibraryScreen — issue #1399. Proves the screen renders against the
 * documented contract shape (`ContentLibraryResponse` — issue #1391, shipped
 * — and the item DTO stubbed per #1826's Proposed Changes, see
 * content-library.ts's header comment) and covers each acceptance criterion:
 * per-type tabs, search/sort applied to the full item set (not just the
 * active tab), and deep-link stability via the URL query string.
 */
import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ContentLibraryScreen } from "./ContentLibraryScreen";

const LIBRARY = {
	id: "lib-1",
	name: "primary",
	disk_path: "/data/content-libraries/primary",
	created_at: "2026-09-01T00:00:00Z",
	updated_at: "2026-09-01T00:00:00Z",
};

const OVF_ITEM = {
	id: "item-1",
	library_id: "lib-1",
	name: "vcsa-appliance",
	type: "vcsp.ovf" as const,
	description: "vCenter appliance OVF",
	version: 1,
	files: [{ name: "vcsa.ovf", size: 1_000_000 }],
	created_at: "2026-09-01T00:00:00Z",
	updated_at: "2026-09-02T00:00:00Z",
};

const ISO_ITEM = {
	id: "item-2",
	library_id: "lib-1",
	name: "esxi-install",
	type: "vcsp.iso" as const,
	description: "ESXi installer ISO",
	version: 2,
	files: [{ name: "esxi.iso", size: 500_000 }],
	created_at: "2026-09-01T00:00:00Z",
	updated_at: "2026-09-03T00:00:00Z",
};

function jsonResponse(body: unknown): Response {
	return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
}

function installFetchMock(options?: { libraries?: unknown[]; items?: unknown[] }) {
	const libraries = options?.libraries ?? [LIBRARY];
	const items = options?.items ?? [OVF_ITEM, ISO_ITEM];

	globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
		const url = typeof input === "string" ? input : input.toString();
		if (url === "/api/v1/content-libraries") {
			return jsonResponse(libraries);
		}
		if (url === `/api/v1/content-libraries/${LIBRARY.id}/items`) {
			return jsonResponse(items);
		}
		return new Response("not found", { status: 404 });
	}) as typeof fetch;
}

beforeEach(() => {
	window.history.pushState(null, "", "/content-library");
});

afterEach(() => {
	vi.restoreAllMocks();
	window.history.pushState(null, "", "/content-library");
});

describe("ContentLibraryScreen", () => {
	it("renders items from the library, split by type tabs with counts", async () => {
		installFetchMock();
		render(<ContentLibraryScreen />);

		await waitFor(() => expect(screen.getByText("vcsa-appliance")).toBeInTheDocument());
		expect(screen.getByText("esxi-install")).toBeInTheDocument();

		const allTab = screen.getByRole("tab", { name: /All/ });
		expect(allTab).toHaveTextContent("2");
	});

	it("filters by the active type tab, keeping search/sort scoped to the full item set", async () => {
		installFetchMock();
		render(<ContentLibraryScreen />);
		await waitFor(() => expect(screen.getByText("vcsa-appliance")).toBeInTheDocument());

		fireEvent.click(screen.getByRole("tab", { name: /ISO/ }));

		await waitFor(() => expect(screen.queryByText("vcsa-appliance")).not.toBeInTheDocument());
		expect(screen.getByText("esxi-install")).toBeInTheDocument();

		// The "All" tab count still reflects both items — the tab only narrows
		// what's displayed, not what search/sort operate over (AC1).
		expect(screen.getByRole("tab", { name: /All/ })).toHaveTextContent("2");
	});

	it("search narrows the visible set within the active tab and persists to the URL", async () => {
		installFetchMock();
		render(<ContentLibraryScreen />);
		await waitFor(() => expect(screen.getByText("vcsa-appliance")).toBeInTheDocument());

		fireEvent.change(screen.getByLabelText("Search content library items"), { target: { value: "esxi" } });

		await waitFor(() => expect(screen.queryByText("vcsa-appliance")).not.toBeInTheDocument());
		expect(screen.getByText("esxi-install")).toBeInTheDocument();
		expect(new URLSearchParams(window.location.search).get("q")).toBe("esxi");
	});

	it("restores view/type/search/sort from the URL on load (deep-link stability, AC3)", async () => {
		window.history.pushState(null, "", "/content-library?library=lib-1&type=vcsp.iso&q=esxi&sort=size");
		installFetchMock();
		render(<ContentLibraryScreen />);

		await waitFor(() => expect(screen.getByText("esxi-install")).toBeInTheDocument());
		expect(screen.queryByText("vcsa-appliance")).not.toBeInTheDocument();
		expect(screen.getByRole("tab", { name: /ISO/ })).toHaveAttribute("aria-selected", "true");
	});

	it("shows a note when there are no content libraries yet", async () => {
		installFetchMock({ libraries: [] });
		render(<ContentLibraryScreen />);
		await waitFor(() => expect(screen.getByText("No content libraries yet.")).toBeInTheDocument());
	});
});
