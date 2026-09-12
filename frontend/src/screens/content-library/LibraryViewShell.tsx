/**
 * `LibraryViewShell` — the reusable list shell issue #1399 produces for #1422
 * (virtual-folder create/rename/item assignment chrome) and child D / #1429
 * (the extensible family-view pattern), plus #1054 (VKS reuse) per this
 * issue's own Home section. Deliberately generic: it knows nothing about
 * content-library items, folders, or families — it takes an already-filtered
 * `items` array, a caller-supplied `columns` set, and controlled search/sort
 * state, and renders exactly one searchable, sortable table.
 *
 * Reuse contract (this issue's AC2 — "no screen-specific logic baked into the
 * shell that a consumer can't opt out of"):
 *   - Filtering (by type, by folder, by family) is the CALLER's job, done
 *     before `items` is passed in. The shell never filters on your behalf.
 *   - Search/sort STATE is owned by the caller (controlled props) so it can
 *     be persisted anywhere the caller likes — this screen keeps it in the
 *     URL for deep-link stability (AC3); a future consumer is free to keep it
 *     in memory instead.
 *   - `toolbarExtra` is the seam for view-specific chrome next to the
 *     search/sort controls (e.g. #1399's own type tabs below, a future
 *     folder breadcrumb for #1422, or a family switcher for #1429) — it is
 *     rendered, never interpreted, by the shell.
 *   - `renderEmpty`/`emptyMessage` and `loading` are the only other opt-in
 *     seams; every other piece of behavior is the caller's.
 */
import type { ReactNode } from "react";

export interface LibraryViewShellColumn<T> {
	key: string;
	header: string;
	render: (item: T) => ReactNode;
	className?: string;
}

export interface LibraryViewShellSortOption<S extends string> {
	value: S;
	label: string;
}

export interface LibraryViewShellProps<T, S extends string> {
	items: T[];
	getId: (item: T) => string;
	columns: LibraryViewShellColumn<T>[];
	search: string;
	onSearchChange: (value: string) => void;
	searchPlaceholder?: string;
	searchAriaLabel?: string;
	sortOptions: LibraryViewShellSortOption<S>[];
	sortValue: S;
	onSortChange: (value: S) => void;
	sortAriaLabel?: string;
	loading?: boolean;
	emptyMessage?: string;
	/** Rendered between the search/sort controls and the table — the seam
	 * consumers (this screen's own type tabs, a future folder chrome, etc.)
	 * hang view-specific controls off without the shell needing to know
	 * anything about them. */
	toolbarExtra?: ReactNode;
	/** Rendered above the toolbar (e.g. a library picker) — a second, higher
	 * seam for chrome that belongs above search/sort rather than beside it. */
	headerExtra?: ReactNode;
}

export function LibraryViewShell<T, S extends string>({
	items,
	getId,
	columns,
	search,
	onSearchChange,
	searchPlaceholder = "search…",
	searchAriaLabel = "Search",
	sortOptions,
	sortValue,
	onSortChange,
	sortAriaLabel = "Sort by",
	loading = false,
	emptyMessage = "No items match the current filters.",
	toolbarExtra,
	headerExtra,
}: LibraryViewShellProps<T, S>) {
	return (
		<div className="library-view-shell">
			{headerExtra && <div className="library-view-shell__header">{headerExtra}</div>}

			<div className="library-view-shell__toolbar">
				<input
					type="search"
					className="library-view-shell__search"
					placeholder={searchPlaceholder}
					value={search}
					onChange={(e) => onSearchChange(e.target.value)}
					aria-label={searchAriaLabel}
				/>
				<select
					className="library-view-shell__sort"
					value={sortValue}
					onChange={(e) => onSortChange(e.target.value as S)}
					aria-label={sortAriaLabel}
				>
					{sortOptions.map((opt) => (
						<option key={opt.value} value={opt.value}>
							{opt.label}
						</option>
					))}
				</select>
				{toolbarExtra && <div className="library-view-shell__toolbar-extra">{toolbarExtra}</div>}
			</div>

			<div className="library-view-shell__table-wrap">
				<table className="library-view-shell__table">
					<thead>
						<tr>
							{columns.map((col) => (
								<th key={col.key} className={col.className}>
									{col.header}
								</th>
							))}
						</tr>
					</thead>
					<tbody>
						{items.map((item) => (
							<tr key={getId(item)}>
								{columns.map((col) => (
									<td key={col.key} className={col.className}>
										{col.render(item)}
									</td>
								))}
							</tr>
						))}
						{!loading && items.length === 0 && (
							<tr>
								<td colSpan={columns.length} className="library-view-shell__empty">
									{emptyMessage}
								</td>
							</tr>
						)}
					</tbody>
				</table>
			</div>
		</div>
	);
}
