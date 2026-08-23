/**
 * Library "Repository" tab data layer (docs/api-contract.md "Library & content
 * library" + prototype screen 7):
 *
 *   GET /library/items             — mode-aware presence over the depot catalog
 *   GET /library/request-manifest  — air-gapped "Export request manifest" want-list
 *
 * `backend/Waypoint.Api/Controllers/LibraryController.cs` builds both from the
 * existing depot_artifacts catalog (no separate library store) via
 * `Waypoint.Core.Catalog.LibraryPresenceEvaluator`.
 */

import { apiGet } from "../../lib/api";

export type LibraryPresence = "present" | "superseded" | "in_depot" | "missing";

export interface LibraryItem {
	id: string;
	external_id: string;
	product: string | null;
	version: string | null;
	status: string;
	presence: LibraryPresence;
	size_bytes: number | null;
	provenance: string;
	indexed_at: string;
	updated_at: string;
}

export interface LibraryFamily {
	name: string;
	present_count: number;
	missing_count: number;
}

export interface LibraryItemsResponse {
	mode: "connected" | "disconnected";
	items: LibraryItem[];
	families: LibraryFamily[];
}

export function fetchLibraryItems(): Promise<LibraryItemsResponse> {
	return apiGet<LibraryItemsResponse>("/library/items");
}

export interface LibraryRequestManifestEntry {
	external_id: string;
	product: string | null;
	version: string | null;
	reason: string;
}

export interface LibraryRequestManifestResponse {
	generated_at: string;
	appliance_mode: string;
	wanted: LibraryRequestManifestEntry[];
}

export function fetchLibraryRequestManifest(): Promise<LibraryRequestManifestResponse> {
	return apiGet<LibraryRequestManifestResponse>("/library/request-manifest");
}

/** Client-side filter applied on top of the fetched item list (prototype screen 7's "All items / Present only / Missing only" select). */
export type LibraryPresenceFilter = "all" | "present" | "missing";

export function matchesPresenceFilter(item: LibraryItem, filter: LibraryPresenceFilter): boolean {
	if (filter === "all") return true;
	if (filter === "present") return item.presence === "present" || item.presence === "superseded";
	return item.presence === "in_depot" || item.presence === "missing";
}

/** Builds the downloadable `Blob` for the "Export request manifest" action — a plain JSON file, machine-readable by a connected instance (docs/api-contract.md). */
export function requestManifestToBlob(manifest: LibraryRequestManifestResponse): Blob {
	return new Blob([JSON.stringify(manifest, null, 2)], { type: "application/json" });
}

export function formatBytes(bytes: number | null): string {
	if (bytes === null || !Number.isFinite(bytes) || bytes < 0) {
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

export const PRESENCE_LABELS: Record<LibraryPresence, string> = {
	present: "present",
	superseded: "superseded",
	in_depot: "in depot",
	missing: "missing",
};
