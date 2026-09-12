/**
 * Content library view shell data layer (issue #1399, epic #1185). Wired
 * against the content-library registry (`ContentLibrariesController`, issue
 * #1391, shipped) and the item CRUD contract (`ContentLibraryItem`, issue
 * #1396's model layer + issue #1826's still-open HTTP surface —
 * `docs/reference/api-contract.md` "Library & content library": item CRUD's
 * operation layer is shipped but "No HTTP endpoint yet"). Per this issue's
 * own "Risks" section ("stub against #37's documented contract and adjust —
 * do not block on #37 merging first, only on its contract being fixed"),
 * `fetchContentLibraryItems` below is written against #1826's own Proposed
 * Changes (`GET /content-libraries/{id}/items` returning the item DTO,
 * "for parity with the folder tree's own read surface"), mirroring
 * `ContentLibraryItem.cs`'s fields field-for-field in the same snake_case
 * wire convention `ContentLibraryFolderContracts.cs`/`ContentLibraryResponse`
 * already use. Adjust this file, not its callers' shapes, if #1826 lands
 * with a different envelope.
 */

import { apiGet } from "../../lib/api";

/** Registry row (`ContentLibrariesController.List`, issue #1391 — shipped). */
export interface ContentLibrary {
	id: string;
	name: string;
	disk_path: string;
	created_at: string;
	updated_at: string;
}

export function fetchContentLibraries(): Promise<ContentLibrary[]> {
	return apiGet<ContentLibrary[]>("/content-libraries");
}

/** Closed VCSP wire vocabulary (`ContentLibraryItemTypes.All`, research #1032 Q3). */
export type ContentLibraryItemType = "vcsp.ovf" | "vcsp.iso" | "vcsp.other";

export const CONTENT_LIBRARY_ITEM_TYPES: readonly ContentLibraryItemType[] = ["vcsp.ovf", "vcsp.iso", "vcsp.other"];

export const CONTENT_LIBRARY_TYPE_LABELS: Record<ContentLibraryItemType, string> = {
	"vcsp.ovf": "OVA / OVF",
	"vcsp.iso": "ISO",
	"vcsp.other": "Files",
};

export interface ContentLibraryItemFile {
	name: string;
	size: number;
}

/**
 * One item, per `ContentLibraryItem.cs`. `folder_id` is not on that record
 * today (folder assignment lives in the folder tree's own `item_ids`,
 * `ContentLibraryFoldersController`, issue #1389) — it is included here,
 * optional and always `undefined` until #1422 ships, so the shell's data
 * layer does not need a reshape when virtual-folder assignment starts
 * riding on this same item shape (this issue's own AC3).
 */
export interface ContentLibraryItem {
	id: string;
	library_id: string;
	folder_id?: string | null;
	name: string;
	type: ContentLibraryItemType;
	description: string;
	version: number;
	files: ContentLibraryItemFile[];
	created_at: string;
	updated_at: string;
}

export function fetchContentLibraryItems(libraryId: string): Promise<ContentLibraryItem[]> {
	return apiGet<ContentLibraryItem[]>(`/content-libraries/${encodeURIComponent(libraryId)}/items`);
}

/** Client-side type filter over the fetched item list (the per-type tabs). */
export type ContentLibraryTypeFilter = "all" | ContentLibraryItemType;

export function matchesTypeFilter(item: ContentLibraryItem, filter: ContentLibraryTypeFilter): boolean {
	return filter === "all" || item.type === filter;
}

/** Case-insensitive match over name/description/type — applied to the FULL
 * item set (this issue's AC1: "search... work across the full item set, not
 * just the active type view"), never pre-narrowed to the active tab. */
export function matchesSearch(item: ContentLibraryItem, term: string): boolean {
	const needle = term.trim().toLowerCase();
	if (!needle) return true;
	return (
		item.name.toLowerCase().includes(needle) ||
		item.description.toLowerCase().includes(needle) ||
		item.type.toLowerCase().includes(needle)
	);
}

export function itemTotalSize(item: ContentLibraryItem): number {
	return item.files.reduce((sum, f) => sum + f.size, 0);
}

export type ContentLibrarySortKey = "name" | "size" | "updated_at";

export const CONTENT_LIBRARY_SORT_OPTIONS: { value: ContentLibrarySortKey; label: string }[] = [
	{ value: "name", label: "Name" },
	{ value: "size", label: "Size" },
	{ value: "updated_at", label: "Last updated" },
];

/** Sorts a NEW array (never mutates its input) — ascending by name, descending
 * by size/updated_at (largest/most-recent first, matching `results`/`livejobs`
 * screens' own "newest first" default for time-ordered columns). */
export function sortItems(items: ContentLibraryItem[], key: ContentLibrarySortKey): ContentLibraryItem[] {
	const copy = [...items];
	switch (key) {
		case "name":
			return copy.sort((a, b) => a.name.localeCompare(b.name));
		case "size":
			return copy.sort((a, b) => itemTotalSize(b) - itemTotalSize(a));
		case "updated_at":
			return copy.sort((a, b) => (a.updated_at < b.updated_at ? 1 : a.updated_at > b.updated_at ? -1 : 0));
		default:
			return copy;
	}
}

export function formatBytes(bytes: number | null | undefined): string {
	if (bytes === null || bytes === undefined || !Number.isFinite(bytes) || bytes < 0) {
		return "—";
	}
	const units = ["B", "KB", "MB", "GB", "TB"];
	let value = bytes;
	let unitIndex = 0;
	while (value >= 1024 && unitIndex < units.length - 1) {
		value /= 1024;
		unitIndex += 1;
	}
	const precision = unitIndex === 0 ? 0 : 1;
	return `${value.toFixed(precision)} ${units[unitIndex]}`;
}
