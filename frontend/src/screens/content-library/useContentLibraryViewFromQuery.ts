/**
 * Deep-link view state for `/content-library` (issue #1399 AC3: "Deep links
 * to a specific view/type survive a page reload — state in the URL, not only
 * component state"). `?library=<id>&type=<type>&q=<term>&sort=<key>`,
 * following the same "ride the existing path's query string, write via
 * `pushState`" shape `livejobs/useSelectionFromQuery.ts` established (issue
 * #590 AC4) rather than graduating the hand-rolled router
 * (`lib/router.tsx`) to param routes for one screen.
 */
import { useCallback, useEffect, useState } from "react";
import type { ContentLibraryItemType } from "./content-library";
import type { ContentLibrarySortKey } from "./content-library";

export interface ContentLibraryView {
	libraryId: string | undefined;
	type: ContentLibraryItemType | "all";
	search: string;
	sort: ContentLibrarySortKey;
}

const DEFAULT_SORT: ContentLibrarySortKey = "name";

function isSortKey(value: string | null): value is ContentLibrarySortKey {
	return value === "name" || value === "size" || value === "updated_at";
}

function isTypeFilter(value: string | null): value is ContentLibraryItemType | "all" {
	return value === "all" || value === "vcsp.ovf" || value === "vcsp.iso" || value === "vcsp.other";
}

function readView(): ContentLibraryView {
	const params = new URLSearchParams(window.location.search);
	const type = params.get("type");
	const sort = params.get("sort");
	return {
		libraryId: params.get("library") ?? undefined,
		type: isTypeFilter(type) ? type : "all",
		search: params.get("q") ?? "",
		sort: isSortKey(sort) ? sort : DEFAULT_SORT,
	};
}

export interface UseContentLibraryViewFromQueryResult extends ContentLibraryView {
	/** Updates the URL (`pushState`, no reload) and local state together.
	 * Only the supplied keys change; omitted keys keep their current value. */
	setView: (next: Partial<ContentLibraryView>) => void;
}

export function useContentLibraryViewFromQuery(): UseContentLibraryViewFromQueryResult {
	const [view, setViewState] = useState<ContentLibraryView>(readView);

	useEffect(() => {
		const sync = () => setViewState(readView());
		window.addEventListener("popstate", sync);
		return () => window.removeEventListener("popstate", sync);
	}, []);

	const setView = useCallback((next: Partial<ContentLibraryView>) => {
		setViewState((current) => {
			const merged: ContentLibraryView = { ...current, ...next };
			const params = new URLSearchParams(window.location.search);
			if (merged.libraryId) {
				params.set("library", merged.libraryId);
			} else {
				params.delete("library");
			}
			if (merged.type === "all") {
				params.delete("type");
			} else {
				params.set("type", merged.type);
			}
			if (merged.search) {
				params.set("q", merged.search);
			} else {
				params.delete("q");
			}
			if (merged.sort === DEFAULT_SORT) {
				params.delete("sort");
			} else {
				params.set("sort", merged.sort);
			}
			const qs = params.toString();
			const url = `${window.location.pathname}${qs ? `?${qs}` : ""}`;
			window.history.pushState(null, "", url);
			return merged;
		});
	}, []);

	return { ...view, setView };
}
