/**
 * Content library view shell (issue #1399, epic #1185) — the base screen
 * every other #1056 child (virtual folders #1422, family views child D) and
 * #1054 (VKS reuse) mounts onto, per this issue's Home section. Renders one
 * library's items, split into per-type views (OVA/OVF, ISO, Files) as tabs,
 * with search and sort applied across the FULL item set (AC1) and the
 * current library/type/search/sort persisted in the URL for deep-link
 * stability (AC3, `useContentLibraryViewFromQuery.ts`).
 *
 * The list/detail rendering itself is delegated to `LibraryViewShell`
 * (AC2) — this screen supplies columns, the type-tab toolbar, and the
 * library picker; it bakes no folder or family logic into the shell.
 */
import { useCallback, useEffect, useMemo, useState } from "react";
import { ApiError } from "../../lib/api";
import {
	CONTENT_LIBRARY_ITEM_TYPES,
	CONTENT_LIBRARY_SORT_OPTIONS,
	CONTENT_LIBRARY_TYPE_LABELS,
	fetchContentLibraries,
	fetchContentLibraryItems,
	formatBytes,
	itemTotalSize,
	matchesSearch,
	matchesTypeFilter,
	sortItems,
	type ContentLibrary,
	type ContentLibraryItem,
} from "./content-library";
import { LibraryViewShell, type LibraryViewShellColumn } from "./LibraryViewShell";
import { useContentLibraryViewFromQuery } from "./useContentLibraryViewFromQuery";
import "./ContentLibraryScreen.css";

const COLUMNS: LibraryViewShellColumn<ContentLibraryItem>[] = [
	{
		key: "name",
		header: "ITEM",
		className: "content-library-col-item",
		render: (item) => (
			<div>
				<div className="mono content-library-item__name">{item.name}</div>
				{item.description && <div className="content-library-item__description">{item.description}</div>}
			</div>
		),
	},
	{ key: "type", header: "TYPE", render: (item) => CONTENT_LIBRARY_TYPE_LABELS[item.type] },
	{ key: "size", header: "SIZE", className: "mono", render: (item) => formatBytes(itemTotalSize(item)) },
	{ key: "version", header: "VERSION", className: "mono", render: (item) => item.version },
	{ key: "updated", header: "UPDATED", className: "mono", render: (item) => new Date(item.updated_at).toLocaleString() },
];

export function ContentLibraryScreen() {
	const { libraryId, type, search, sort, setView } = useContentLibraryViewFromQuery();

	const [libraries, setLibraries] = useState<ContentLibrary[]>([]);
	const [items, setItems] = useState<ContentLibraryItem[]>([]);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);

	useEffect(() => {
		fetchContentLibraries()
			.then((libs) => {
				setLibraries(libs);
				if (!libraryId && libs.length > 0) {
					setView({ libraryId: libs[0].id });
				}
			})
			.catch((err: unknown) => {
				setLoadError(err instanceof ApiError ? err.message : "Could not load content libraries.");
			});
		// Runs once — `setView` is stable and only used to seed the default library.
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, []);

	const activeLibraryId = libraryId ?? libraries[0]?.id;

	const load = useCallback(() => {
		if (!activeLibraryId) {
			setItems([]);
			setLoading(false);
			return;
		}
		setLoading(true);
		setLoadError(null);
		fetchContentLibraryItems(activeLibraryId)
			.then((res) => setItems(res))
			.catch((err: unknown) => {
				setLoadError(err instanceof ApiError ? err.message : "Could not load this library's items.");
			})
			.finally(() => setLoading(false));
	}, [activeLibraryId]);

	useEffect(() => {
		load();
	}, [load]);

	// Search/sort apply to the FULL fetched item set (AC1) — the active type
	// tab only narrows what's DISPLAYED, never what's searched or sorted.
	const searchedAndSorted = useMemo(() => {
		const matched = items.filter((item) => matchesSearch(item, search));
		return sortItems(matched, sort);
	}, [items, search, sort]);

	const visibleItems = useMemo(
		() => searchedAndSorted.filter((item) => matchesTypeFilter(item, type)),
		[searchedAndSorted, type],
	);

	const countsByType = useMemo(() => {
		const counts: Record<string, number> = { all: searchedAndSorted.length };
		for (const t of CONTENT_LIBRARY_ITEM_TYPES) {
			counts[t] = searchedAndSorted.filter((item) => item.type === t).length;
		}
		return counts;
	}, [searchedAndSorted]);

	const typeTabs = (
		<div className="content-library-tabs" role="tablist" aria-label="Filter by type">
			<button
				type="button"
				role="tab"
				aria-selected={type === "all"}
				className={`content-library-tab ${type === "all" ? "is-active" : ""}`}
				onClick={() => setView({ type: "all" })}
			>
				All <span className="mono">{countsByType.all}</span>
			</button>
			{CONTENT_LIBRARY_ITEM_TYPES.map((t) => (
				<button
					key={t}
					type="button"
					role="tab"
					aria-selected={type === t}
					className={`content-library-tab ${type === t ? "is-active" : ""}`}
					onClick={() => setView({ type: t })}
				>
					{CONTENT_LIBRARY_TYPE_LABELS[t]} <span className="mono">{countsByType[t]}</span>
				</button>
			))}
		</div>
	);

	const libraryPicker =
		libraries.length > 0 ? (
			<select
				className="content-library-picker"
				aria-label="Select content library"
				value={activeLibraryId ?? ""}
				onChange={(e) => setView({ libraryId: e.target.value })}
			>
				{libraries.map((lib) => (
					<option key={lib.id} value={lib.id}>
						{lib.name}
					</option>
				))}
			</select>
		) : (
			!loading && <span className="content-library-empty-note">No content libraries yet.</span>
		);

	return (
		<div className="content-library-screen">
			<div className="content-library-screen__header">
				<h1>Content Library</h1>
				<p className="content-library-screen__subtitle">Per-type views over one content library's items — search and sort across every type.</p>
			</div>

			{loadError && <div className="content-library-screen__error">{loadError}</div>}

			<LibraryViewShell
				items={visibleItems}
				getId={(item) => item.id}
				columns={COLUMNS}
				search={search}
				onSearchChange={(value) => setView({ search: value })}
				searchPlaceholder="search name, description, type…"
				searchAriaLabel="Search content library items"
				sortOptions={CONTENT_LIBRARY_SORT_OPTIONS}
				sortValue={sort}
				onSortChange={(value) => setView({ sort: value })}
				sortAriaLabel="Sort content library items"
				loading={loading}
				emptyMessage={loading ? "Loading…" : "No items match the current filters."}
				toolbarExtra={typeTabs}
				headerExtra={libraryPicker}
			/>
		</div>
	);
}
