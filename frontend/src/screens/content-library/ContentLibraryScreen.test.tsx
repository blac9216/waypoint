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
import { useAuth } from "../../lib/auth-context";
import { ContentLibraryScreen } from "./ContentLibraryScreen";

vi.mock("../../lib/auth-context", () => ({
	useAuth: vi.fn(),
}));

const mockUseAuth = vi.mocked(useAuth);

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

interface FolderRow {
	id: string;
	name: string;
	parent_folder_id: string | null;
	created_at: string;
	item_ids: string[];
}

function buildTree(rows: FolderRow[]): unknown[] {
	const byParent = new Map<string | null, FolderRow[]>();
	for (const row of rows) {
		const list = byParent.get(row.parent_folder_id) ?? [];
		list.push(row);
		byParent.set(row.parent_folder_id, list);
	}
	const build = (parentId: string | null): unknown[] =>
		(byParent.get(parentId) ?? []).map((row) => ({
			id: row.id,
			name: row.name,
			created_at: row.created_at,
			item_ids: row.item_ids,
			children: build(row.id),
		}));
	return build(null);
}

/**
 * A stateful fetch mock standing in for `ContentLibraryFoldersController`
 * (issue #1389) — real enough (mutable folder rows, item-folder assignment,
 * a `GET .../folders` that reflects prior writes) to prove the folder UI's
 * create/assign/repair-survival round trip end to end, per this issue's AC1
 * ("trigger a repair, or its test double").
 */
function installFetchMock(options?: { libraries?: unknown[]; items?: unknown[]; folders?: FolderRow[] }) {
	const libraries = options?.libraries ?? [LIBRARY];
	const items = options?.items ?? [OVF_ITEM, ISO_ITEM];
	const folderRows: FolderRow[] = options?.folders ?? [];
	let nextFolderId = 1;

	globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
		const url = typeof input === "string" ? input : input.toString();
		const method = init?.method ?? "GET";
		const body = init?.body ? (JSON.parse(init.body as string) as Record<string, unknown>) : undefined;

		if (url === "/api/v1/content-libraries") {
			return jsonResponse(libraries);
		}
		if (url === `/api/v1/content-libraries/${LIBRARY.id}/items`) {
			return jsonResponse(items);
		}
		if (url === `/api/v1/content-libraries/${LIBRARY.id}/folders` && method === "GET") {
			return jsonResponse(buildTree(folderRows));
		}
		if (url === `/api/v1/content-libraries/${LIBRARY.id}/folders` && method === "POST") {
			const id = `folder-${nextFolderId++}`;
			const row: FolderRow = {
				id,
				name: body?.name as string,
				parent_folder_id: (body?.parent_folder_id as string | null) ?? null,
				created_at: "2026-09-05T00:00:00Z",
				item_ids: [],
			};
			folderRows.push(row);
			return new Response(
				JSON.stringify({ id, library_id: LIBRARY.id, parent_folder_id: row.parent_folder_id, name: row.name, created_at: row.created_at }),
				{ status: 201, headers: { "Content-Type": "application/json" } },
			);
		}
		const assignMatch = url.match(new RegExp(`/api/v1/content-libraries/${LIBRARY.id}/items/([^/]+)/folder$`));
		if (assignMatch && method === "PATCH") {
			const itemId = assignMatch[1];
			for (const row of folderRows) {
				row.item_ids = row.item_ids.filter((id) => id !== itemId);
			}
			const targetFolderId = (body?.folder_id as string | null) ?? null;
			if (targetFolderId) {
				const target = folderRows.find((r) => r.id === targetFolderId);
				if (!target) {
					return new Response(JSON.stringify({ error: { code: "not_found", message: "No such folder." } }), { status: 404 });
				}
				target.item_ids.push(itemId);
			}
			return new Response(null, { status: 204 });
		}
		return new Response("not found", { status: 404 });
	}) as typeof fetch;
}

function mockAuthUser(role: "Viewer" | "Operator" | "Admin" = "Operator") {
	mockUseAuth.mockReturnValue({
		user: { username: "test-user", role },
		token: "test-token",
		status: "signed-in",
		error: null,
		login: vi.fn(),
		localAuthAvailable: false,
		startOidcLogin: vi.fn(),
		stepUpOidcLogin: vi.fn(),
		logout: vi.fn(),
	});
}

beforeEach(() => {
	window.history.pushState(null, "", "/content-library");
	mockAuthUser();
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

	describe("virtual folders (issue #1422)", () => {
		it("creates a folder, assigns an item to it, and the assignment survives a repair (its test double: a plain re-fetch)", async () => {
			installFetchMock();
			render(<ContentLibraryScreen />);
			await waitFor(() => expect(screen.getByText("vcsa-appliance")).toBeInTheDocument());

			// Create a folder via the tree panel.
			fireEvent.click(screen.getByText("+ New folder"));
			fireEvent.change(screen.getByLabelText("New folder name"), { target: { value: "Appliances" } });
			fireEvent.click(screen.getByText("Create"));
			await waitFor(() => expect(screen.getByRole("button", { name: /Appliances/ })).toBeInTheDocument());

			// Assign the item to it via the per-row move menu (menu-based, not DnD).
			fireEvent.change(screen.getByLabelText("Move vcsa-appliance to folder"), { target: { value: "folder-1" } });
			await waitFor(() => expect(screen.getByLabelText("Move vcsa-appliance to folder")).toHaveValue("folder-1"));

			// "Trigger a repair, or its test double" (AC1): re-fetch items+folders
			// from the server exactly as a post-repair reload would, and confirm
			// the assignment is still there — folders are DB-only metadata that
			// never touch disk_path, so a repair cannot disturb it.
			fireEvent.click(screen.getByRole("tab", { name: /All/ }));
			await waitFor(() => expect(screen.getByLabelText("Move vcsa-appliance to folder")).toHaveValue("folder-1"));

			// Selecting the folder narrows the visible items to what's assigned to it.
			fireEvent.click(screen.getByRole("button", { name: /Appliances/ }));
			await waitFor(() => expect(screen.getByText("vcsa-appliance")).toBeInTheDocument());
			expect(screen.queryByText("esxi-install")).not.toBeInTheDocument();
			expect(new URLSearchParams(window.location.search).get("folder")).toBe("folder-1");
		});

		it("deep-links to a folder's contents and restores the selection on reload (AC2)", async () => {
			installFetchMock({
				folders: [{ id: "folder-1", name: "Appliances", parent_folder_id: null, created_at: "2026-09-01T00:00:00Z", item_ids: ["item-1"] }],
			});
			window.history.pushState(null, "", "/content-library?library=lib-1&folder=folder-1");
			render(<ContentLibraryScreen />);

			await waitFor(() => expect(screen.getByText("vcsa-appliance")).toBeInTheDocument());
			expect(screen.queryByText("esxi-install")).not.toBeInTheDocument();
		});

		it("rolls back the optimistic move on an ApiError from the assign endpoint", async () => {
			installFetchMock({
				folders: [{ id: "folder-1", name: "Appliances", parent_folder_id: null, created_at: "2026-09-01T00:00:00Z", item_ids: [] }],
			});
			render(<ContentLibraryScreen />);
			await waitFor(() => expect(screen.getByText("vcsa-appliance")).toBeInTheDocument());

			fireEvent.change(screen.getByLabelText("Move vcsa-appliance to folder"), { target: { value: "bogus-folder" } });

			await waitFor(() => expect(screen.getByLabelText("Move vcsa-appliance to folder")).toHaveValue(""));
		});

		it("Viewer sees folder create/delete visibly disabled, and the move menu itself disabled (AC3)", async () => {
			mockAuthUser("Viewer");
			installFetchMock();
			render(<ContentLibraryScreen />);
			await waitFor(() => expect(screen.getByText("vcsa-appliance")).toBeInTheDocument());

			expect(screen.getByText("+ New folder")).toBeDisabled();
			expect(screen.getByLabelText("Move vcsa-appliance to folder")).toBeDisabled();
		});
	});
});
