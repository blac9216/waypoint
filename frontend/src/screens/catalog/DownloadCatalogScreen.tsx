/**
 * Download Catalog — docs/ui/prototype/README.md screen 6, against
 * docs/api-contract.md's "Download Catalog" ledger row
 * (`/catalog/artifacts`, `/catalog/sync`, `/downloads`, `/system`).
 *
 * The screen itself is only reachable in connected mode already (the
 * `catalog` route is `connectedOnly` in lib/router.tsx and hidden from the
 * nav by LeftRail) — this component adds a defense-in-depth stub for the
 * case where it somehow renders anyway (e.g. a stale SPA shell after a mode
 * flip), matching the README: "Mode toggle ... Hides the Download Catalog
 * nav item; if the user is on the catalog when switching, redirects to
 * Transfer."
 */
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useAuth } from "../../lib/auth-context";
import { ApiError } from "../../lib/api";
import type { WaypointEvent } from "../../lib/events";
import { roleAtLeast, roleGateProps } from "../../lib/roles";
import { useSystem } from "../../lib/system-context";
import {
	DISPLAY_STATUS_LABELS,
	displayStatus,
	fetchCatalogArtifacts,
	filterArtifactsBySearch,
	formatEta,
	formatRate,
	formatTransferEstimate,
	friendlyProductName,
	groupArtifactsByProduct,
	isKubernetesProduct,
	queueBinariesDownload,
	queueDownloads,
	syncCatalog,
	type ArtifactStatus,
	type CatalogArtifact,
	type CatalogArtifactsQuery,
	type DownloadQueueItem,
	type ProductType,
} from "./catalog";
import { useDownloadQueue } from "./useDownloadQueue";
import { useCatalogPull, type UseCatalogPullResult } from "./useCatalogPull";
import { ProductGroupList } from "./ProductGroupList";
import { StoresUsagePanel } from "./StoresUsagePanel";
import "./DownloadCatalogScreen.css";

const TYPE_OPTIONS: { value: ProductType | ""; label: string }[] = [
	{ value: "", label: "Any type" },
	{ value: "core", label: "Core infrastructure" },
	{ value: "kubernetes", label: "Kubernetes" },
];

// Issue #1768: values are the backend's own `DepotArtifactStatuses.All`
// vocabulary (`indexed`/`downloading`/`present`/`failed`/`missing`) — this
// filter binds server-side (`ListArtifacts`'s `status` query parameter), so
// every value offered here must be one the API actually accepts, in this
// order. Labels are derived through `displayStatus`/`DISPLAY_STATUS_LABELS`
// (review round 1 finding F1 — a prior version of this comment claimed
// that already, while the labels underneath were still hand-written
// literals that could drift from the table's own copy) so the dropdown's
// text can never drift from `ArtifactTable`'s.
const STATUS_ORDER: ArtifactStatus[] = ["indexed", "downloading", "present", "failed", "missing"];
const STATUS_OPTIONS: { value: ArtifactStatus | ""; label: string }[] = [
	{ value: "", label: "Any status" },
	...STATUS_ORDER.map((value) => ({
		value,
		label: DISPLAY_STATUS_LABELS[displayStatus(value) ?? "not_downloaded"],
	})),
];

// Run-level terminal states (docs/api-contract.md's `run.progress` `state`
// field, mirroring HistoryPanel.tsx's TERMINAL_STATES) — used only to know
// when to drop this screen's own `queuedByArtifact` bookkeeping below, never
// to fabricate a per-artifact status.
const RUN_TERMINAL_STATES = new Set(["completed", "completed_with_failures", "aborted"]);

// `DownloadQueueItem.state` values that mean an artifact is genuinely in
// flight on the legacy `download` path (docs/api-contract.md's queue-item
// states) — used only to decide whether a fresh binaries-download enqueue's
// "queued" placeholder should yield to an existing `byArtifact` entry
// (review round 2 finding C: a TERMINAL legacy entry — `verified`/`failed` —
// carries no live progress and must not win over the fresh enqueue).
const IN_FLIGHT_QUEUE_STATES = new Set(["queued", "downloading", "verifying"]);

function formatSyncTime(iso: string | null): string {
	if (!iso) {
		return "never synced";
	}
	const date = new Date(iso);
	if (Number.isNaN(date.getTime())) {
		return "never synced";
	}
	return date.toISOString().slice(0, 16).replace("T", " ") + "Z";
}

export function DownloadCatalogScreen() {
	const { user, token } = useAuth();
	const { mode } = useSystem();

	const [artifacts, setArtifacts] = useState<CatalogArtifact[]>([]);
	const [indexSyncedAt, setIndexSyncedAt] = useState<string | null>(null);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);
	const [syncing, setSyncing] = useState(false);

	const [search, setSearch] = useState("");
	const [product, setProduct] = useState("");
	const [version, setVersion] = useState("");
	const [status, setStatus] = useState<ArtifactStatus | "">("");
	const [type, setType] = useState<ProductType | "">("");

	const [selected, setSelected] = useState<Set<string>>(new Set());
	const [queueError, setQueueError] = useState<string | null>(null);
	const [queueing, setQueueing] = useState(false);
	const [binariesQueueError, setBinariesQueueError] = useState<string | null>(null);
	const [binariesQueueing, setBinariesQueueing] = useState(false);
	// Issue #1487 finding 1: `POST /downloads/binaries` never touches
	// `downloads`/`depot_artifacts` (that's issue #1482), so there is no
	// server-side "queued" status to re-fetch. This is client-only,
	// session-scoped bookkeeping from the enqueue response itself — never a
	// fabricated persisted status — cleared once the run reaches a terminal
	// state (see the `run.progress` subscription below) or on reload (this
	// state does not survive one).
	const [binariesRunNotice, setBinariesRunNotice] = useState<{ runId: string; count: number } | null>(null);
	const [queuedByArtifact, setQueuedByArtifact] = useState<Map<string, string>>(new Map());

	// Drops `queuedByArtifact`/`binariesRunNotice` bookkeeping for a run once
	// it reaches a terminal state — the only "clear" trigger besides a reload
	// (this state is in-memory only, so a reload already starts empty). Fed
	// as `useDownloadQueue`'s `onEvent` sink below (docs/api-contract.md
	// "Event streams (SSE)": `run.progress` is run-scoped and carries the
	// run's lifecycle `state`) rather than a second `connectEventStream` call
	// on the same `/api/v1/events` URL — two independent readers of one
	// stream is not a real distinction the mock (and, more importantly, an
	// actual SSE `EventSource`) is built to share cleanly.
	const handleQueueEvent = useCallback((event: WaypointEvent) => {
		if (event.type !== "run.progress" || !event.run_id) {
			return;
		}
		const data = event.data as { state?: string };
		if (!data.state || !RUN_TERMINAL_STATES.has(data.state)) {
			return;
		}
		const runId = event.run_id;
		setQueuedByArtifact((prev) => {
			let changed = false;
			const next = new Map(prev);
			for (const [artifactId, artifactRunId] of prev) {
				if (artifactRunId === runId) {
					next.delete(artifactId);
					changed = true;
				}
			}
			return changed ? next : prev;
		});
		setBinariesRunNotice((prev) => (prev && prev.runId === runId ? null : prev));
	}, []);

	const {
		items: queueItems,
		byArtifact,
		seedError: queueSeedError,
	} = useDownloadQueue(token, Boolean(user), handleQueueEvent);
	const catalogPull = useCatalogPull();

	// Issue #1592: a per-load generation counter plus an AbortController,
	// aborted by the next `load` call, guard against a superseded walk's
	// result overwriting a newer one. The generation check (not just the
	// abort) is what actually protects state: a mocked/real fetch can still
	// resolve after being aborted, so `.then`/`.catch` above also verify this
	// call is still the latest before touching state.
	const loadGenerationRef = useRef(0);
	const loadAbortRef = useRef<AbortController | null>(null);

	const load = useCallback((query: CatalogArtifactsQuery) => {
		loadAbortRef.current?.abort();
		const controller = new AbortController();
		loadAbortRef.current = controller;
		const generation = ++loadGenerationRef.current;
		const isCurrent = () => loadGenerationRef.current === generation;

		setLoading(true);
		setLoadError(null);
		fetchCatalogArtifacts(query, controller.signal)
			.then((res) => {
				if (!isCurrent()) {
					return;
				}
				setArtifacts(res.artifacts);
				setIndexSyncedAt(res.index_synced_at);
			})
			.catch((err: unknown) => {
				if (!isCurrent() || controller.signal.aborted) {
					return;
				}
				setLoadError(err instanceof ApiError ? err.message : "Could not load the download catalog.");
			})
			.finally(() => {
				if (isCurrent()) {
					setLoading(false);
				}
			});
	}, []);

	// Re-walks the catalog only when a server-bound filter changes
	// (`product`/`version`/`status` — `ListArtifacts`'s own query
	// parameters). `search` has no server query parameter to bind to
	// (issue #468) and is filtered client-side below over the already-walked
	// set, so it is deliberately not a dependency here — issue #1592: a
	// changed search term must never trigger a fresh walk.
	useEffect(() => {
		load({
			product: product || undefined,
			version: version || undefined,
			status: status || undefined,
		});
		// Review round 1 finding F4: without this cleanup, the walk started
		// above is aborted only by the *next* `load` call, never by unmount —
		// navigating away mid-walk left up to six serial page requests in
		// flight, each still calling `setArtifacts`/`setIndexSyncedAt` on an
		// unmounted component when they resolved. Abort whatever `load` most
		// recently started so unmount cancels it the same way a superseded
		// `load` call already does.
		return () => {
			loadAbortRef.current?.abort();
		};
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, [product, version, status, load]);

	// The search-filtered view of the walked superset (issue #1592) — every
	// downstream computation (filter options, grouping, selection) reads
	// this, not the raw walk result, matching the pre-#1592 behaviour where
	// `fetchCatalogArtifacts` itself applied the search filter before
	// `artifacts` was ever set.
	const searchedArtifacts = useMemo(() => filterArtifactsBySearch(artifacts, search), [artifacts, search]);

	// Drop any selection that no longer exists in the current filtered result.
	useEffect(() => {
		setSelected((prev) => {
			const ids = new Set(searchedArtifacts.map((a) => a.id));
			const next = new Set([...prev].filter((id) => ids.has(id)));
			return next.size === prev.size ? prev : next;
		});
	}, [searchedArtifacts]);

	const productOptions = useMemo(() => {
		const set = new Set(searchedArtifacts.map((a) => a.product));
		return Array.from(set).sort((a, b) => friendlyProductName(a).localeCompare(friendlyProductName(b)));
	}, [searchedArtifacts]);

	const versionOptions = useMemo(() => {
		const set = new Set(searchedArtifacts.map((a) => a.version));
		return Array.from(set).sort();
	}, [searchedArtifacts]);

	// Type (core vs. Kubernetes-stack) is a client-side classification of the
	// catalog key (see catalog.ts's isKubernetesProduct) — the backend has no
	// such field to filter on server-side.
	const typedArtifacts = useMemo(
		() =>
			type
				? searchedArtifacts.filter((a) => isKubernetesProduct(a.product) === (type === "kubernetes"))
				: searchedArtifacts,
		[searchedArtifacts, type],
	);

	const groups = useMemo(() => groupArtifactsByProduct(typedArtifacts), [typedArtifacts]);

	// Issue #1487 finding 3: a stale binariesQueueError from a previous
	// selection must not resurface once the operator changes the selection —
	// every path that mutates `selected` also drops it.
	const toggleSelected = useCallback((id: string) => {
		setBinariesQueueError(null);
		setSelected((prev) => {
			const next = new Set(prev);
			if (next.has(id)) {
				next.delete(id);
			} else {
				next.add(id);
			}
			return next;
		});
	}, []);

	// Toggles selection for exactly the given ids (one product group's
	// artifacts) — selects them all if any is unselected, else clears them.
	const toggleSelectGroup = useCallback((ids: string[]) => {
		setBinariesQueueError(null);
		setSelected((prev) => {
			const allSelected = ids.length > 0 && ids.every((id) => prev.has(id));
			const next = new Set(prev);
			for (const id of ids) {
				if (allSelected) {
					next.delete(id);
				} else {
					next.add(id);
				}
			}
			return next;
		});
	}, []);

	const clearSelection = useCallback(() => {
		setBinariesQueueError(null);
		setSelected(new Set());
	}, []);

	const canQueue = user ? roleAtLeast(user.role, "Operator") : false;
	const queueGate = user
		? roleGateProps(user.role, "Operator", `Requires Operator or Admin — downloads are not available to ${user.role}`)
		: { disabled: true };
	const adminGate = user
		? roleGateProps(user.role, "Admin", `Requires Admin — pulling the vendor catalog is not available to ${user.role}`)
		: { disabled: true };

	const doQueue = useCallback(
		async (ids: string[]) => {
			if (ids.length === 0 || !canQueue) {
				return;
			}
			setQueueing(true);
			setQueueError(null);
			try {
				await queueDownloads(ids);
				clearSelection();
				// Re-fetch so freshly-queued rows flip to `queued` immediately;
				// live progress from here on is SSE-only. `search` is not a
				// server query parameter (issue #1592) so it is not part of
				// this re-walk's query either.
				load({
					product: product || undefined,
					version: version || undefined,
					status: status || undefined,
				});
			} catch (err) {
				setQueueError(err instanceof ApiError ? err.message : "Could not queue the selected downloads.");
			} finally {
				setQueueing(false);
			}
		},
		[canQueue, clearSelection, load, product, version, status],
	);

	const retryArtifact = useCallback((id: string) => doQueue([id]), [doQueue]);

	/**
	 * Issue #1487: the new connected binaries-download path
	 * (`POST /downloads/binaries`), distinct from `doQueue`'s legacy
	 * `POST /downloads` above. Same Operator+ floor (the endpoint's own
	 * `[RequireOperatorRole]`, mirrored client-side by `canQueue`/`queueGate`).
	 *
	 * Unlike the legacy path, this one is honestly NOT optimistic about
	 * per-artifact status: `DownloadsController.QueueBinariesDownload` only
	 * creates a run + one `binaries-download` job per artifact (finding 1 —
	 * the invoking job/catalog write is #1482), so a `load()` re-fetch here
	 * would show nothing changed and the doc comment this used to carry
	 * ("re-fetch so freshly-queued rows flip to `queued` immediately") would
	 * be false. Instead: clear the selection, surface the created run id
	 * (`binariesRunNotice`, with a link to Live Jobs) and mark the selected
	 * ids as queued for this session only (`queuedByArtifact`, fed straight
	 * from this response) — both cleared when the run reaches a terminal
	 * state via the `run.progress` subscription below, never persisted, never
	 * fabricated.
	 */
	const doBinariesQueue = useCallback(
		async (ids: string[]) => {
			if (ids.length === 0 || !canQueue) {
				return;
			}
			setBinariesQueueing(true);
			setBinariesQueueError(null);
			try {
				const res = await queueBinariesDownload(ids);
				setBinariesRunNotice({ runId: res.run_id, count: res.depot_artifact_ids.length });
				setQueuedByArtifact((prev) => {
					const next = new Map(prev);
					for (const id of res.depot_artifact_ids) {
						next.set(id, res.run_id);
					}
					return next;
				});
				clearSelection();
			} catch (err) {
				setBinariesQueueError(err instanceof ApiError ? err.message : "Could not queue the selected downloads.");
			} finally {
				setBinariesQueueing(false);
			}
		},
		[canQueue, clearSelection],
	);

	const doSync = useCallback(async () => {
		setSyncing(true);
		try {
			await syncCatalog();
		} catch (err) {
			setLoadError(err instanceof ApiError ? err.message : "Could not start a catalog sync.");
		} finally {
			setSyncing(false);
		}
	}, []);

	// Mode-gating stub: the route is already connectedOnly and hidden from
	// nav, but render an explicit explanation rather than a blank/broken
	// screen if this ever mounts on a disconnected instance.
	if (mode === "disconnected") {
		return (
			<div className="catalog-screen catalog-screen--gated">
				<div className="catalog-screen__gated-card">
					<h1>Download Catalog unavailable</h1>
					<p>
						This appliance is running in <strong>air-gapped mode</strong>. The download catalog and depot
						downloads only exist in connected mode — see the Library and Transfer screens for the
						air-gapped equivalents.
					</p>
				</div>
			</div>
		);
	}

	const selectedArtifacts = searchedArtifacts.filter((a) => selected.has(a.id));
	const selectedTotalBytes = selectedArtifacts.reduce((sum, a) => sum + a.size_bytes, 0);
	// Live aggregate rate: sum of currently-downloading jobs' measured
	// rate, so the estimate reflects real throughput when the queue is
	// active (falls back to an assumed bandwidth constant when it's 0 —
	// see formatTransferEstimate).
	const liveAggregateRate = queueItems.reduce(
		(sum, item) => (item.state === "downloading" ? sum + (item.rate_bytes_per_sec ?? 0) : sum),
		0,
	);
	const transferEstimate = formatTransferEstimate(selectedTotalBytes, liveAggregateRate);

	// Merges the real SSE-driven `byArtifact` (legacy `download` path) with a
	// synthesized "queued" placeholder for every artifact this session's own
	// `POST /downloads/binaries` calls just enqueued (`queuedByArtifact`) —
	// reuses ArtifactTable's existing `queued`/`--warn` status rendering
	// rather than inventing a second display path. `byArtifact` is seeded
	// from `GET /downloads`, which lists the WHOLE legacy queue with no state
	// filter (`DownloadsController.ListDownloads`), so an artifact previously
	// downloaded through the legacy path can already carry a TERMINAL entry
	// here (`verified`/`failed`) — that entry carries no live progress and
	// must not win over a fresh enqueue. The placeholder only yields to an
	// existing entry that is genuinely in flight (`queued`/`downloading`/
	// `verifying`); it overwrites a terminal one (or a missing one) so a
	// re-download of an already-verified/failed artifact still shows queued.
	const displayByArtifact = new Map(byArtifact);
	for (const [artifactId, runId] of queuedByArtifact) {
		const existing = displayByArtifact.get(artifactId);
		if (existing && IN_FLIGHT_QUEUE_STATES.has(existing.state)) {
			continue;
		}
		displayByArtifact.set(artifactId, {
			id: `binaries-${artifactId}`,
			artifact_id: artifactId,
			job_id: `binaries-${artifactId}`,
			run_id: runId,
			state: "queued",
			progress_percent: 0,
			rate_bytes_per_sec: null,
			eta_seconds: null,
			retries: 0,
		});
	}

	return (
		<div className="catalog-screen">
			<div className="catalog-screen__main">
				<div className="catalog-filterbar">
					<input
						type="search"
						className="catalog-filterbar__search"
						placeholder="Search artifacts…"
						value={search}
						onChange={(e) => setSearch(e.target.value)}
						aria-label="Search artifacts"
					/>
					<select value={product} onChange={(e) => setProduct(e.target.value)} aria-label="Filter by product">
						<option value="">Any product</option>
						{productOptions.map((p) => (
							<option key={p} value={p}>
								{friendlyProductName(p)} ({p})
							</option>
						))}
					</select>
					<select value={version} onChange={(e) => setVersion(e.target.value)} aria-label="Filter by version">
						<option value="">Any version</option>
						{versionOptions.map((v) => (
							<option key={v} value={v}>
								{v}
							</option>
						))}
					</select>
					<select
						value={status}
						onChange={(e) => setStatus(e.target.value as ArtifactStatus | "")}
						aria-label="Filter by status"
					>
						{STATUS_OPTIONS.map((opt) => (
							<option key={opt.value} value={opt.value}>
								{opt.label}
							</option>
						))}
					</select>
					<select value={type} onChange={(e) => setType(e.target.value as ProductType | "")} aria-label="Filter by type">
						{TYPE_OPTIONS.map((opt) => (
							<option key={opt.value} value={opt.value}>
								{opt.label}
							</option>
						))}
					</select>
					<div className="catalog-filterbar__spacer" />
					<div className="catalog-filterbar__sync mono">Index synced {formatSyncTime(indexSyncedAt)}</div>
					<button type="button" onClick={doSync} disabled={syncing} title="Walks the local /vcf filesystem only — no network call, no Broadcom contact.">
						{syncing ? "Re-indexing…" : "Local re-index"}
					</button>
				</div>

				{loadError && <div className="catalog-screen__error">{loadError}</div>}

				{binariesRunNotice && (
					<div className="catalog-screen__notice" aria-live="polite">
						Queued run {binariesRunNotice.runId} — {binariesRunNotice.count} artifact
						{binariesRunNotice.count === 1 ? "" : "s"}.{" "}
						<a href={`/live-jobs?run=${binariesRunNotice.runId}`} className="catalog-screen__notice-link">
							View in Live Jobs
						</a>
					</div>
				)}

				<CatalogPullPanel pull={catalogPull} adminGate={adminGate} />

				<ProductGroupList
					groups={groups}
					loading={loading}
					selected={selected}
					onToggle={toggleSelected}
					onToggleGroup={toggleSelectGroup}
					byArtifact={displayByArtifact}
					onRetry={retryArtifact}
					canQueue={canQueue}
				/>

				{selected.size > 0 && (
					<div className="catalog-footer">
						<div className="catalog-footer__summary">
							<span className="mono">{selected.size} selected</span>
							<span className="mono">{formatBytesInline(selectedTotalBytes)}</span>
							{transferEstimate && <span className="mono">{transferEstimate}</span>}
						</div>
						{binariesQueueError && <div className="catalog-footer__error">{binariesQueueError}</div>}
						{queueError && <div className="catalog-footer__error">{queueError}</div>}
						<div className="catalog-footer__spacer" />
						<button type="button" onClick={clearSelection}>
							Clear
						</button>
						{/* Legacy path (ADR-0030): POST /downloads, removed entirely by
						    issue #1040. Kept visually and lexically distinct from the new
						    binaries-download action below — never the primary button. */}
						<button
							type="button"
							className="catalog-footer__queue catalog-footer__queue--legacy"
							onClick={() => doQueue(Array.from(selected))}
							{...queueGate}
							disabled={queueGate.disabled || queueing}
							title={queueGate.title ?? "Legacy queue path — superseded by Download, removed in a later issue."}
						>
							{queueing ? "Queuing…" : `Legacy download (UMDS-only) — ${selected.size}`}
						</button>
						<button
							type="button"
							className="catalog-footer__queue catalog-footer__queue--binaries"
							onClick={() => doBinariesQueue(Array.from(selected))}
							{...queueGate}
							disabled={queueGate.disabled || binariesQueueing}
						>
							{binariesQueueing ? "Queuing…" : `Download ${selected.size}`}
						</button>
					</div>
				)}
			</div>

			<aside className="catalog-rail">
				<DownloadQueuePanel byArtifact={byArtifact} artifacts={searchedArtifacts} seedError={queueSeedError} />
				<StoresUsagePanel />
			</aside>
		</div>
	);
}

/**
 * "Pull vendor catalog" — the connected counterpart to "Local re-index"
 * above, distinct in both copy and mechanism: this contacts Broadcom via the
 * installed managed tool and the stored, validated Activation Code
 * (`POST /catalog/pull`), rather than walking the local `/vcf` filesystem.
 * Disabled-with-reason is driven entirely by `status.ready`/
 * `not_ready_reason` from `GET /catalog/pull` — never a client-side guess —
 * so it can never claim readiness the server doesn't also grant, and a raced
 * 409 `catalog_pull_not_ready` still surfaces via `actionError`.
 */
function CatalogPullPanel({
	pull,
	adminGate,
}: {
	pull: UseCatalogPullResult;
	adminGate: { disabled: boolean; style?: { opacity: number }; title?: string };
}) {
	const { status, loading, loadError, running, logLines, actionError, doPull } = pull;

	const serverReady = status?.ready ?? false;
	const pullDisabled = adminGate.disabled || running || loading || !serverReady;
	const pullTitle = adminGate.disabled
		? adminGate.title
		: running
			? "A catalog pull is already in progress"
			: !serverReady
				? status?.not_ready_reason
				: undefined;

	let lastResultText: string | null = null;
	let lastResultBad = false;
	if (status?.last_outcome === "succeeded") {
		const count = status.last_success_item_count;
		lastResultText =
			count === 0
				? `Last pull succeeded — the vendor catalog reported 0 items.`
				: `Last pull succeeded — indexed ${count ?? "?"} item(s).`;
	} else if (status?.last_outcome === "failed" || status?.last_outcome === "auth_failed") {
		lastResultBad = true;
		lastResultText = `Last pull failed${status.last_failure_reason ? `: ${status.last_failure_reason}` : "."}`;
	}

	return (
		<div className="catalog-pull-panel">
			<div className="catalog-pull-panel__header">
				<div className="catalog-pull-panel__title">
					PULL VENDOR CATALOG
					<span className="catalog-pull-panel__subtitle">Contacts Broadcom via the installed download tool — distinct from local re-index.</span>
				</div>
				<div className="catalog-filterbar__spacer" />
				<button type="button" onClick={() => void doPull()} disabled={pullDisabled} title={pullTitle}>
					{running ? "Pulling…" : "Pull vendor catalog"}
				</button>
			</div>

			{loadError && <div className="catalog-screen__error">{loadError}</div>}
			{actionError && <div className="catalog-screen__error">{actionError}</div>}

			{!loading && !serverReady && !running && status?.not_ready_reason && (
				<div className="catalog-pull-panel__not-ready">{status.not_ready_reason}</div>
			)}

			<div className="catalog-pull-panel__facts mono">
				<span>Last attempt {formatSyncTime(status?.last_attempt_at ?? null)}</span>
				<span>Last success {formatSyncTime(status?.last_success_at ?? null)}</span>
			</div>

			{lastResultText && !running && (
				<div className={lastResultBad ? "catalog-pull-panel__result--bad" : "catalog-pull-panel__result--ok"} aria-live="polite">
					{lastResultText}
				</div>
			)}

			{(running || logLines.length > 0) && (
				<div className="catalog-pull-panel__log mono" aria-live="polite">
					{logLines.length === 0 ? (
						<div className="catalog-pull-panel__log-empty">Waiting for progress…</div>
					) : (
						logLines.map((line) => <div key={line.seq}>{line.message}</div>)
					)}
				</div>
			)}
		</div>
	);
}

function formatBytesInline(bytes: number): string {
	if (bytes <= 0) return "0 B";
	const units = ["B", "KB", "MB", "GB", "TB"];
	let value = bytes;
	let i = 0;
	while (value >= 1024 && i < units.length - 1) {
		value /= 1024;
		i += 1;
	}
	return `${value.toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
}

function DownloadQueuePanel({
	byArtifact,
	artifacts,
	seedError,
}: {
	byArtifact: Map<string, DownloadQueueItem>;
	artifacts: CatalogArtifact[];
	seedError: string | null;
}) {
	const artifactNameById = useMemo(() => new Map(artifacts.map((a) => [a.id, a.name])), [artifacts]);
	const active = Array.from(byArtifact.values()).filter((item) => item.state !== "verified");

	return (
		<div className="catalog-panel">
			<div className="catalog-panel__title">DOWNLOAD QUEUE</div>
			{/* Issue #1780: a failed GET /downloads seed is surfaced distinctly
			    from a genuinely empty queue — the screen's existing error
			    pattern (`catalog-screen__error`), reused here rather than a new
			    class, since it is the same "something failed, here is why" shape. */}
			{seedError && <div className="catalog-screen__error">{seedError}</div>}
			{!seedError && active.length === 0 && <div className="catalog-panel__empty">No active downloads.</div>}
			<ul className="catalog-queue-list">
				{active.map((item) => (
					<li key={item.job_id} className="catalog-queue-item">
						<div className="catalog-queue-item__name mono">
							{artifactNameById.get(item.artifact_id) ?? item.artifact_id}
						</div>
						<div className="catalog-queue-item__bar">
							<div
								className="catalog-queue-item__bar-fill"
								style={{ width: `${Math.min(100, Math.max(0, item.progress_percent))}%` }}
							/>
						</div>
						<div className="catalog-queue-item__meta mono">
							<span>{item.state}</span>
							<span>{item.progress_percent}%</span>
							<span>{formatRate(item.rate_bytes_per_sec)}</span>
							<span>ETA {formatEta(item.eta_seconds)}</span>
							{item.retries > 0 && <span className="catalog-queue-item__retries">retries {item.retries}</span>}
						</div>
					</li>
				))}
			</ul>
		</div>
	);
}
