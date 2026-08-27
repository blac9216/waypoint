/**
 * Component board (issue #757, epic #726 §7): the run-centric state board
 * for 10,000+-job scans. Three bounded pieces, none of which ever loads the
 * run's full job set:
 *
 *   1. Grouped counters — rendered ENTIRELY from the server-side GROUP BY
 *      counts endpoint (vocabulary-sized), grouped by priority with
 *      per-state chips. Clicking a state chip toggles it as a list filter.
 *   2. Virtualized component list — `computeWindow` renders only the
 *      on-screen slice of the cursor-paged rows; scrolling near the loaded
 *      end triggers `loadMore()` for the next server page. Search/filter
 *      changes reset the paged set (useComponentJobs).
 *   3. Selected-item detail — log-first: events load ONLY when an item is
 *      selected, through `fetchAllJobEventHistory`'s events/truncated/
 *      loadMore contract (issue #721/PR #931), scoped by `jobId`. Per-item
 *      controls ride the EXISTING endpoints: cancel (DELETE /jobs/{id},
 *      cooperative, non-terminal states) and retry (POST
 *      /runs/{runId}/jobs/{jobId}/retry, `failed` only) — shown only where
 *      legal for the item's state, per ADR-0024 attempt semantics.
 *
 * Bulk operations and bounded SSE live-updates are issue #757's stated
 * remainder; the board offers an explicit Refresh instead of folding SSE in
 * this slice.
 */
import { useEffect, useMemo, useRef, useState } from "react";
import { fetchAllJobEventHistory } from "../../api/jobEventHistory";
import { ApiError } from "../../lib/api";
import { useAuth } from "../../lib/auth-context";
import type { WaypointEvent } from "../../lib/events";
import { roleGateProps } from "../../lib/roles";
import { computeWindow, type ComponentJobFilters, type ComponentJobItem } from "./componentJobs";
import { cancelJob, retryJob, TERMINAL_JOB_STATES, type JobState } from "./liverun";
import { useComponentJobs } from "./useComponentJobs";

const ROW_HEIGHT = 34;
const VIEWPORT_HEIGHT = 480;
/** Trigger the next page while this many unrendered rows remain below. */
const LOAD_MORE_SLACK_ROWS = 40;
const EVENT_BATCH = 500;

const CANCELLABLE_STATES: ReadonlySet<string> = new Set(["queued", "running", "attesting", "converting", "blocked"]);

export function ComponentJobBoard({ runId }: { runId: string }) {
	const { user } = useAuth();
	const [search, setSearch] = useState("");
	const [stateFilter, setStateFilter] = useState<string | null>(null);
	const [selected, setSelected] = useState<ComponentJobItem | null>(null);
	const [scrollTop, setScrollTop] = useState(0);
	const [actionError, setActionError] = useState<string | null>(null);
	const [actingJobId, setActingJobId] = useState<string | null>(null);

	const filters = useMemo<ComponentJobFilters>(
		() => ({
			search: search.trim() || undefined,
			state: stateFilter ?? undefined,
		}),
		[search, stateFilter],
	);

	const { counts, items, hasMore, loading, loadingMore, error, loadMore, refresh } = useComponentJobs(runId, filters);

	const win = computeWindow(scrollTop, VIEWPORT_HEIGHT, ROW_HEIGHT, items.length);

	// Near-the-end page fetch: when the window's end approaches the loaded row
	// count and the server has more, pull the next page. Effect (not scroll
	// handler) so a short list that never scrolls still fills the viewport.
	useEffect(() => {
		if (hasMore && !loadingMore && win.end + LOAD_MORE_SLACK_ROWS >= items.length) {
			loadMore();
		}
	}, [hasMore, loadingMore, win.end, items.length, loadMore]);

	// Group the counts by priority for the board's counters.
	const priorities = useMemo(() => {
		const byPriority = new Map<number, { total: number; byState: Map<string, number> }>();
		for (const row of counts) {
			let group = byPriority.get(row.priority);
			if (!group) {
				group = { total: 0, byState: new Map<string, number>() };
				byPriority.set(row.priority, group);
			}
			group.total += row.count;
			group.byState.set(row.state, (group.byState.get(row.state) ?? 0) + row.count);
		}
		return [...byPriority.entries()].sort((a, b) => a[0] - b[0]);
	}, [counts]);

	const totalJobs = useMemo(() => counts.reduce((sum, row) => sum + row.count, 0), [counts]);

	const controlGate = user?.role ? roleGateProps(user.role, "Operator") : { disabled: true, style: { opacity: 0.42 } };

	async function handleCancel(item: ComponentJobItem) {
		if (!window.confirm(`Cancel "${item.target_name ?? item.id}"? This cannot be undone.`)) {
			return;
		}
		setActionError(null);
		setActingJobId(item.id);
		try {
			await cancelJob(item.id);
			refresh();
		} catch (err) {
			setActionError(err instanceof ApiError ? err.message : "Could not cancel the job.");
		} finally {
			setActingJobId(null);
		}
	}

	async function handleRetry(item: ComponentJobItem) {
		setActionError(null);
		setActingJobId(item.id);
		try {
			await retryJob(runId, item.id);
			refresh();
		} catch (err) {
			setActionError(err instanceof ApiError ? err.message : "Could not retry the job.");
		} finally {
			setActingJobId(null);
		}
	}

	return (
		<div className="component-board">
			<div className="component-board__counters" aria-label="Grouped component-job counts">
				<div className="component-board__total mono">{totalJobs} component jobs</div>
				{priorities.map(([priority, group]) => (
					<div key={priority} className="component-board__priority-group">
						<div className="component-board__priority-title mono">
							PRIORITY {priority} · {group.total}
						</div>
						<div className="component-board__state-chips">
							{[...group.byState.entries()].sort().map(([state, count]) => (
								<button
									key={state}
									type="button"
									className={`component-board__state-chip${stateFilter === state ? " is-active" : ""}`}
									onClick={() => setStateFilter((prev) => (prev === state ? null : state))}
								>
									{state} <span className="mono">{count}</span>
								</button>
							))}
						</div>
					</div>
				))}
			</div>

			<div className="component-board__toolbar">
				<input
					type="search"
					className="component-board__search"
					placeholder="Search components by name…"
					value={search}
					onChange={(e) => setSearch(e.target.value)}
					aria-label="Search components"
				/>
				<button type="button" onClick={refresh}>
					Refresh
				</button>
				{stateFilter && (
					<button type="button" onClick={() => setStateFilter(null)}>
						Clear state filter: {stateFilter}
					</button>
				)}
			</div>

			{error && <div className="component-board__error">{error}</div>}
			{actionError && <div className="component-board__error">{actionError}</div>}
			{loading && <div className="component-board__loading">Loading components…</div>}

			{!loading && (
				<div className="component-board__split">
					<div
						className="component-board__list"
						style={{ height: VIEWPORT_HEIGHT, overflowY: "auto" }}
						onScroll={(e) => setScrollTop(e.currentTarget.scrollTop)}
						role="listbox"
						aria-label="Component jobs"
					>
						<div style={{ height: win.topPad }} />
						{items.slice(win.start, win.end).map((item) => (
							<button
								key={item.id}
								type="button"
								role="option"
								aria-selected={selected?.id === item.id}
								className={`component-board__row${selected?.id === item.id ? " is-selected" : ""}`}
								style={{ height: ROW_HEIGHT }}
								onClick={() => setSelected(item)}
							>
								<span className="component-board__row-name">{item.target_name ?? item.id}</span>
								<span className="mono component-board__row-meta">
									p{item.priority} · {item.component_kind} · {item.state}
								</span>
							</button>
						))}
						<div style={{ height: win.bottomPad }} />
						{loadingMore && <div className="component-board__loading">Loading more…</div>}
					</div>

					<div className="component-board__detail">
						{selected ? (
							<SelectedComponentDetail
								runId={runId}
								item={selected}
								controlGate={controlGate}
								actingJobId={actingJobId}
								onCancel={() => void handleCancel(selected)}
								onRetry={() => void handleRetry(selected)}
							/>
						) : (
							<div className="component-board__detail-empty">Select a component to view its log and controls.</div>
						)}
					</div>
				</div>
			)}
		</div>
	);
}

/**
 * Log-first detail for exactly one selected item — this component is the
 * ONLY place item events load, and it loads them scoped to `jobId` through
 * PR #931's bounded events/truncated/loadMore contract.
 */
export function SelectedComponentDetail({
	runId,
	item,
	controlGate,
	actingJobId,
	onCancel,
	onRetry,
}: {
	runId: string;
	item: ComponentJobItem;
	controlGate: { disabled: boolean; style?: { opacity: number }; title?: string };
	actingJobId: string | null;
	onCancel: () => void;
	onRetry: () => void;
}) {
	const [events, setEvents] = useState<WaypointEvent[]>([]);
	const [truncated, setTruncated] = useState(false);
	const [nextCursor, setNextCursor] = useState<string | null>(null);
	const [loading, setLoading] = useState(true);
	const [error, setError] = useState<string | null>(null);
	const generation = useRef(0);

	useEffect(() => {
		const gen = ++generation.current;
		setEvents([]);
		setTruncated(false);
		setNextCursor(null);
		setError(null);
		setLoading(true);
		fetchAllJobEventHistory(runId, { jobId: item.id }, EVENT_BATCH)
			.then((result) => {
				if (generation.current !== gen) {
					return;
				}
				setEvents(result.events);
				setTruncated(result.truncated);
				setNextCursor(result.nextCursor);
			})
			.catch((err: unknown) => {
				if (generation.current === gen) {
					setError(err instanceof Error ? err.message : "Could not load the item's history.");
				}
			})
			.finally(() => {
				if (generation.current === gen) {
					setLoading(false);
				}
			});
	}, [runId, item.id]);

	function handleLoadMore() {
		if (!nextCursor) {
			return;
		}
		const gen = generation.current;
		fetchAllJobEventHistory(runId, { jobId: item.id }, EVENT_BATCH, nextCursor)
			.then((result) => {
				if (generation.current !== gen) {
					return;
				}
				setEvents((prev) => [...prev, ...result.events]);
				setTruncated(result.truncated);
				setNextCursor(result.nextCursor);
			})
			.catch((err: unknown) => {
				if (generation.current === gen) {
					setError(err instanceof Error ? err.message : "Could not load more history.");
				}
			});
	}

	const state = item.state as JobState;
	const canCancel = CANCELLABLE_STATES.has(item.state);
	const canRetry = item.state === "failed";
	const acting = actingJobId === item.id;

	return (
		<div className="component-board__detail-body">
			<div className="component-board__detail-header">
				<span className="component-board__detail-name">{item.target_name ?? item.id}</span>
				<span className="mono component-board__detail-meta">
					p{item.priority} · {item.component_kind} · {item.state}
					{item.stage ? ` · ${item.stage}` : ""} · attempts {item.attempt_count}
					{TERMINAL_JOB_STATES.has(state) ? " · terminal" : ""}
				</span>
				<div className="component-board__detail-controls">
					{canCancel && (
						<button type="button" {...controlGate} disabled={controlGate.disabled || acting} onClick={onCancel}>
							{acting ? "Working…" : "Cancel"}
						</button>
					)}
					{canRetry && (
						<button type="button" {...controlGate} disabled={controlGate.disabled || acting} onClick={onRetry}>
							{acting ? "Working…" : "Retry"}
						</button>
					)}
					{!canCancel && !canRetry && <span className="component-board__detail-nocontrols">No controls for this state.</span>}
				</div>
			</div>

			{loading && <div className="component-board__loading">Loading history…</div>}
			{error && <div className="component-board__error">{error}</div>}

			{!loading && !error && (
				<>
					<ul className="component-board__events mono" aria-label="Item events">
						{events.length === 0 && <li className="component-board__event-empty">No events recorded yet.</li>}
						{events.map((event) => (
							<li key={event.seq}>
								<span className="component-board__event-ts">{event.ts}</span> {event.type}{" "}
								{formatEventData(event)}
							</li>
						))}
					</ul>
					{truncated && (
						<div role="status" className="component-board__truncation">
							Showing the first {events.length} events — more history exists.{" "}
							<button type="button" onClick={handleLoadMore}>
								Load more history
							</button>
						</div>
					)}
				</>
			)}
		</div>
	);
}

function formatEventData(event: WaypointEvent): string {
	const data = event.data as Record<string, unknown> | undefined;
	if (!data) {
		return "";
	}
	if (typeof data.line === "string") {
		return data.line;
	}
	if (typeof data.message === "string") {
		return data.message;
	}
	if (typeof data.to === "string") {
		return `→ ${data.to}`;
	}
	return "";
}
