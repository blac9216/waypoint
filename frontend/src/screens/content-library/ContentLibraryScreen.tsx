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
 *
 * Virtual folders (issue #1422): `FolderTree` renders the folder panel from
 * `GET .../folders` (issue #1389); the selected folder narrows `visibleItems`
 * the same way the type tab does (search/sort still run over the FULL item
 * set, per AC1), and its id rides the URL for deep-link stability (AC2). Item
 * moves are a per-row menu (not drag-and-drop, per the issue's own Risks
 * section) applied optimistically with rollback on `ApiError`, matching the
 * existing screens' `ApiError` handling pattern.
 */
import { useCallback, useEffect, useMemo, useState } from "react";
import { useAuth } from "../../lib/auth-context";
import { ApiError } from "../../lib/api";
import { roleAtLeast } from "../../lib/roles";
import {
	CONTENT_LIBRARY_ITEM_TYPES,
	CONTENT_LIBRARY_SORT_OPTIONS,
	CONTENT_LIBRARY_TYPE_LABELS,
	assignContentLibraryItemFolder,
	buildItemFolderMap,
	createContentLibraryFolder,
	deleteContentLibraryFolder,
	fetchContentLibraries,
	fetchContentLibraryFolders,
	fetchContentLibraryItems,
	flattenFolderTree,
	formatBytes,
	itemTotalSize,
	matchesSearch,
	matchesTypeFilter,
	renameContentLibraryFolder,
	sortItems,
	type ContentLibrary,
	type ContentLibraryFolderNode,
	type ContentLibraryItem,
} from "./content-library";
import { FolderTree } from "./FolderTree";
import { LibraryViewShell, type LibraryViewShellColumn } from "./LibraryViewShell";
import { useContentLibraryViewFromQuery } from "./useContentLibraryViewFromQuery";
import "./ContentLibraryScreen.css";

/** Removes `itemId` from wherever it currently sits in the tree's `item_ids`
 * and adds it to `targetFolderId`'s node (a no-op add when `targetFolderId`
 * is `null` — the library root has no node of its own). Returns a NEW tree
 * (never mutates `nodes`), matching this codebase's "sort/filter return a new
 * array" convention (`sortItems` above). */
function applyOptimisticMove(
	nodes: ContentLibraryFolderNode[],
	itemId: string,
	previousFolderId: string | null,
	targetFolderId: string | null,
): ContentLibraryFolderNode[] {
	return nodes.map((node) => {
		let itemIds = node.item_ids;
		if (node.id === previousFolderId) {
			itemIds = itemIds.filter((id) => id !== itemId);
		}
		if (node.id === targetFolderId && !itemIds.includes(itemId)) {
			itemIds = [...itemIds, itemId];
		}
		return { ...node, item_ids: itemIds, children: applyOptimisticMove(node.children, itemId, previousFolderId, targetFolderId) };
	});
}

function buildColumns(
	itemFolderMap: Map<string, string>,
	flatFolders: { id: string; name: string; depth: number }[],
	canMove: boolean,
	moving: string | null,
	onMove: (itemId: string, folderId: string | null) => void,
): LibraryViewShellColumn<ContentLibraryItem>[] {
	return [
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
		{
			key: "folder",
			header: "FOLDER",
			render: (item) => (
				<select
					aria-label={`Move ${item.name} to folder`}
					className="content-library-item__move"
					value={itemFolderMap.get(item.id) ?? ""}
					disabled={!canMove || moving === item.id}
					onChange={(e) => onMove(item.id, e.target.value || null)}
				>
					<option value="">Unassigned</option>
					{flatFolders.map((f) => (
						<option key={f.id} value={f.id}>
							{" ".repeat(f.depth * 2)}
							{f.name}
						</option>
					))}
				</select>
			),
		},
	];
}

export function ContentLibraryScreen() {
	const { user } = useAuth();
	const { libraryId, type, search, sort, folderId, setView } = useContentLibraryViewFromQuery();

	const [libraries, setLibraries] = useState<ContentLibrary[]>([]);
	const [items, setItems] = useState<ContentLibraryItem[]>([]);
	const [folders, setFolders] = useState<ContentLibraryFolderNode[]>([]);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);
	const [moving, setMoving] = useState<string | null>(null);
	const [moveError, setMoveError] = useState<string | null>(null);

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

	// Also re-fetches the folder tree (issue #1422 AC1: a library repair — or
	// its test double, a plain re-fetch here — must never disturb folder
	// assignment, since folders are DB-only metadata that never touch
	// `disk_path`; re-running `load()` after a repair is exactly how that gets
	// proven end-to-end through the UI).
	const load = useCallback(() => {
		if (!activeLibraryId) {
			setItems([]);
			setFolders([]);
			setLoading(false);
			return;
		}
		setLoading(true);
		setLoadError(null);
		Promise.all([fetchContentLibraryItems(activeLibraryId), fetchContentLibraryFolders(activeLibraryId)])
			.then(([resItems, resFolders]) => {
				setItems(resItems);
				setFolders(resFolders);
			})
			.catch((err: unknown) => {
				setLoadError(err instanceof ApiError ? err.message : "Could not load this library's items.");
			})
			.finally(() => setLoading(false));
	}, [activeLibraryId]);

	useEffect(() => {
		load();
	}, [load]);

	const itemFolderMap = useMemo(() => buildItemFolderMap(folders), [folders]);
	const flatFolders = useMemo(() => flattenFolderTree(folders), [folders]);

	// Search/sort apply to the FULL fetched item set (AC1) — the active type
	// tab (and the selected folder, issue #1422) only narrow what's DISPLAYED,
	// never what's searched or sorted.
	const searchedAndSorted = useMemo(() => {
		const matched = items.filter((item) => matchesSearch(item, search));
		return sortItems(matched, sort);
	}, [items, search, sort]);

	const typeFiltered = useMemo(
		() => searchedAndSorted.filter((item) => matchesTypeFilter(item, type)),
		[searchedAndSorted, type],
	);

	const visibleItems = useMemo(() => {
		if (!folderId) return typeFiltered;
		return typeFiltered.filter((item) => itemFolderMap.get(item.id) === folderId);
	}, [typeFiltered, folderId, itemFolderMap]);

	const canMove = user ? roleAtLeast(user.role, "Operator") : false;

	const handleMove = useCallback(
		(itemId: string, targetFolderId: string | null) => {
			if (!activeLibraryId) return;
			const previousFolderId = itemFolderMap.get(itemId) ?? null;
			if (previousFolderId === targetFolderId) return;

			setMoveError(null);
			setMoving(itemId);
			// Optimistic update: patch the local folder tree's item_ids so the
			// move menu and folder counts reflect it immediately, then roll back
			// to the pre-move tree on an `ApiError` (matching the existing
			// screens' `ApiError` handling pattern) rather than leaving the UI
			// showing a move the server rejected.
			const previousFolders = folders;
			setFolders((current) => applyOptimisticMove(current, itemId, previousFolderId, targetFolderId));

			assignContentLibraryItemFolder(activeLibraryId, itemId, targetFolderId)
				.catch((err: unknown) => {
					setFolders(previousFolders);
					setMoveError(err instanceof ApiError ? err.message : "Could not move this item.");
				})
				.finally(() => setMoving(null));
		},
		[activeLibraryId, folders, itemFolderMap],
	);

	const columns = useMemo(
		() => buildColumns(itemFolderMap, flatFolders, canMove, moving, handleMove),
		[itemFolderMap, flatFolders, canMove, moving, handleMove],
	);

	const countsByType = useMemo(() => {
		const counts: Record<string, number> = { all: searchedAndSorted.length };
		for (const t of CONTENT_LIBRARY_ITEM_TYPES) {
			counts[t] = searchedAndSorted.filter((item) => item.type === t).length;
		}
		return counts;
	}, [searchedAndSorted]);

	// One panel backs every type tab (the tab only narrows what's displayed
	// within it, per AC1 above) — each tab still links to it via
	// `aria-controls` so assistive tech can associate a tab with the table it
	// governs (issue #1971).
	const itemsPanelId = "content-library-items-panel";

	const typeTabs = (
		<div className="content-library-tabs" role="tablist" aria-label="Filter by type">
			<button
				type="button"
				role="tab"
				aria-selected={type === "all"}
				aria-controls={itemsPanelId}
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
					aria-controls={itemsPanelId}
					className={`content-library-tab ${type === t ? "is-active" : ""}`}
					onClick={() => setView({ type: t })}
				>
					{CONTENT_LIBRARY_TYPE_LABELS[t]} <span className="mono">{countsByType[t]}</span>
				</button>
			))}
		</div>
	);

	const handleCreateFolder = useCallback(
		async (name: string, parentFolderId: string | null) => {
			if (!activeLibraryId) return;
			await createContentLibraryFolder(activeLibraryId, name, parentFolderId);
			load();
		},
		[activeLibraryId, load],
	);

	const handleRenameFolder = useCallback(
		async (targetFolderId: string, name: string, parentFolderId: string | null) => {
			if (!activeLibraryId) return;
			await renameContentLibraryFolder(activeLibraryId, targetFolderId, name, parentFolderId);
			load();
		},
		[activeLibraryId, load],
	);

	const handleDeleteFolder = useCallback(
		async (targetFolderId: string) => {
			if (!activeLibraryId) return;
			await deleteContentLibraryFolder(activeLibraryId, targetFolderId);
			if (folderId === targetFolderId) {
				setView({ folderId: undefined });
			}
			load();
		},
		[activeLibraryId, folderId, load, setView],
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
			{moveError && <div className="content-library-screen__error">{moveError}</div>}

			<div className="content-library-screen__body">
				<FolderTree
					folders={folders}
					selectedFolderId={folderId}
					onSelect={(id) => setView({ folderId: id })}
					role={user?.role}
					onCreate={handleCreateFolder}
					onRename={handleRenameFolder}
					onDelete={handleDeleteFolder}
				/>

				<LibraryViewShell
					items={visibleItems}
					getId={(item) => item.id}
					columns={columns}
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
					panelId={itemsPanelId}
				/>
			</div>
		</div>
	);
}
