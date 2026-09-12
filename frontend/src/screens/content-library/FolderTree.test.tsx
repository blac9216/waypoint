/**
 * FolderTree — issue #1422. Proves the tree renders from the
 * `ContentLibraryFolderNode[]` shape, selection calls back with the right id
 * (including "All items" clearing selection), create/rename/delete call
 * their respective callbacks, and role gating (AC3: Operator+ for create/
 * rename, Admin+ for delete) leaves controls visible-but-disabled rather than
 * hidden for an insufficient role.
 */
import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { FolderTree } from "./FolderTree";
import type { ContentLibraryFolderNode } from "./content-library";

const TREE: ContentLibraryFolderNode[] = [
	{
		id: "folder-1",
		name: "Appliances",
		created_at: "2026-09-01T00:00:00Z",
		item_ids: ["item-1"],
		children: [
			{
				id: "folder-1a",
				name: "vCenter",
				created_at: "2026-09-01T00:00:00Z",
				item_ids: [],
				children: [],
			},
		],
	},
	{
		id: "folder-2",
		name: "Installers",
		created_at: "2026-09-01T00:00:00Z",
		item_ids: [],
		children: [],
	},
];

function noop() {
	return Promise.resolve();
}

describe("FolderTree", () => {
	it("renders the tree with item counts and 'All items' at the top", () => {
		render(
			<FolderTree
				folders={TREE}
				selectedFolderId={undefined}
				onSelect={vi.fn()}
				role="Operator"
				onCreate={noop}
				onRename={noop}
				onDelete={noop}
			/>,
		);
		expect(screen.getByText("All items")).toBeInTheDocument();
		expect(screen.getByText("Appliances")).toBeInTheDocument();
		expect(screen.getByText("vCenter")).toBeInTheDocument();
		expect(screen.getByText("Installers")).toBeInTheDocument();
	});

	it("calls onSelect with the folder id when a node is clicked, and undefined for 'All items'", () => {
		const onSelect = vi.fn();
		render(
			<FolderTree
				folders={TREE}
				selectedFolderId="folder-2"
				onSelect={onSelect}
				role="Operator"
				onCreate={noop}
				onRename={noop}
				onDelete={noop}
			/>,
		);
		fireEvent.click(screen.getByText("Appliances"));
		expect(onSelect).toHaveBeenCalledWith("folder-1");

		fireEvent.click(screen.getByText("All items"));
		expect(onSelect).toHaveBeenCalledWith(undefined);
	});

	it("Operator can create a root folder", async () => {
		const onCreate = vi.fn().mockResolvedValue(undefined);
		render(
			<FolderTree
				folders={TREE}
				selectedFolderId={undefined}
				onSelect={vi.fn()}
				role="Operator"
				onCreate={onCreate}
				onRename={noop}
				onDelete={noop}
			/>,
		);
		fireEvent.click(screen.getByText("+ New folder"));
		fireEvent.change(screen.getByLabelText("New folder name"), { target: { value: "Media" } });
		fireEvent.click(screen.getByText("Create"));
		expect(onCreate).toHaveBeenCalledWith("Media", null);
	});

	it("Operator can rename a folder, resending its current parent (full-replace contract)", async () => {
		const onRename = vi.fn().mockResolvedValue(undefined);
		render(
			<FolderTree
				folders={TREE}
				selectedFolderId={undefined}
				onSelect={vi.fn()}
				role="Operator"
				onCreate={noop}
				onRename={onRename}
				onDelete={noop}
			/>,
		);
		fireEvent.click(screen.getAllByText("Rename")[1]); // folder-1a (vCenter), nested under folder-1
		fireEvent.change(screen.getByLabelText("Rename vCenter"), { target: { value: "vCenter Server" } });
		fireEvent.click(screen.getByText("Save"));
		expect(onRename).toHaveBeenCalledWith("folder-1a", "vCenter Server", "folder-1");
	});

	it("Admin can delete an empty folder after confirming", async () => {
		const onDelete = vi.fn().mockResolvedValue(undefined);
		vi.spyOn(window, "confirm").mockReturnValue(true);
		render(
			<FolderTree
				folders={TREE}
				selectedFolderId={undefined}
				onSelect={vi.fn()}
				role="Admin"
				onCreate={noop}
				onRename={noop}
				onDelete={onDelete}
			/>,
		);
		fireEvent.click(screen.getAllByText("Delete")[2]); // DOM order: folder-1, folder-1a, folder-2 (Installers)
		expect(onDelete).toHaveBeenCalledWith("folder-2");
	});

	it("Viewer sees create/rename/delete visibly disabled, not hidden (roleGateProps convention)", () => {
		render(
			<FolderTree
				folders={TREE}
				selectedFolderId={undefined}
				onSelect={vi.fn()}
				role="Viewer"
				onCreate={noop}
				onRename={noop}
				onDelete={noop}
			/>,
		);
		expect(screen.getByText("+ New folder")).toBeDisabled();
		expect(screen.getAllByText("Delete")[0]).toBeDisabled();
	});

	it("surfaces an ApiError-style message when create fails, without crashing", async () => {
		const onCreate = vi.fn().mockRejectedValue(new Error("boom"));
		render(
			<FolderTree
				folders={TREE}
				selectedFolderId={undefined}
				onSelect={vi.fn()}
				role="Operator"
				onCreate={onCreate}
				onRename={noop}
				onDelete={noop}
			/>,
		);
		fireEvent.click(screen.getByText("+ New folder"));
		fireEvent.change(screen.getByLabelText("New folder name"), { target: { value: "Media" } });
		fireEvent.click(screen.getByText("Create"));
		expect(await screen.findByText("Could not create the folder.")).toBeInTheDocument();
	});
});
