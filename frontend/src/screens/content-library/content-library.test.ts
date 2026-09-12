/**
 * content-library.ts helpers — issue #1399. Proves the filter/sort/format
 * helpers the screen composes, independent of rendering.
 */
import { describe, expect, it } from "vitest";
import {
	buildItemFolderMap,
	flattenFolderTree,
	formatBytes,
	itemTotalSize,
	matchesSearch,
	matchesTypeFilter,
	sortItems,
	type ContentLibraryFolderNode,
	type ContentLibraryItem,
} from "./content-library";

function makeItem(overrides: Partial<ContentLibraryItem>): ContentLibraryItem {
	return {
		id: "item-1",
		library_id: "lib-1",
		name: "vcsa-appliance",
		type: "vcsp.ovf",
		description: "vCenter appliance OVF",
		version: 1,
		files: [{ name: "vcsa.ovf", size: 1000 }],
		created_at: "2026-09-01T00:00:00Z",
		updated_at: "2026-09-01T00:00:00Z",
		...overrides,
	};
}

describe("matchesTypeFilter", () => {
	it("matches everything for 'all'", () => {
		expect(matchesTypeFilter(makeItem({ type: "vcsp.iso" }), "all")).toBe(true);
	});

	it("matches only the given type otherwise", () => {
		expect(matchesTypeFilter(makeItem({ type: "vcsp.iso" }), "vcsp.iso")).toBe(true);
		expect(matchesTypeFilter(makeItem({ type: "vcsp.iso" }), "vcsp.ovf")).toBe(false);
	});
});

describe("matchesSearch", () => {
	it("matches empty search unconditionally", () => {
		expect(matchesSearch(makeItem({}), "")).toBe(true);
		expect(matchesSearch(makeItem({}), "   ")).toBe(true);
	});

	it("matches name, description, or type case-insensitively", () => {
		const item = makeItem({ name: "VCSA-Appliance", description: "vCenter server appliance", type: "vcsp.iso" });
		expect(matchesSearch(item, "vcsa")).toBe(true);
		expect(matchesSearch(item, "SERVER")).toBe(true);
		expect(matchesSearch(item, "vcsp.iso")).toBe(true);
		expect(matchesSearch(item, "nsx")).toBe(false);
	});
});

describe("itemTotalSize", () => {
	it("sums every file's size", () => {
		const item = makeItem({ files: [{ name: "a", size: 100 }, { name: "b", size: 250 }] });
		expect(itemTotalSize(item)).toBe(350);
	});

	it("is zero for an item with no files", () => {
		expect(itemTotalSize(makeItem({ files: [] }))).toBe(0);
	});
});

describe("sortItems", () => {
	const items = [
		makeItem({ id: "b", name: "beta", files: [{ name: "f", size: 100 }], updated_at: "2026-09-02T00:00:00Z" }),
		makeItem({ id: "a", name: "alpha", files: [{ name: "f", size: 300 }], updated_at: "2026-09-03T00:00:00Z" }),
		makeItem({ id: "c", name: "charlie", files: [{ name: "f", size: 200 }], updated_at: "2026-09-01T00:00:00Z" }),
	];

	it("sorts by name ascending", () => {
		expect(sortItems(items, "name").map((i) => i.id)).toEqual(["a", "b", "c"]);
	});

	it("sorts by size descending", () => {
		expect(sortItems(items, "size").map((i) => i.id)).toEqual(["a", "c", "b"]);
	});

	it("sorts by updated_at descending (most recent first)", () => {
		expect(sortItems(items, "updated_at").map((i) => i.id)).toEqual(["a", "b", "c"]);
	});

	it("never mutates its input", () => {
		const copy = [...items];
		sortItems(items, "name");
		expect(items).toEqual(copy);
	});
});

const FOLDER_TREE: ContentLibraryFolderNode[] = [
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
				item_ids: ["item-2"],
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

describe("flattenFolderTree", () => {
	it("flattens depth-first with each node's depth", () => {
		expect(flattenFolderTree(FOLDER_TREE)).toEqual([
			{ id: "folder-1", name: "Appliances", depth: 0 },
			{ id: "folder-1a", name: "vCenter", depth: 1 },
			{ id: "folder-2", name: "Installers", depth: 0 },
		]);
	});

	it("returns an empty list for an empty tree", () => {
		expect(flattenFolderTree([])).toEqual([]);
	});
});

describe("buildItemFolderMap", () => {
	it("maps each directly-assigned item to its folder id, at any depth", () => {
		const map = buildItemFolderMap(FOLDER_TREE);
		expect(map.get("item-1")).toBe("folder-1");
		expect(map.get("item-2")).toBe("folder-1a");
	});

	it("omits items with no folder assignment", () => {
		const map = buildItemFolderMap(FOLDER_TREE);
		expect(map.has("item-3")).toBe(false);
	});
});

describe("formatBytes", () => {
	it("renders a dash for null/undefined/negative/non-finite", () => {
		expect(formatBytes(null)).toBe("—");
		expect(formatBytes(undefined)).toBe("—");
		expect(formatBytes(-1)).toBe("—");
		expect(formatBytes(Number.NaN)).toBe("—");
	});

	it("scales units", () => {
		expect(formatBytes(500)).toBe("500 B");
		expect(formatBytes(1536)).toBe("1.5 KB");
	});
});
