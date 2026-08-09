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
import { useCallback, useEffect, useMemo, useState } from "react";
import { useAuth } from "../../lib/auth-context";
import { ApiError } from "../../lib/api";
import { roleAtLeast, roleGateProps } from "../../lib/roles";
import { useSystem } from "../../lib/system-context";
import {
	fetchCatalogArtifacts,
	formatEta,
	formatRate,
	formatTransferEstimate,
	queueDownloads,
	syncCatalog,
	type ArtifactStatus,
	type CatalogArtifact,
	type CatalogArtifactsQuery,
	type DownloadQueueItem,
} from "./catalog";
import { useDownloadQueue } from "./useDownloadQueue";
import { ArtifactTable } from "./ArtifactTable";
import { StoresUsagePanel } from "./StoresUsagePanel";
import "./DownloadCatalogScreen.css";

const STATUS_OPTIONS: { value: ArtifactStatus | ""; label: string }[] = [
	{ value: "", label: "Any status" },
	{ value: "not_downloaded", label: "Not downloaded" },
	{ value: "queued", label: "Queued" },
	{ value: "downloading", label: "Downloading" },
	{ value: "verified", label: "Verified" },
	{ value: "failed", label: "Failed" },
];

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

	const [selected, setSelected] = useState<Set<string>>(new Set());
	const [queueError, setQueueError] = useState<string | null>(null);
	const [queueing, setQueueing] = useState(false);

	const { items: queueItems, byArtifact } = useDownloadQueue(token, Boolean(user));

	const load = useCallback((query: CatalogArtifactsQuery) => {
		setLoading(true);
		setLoadError(null);
		fetchCatalogArtifacts(query)
			.then((res) => {
				setArtifacts(res.artifacts);
				setIndexSyncedAt(res.index_synced_at);
			})
			.catch((err: unknown) => {
				setLoadError(err instanceof ApiError ? err.message : "Could not load the download catalog.");
			})
			.finally(() => setLoading(false));
	}, []);

	// Re-fetch whenever a filter changes. Debounced on `search` only — the
	// selects are discrete choices with no reason to wait.
	useEffect(() => {
		const query: CatalogArtifactsQuery = {
			search: search.trim() || undefined,
			product: product || undefined,
			version: version || undefined,
			status: status || undefined,
		};
		const handle = setTimeout(() => load(query), search ? 250 : 0);
		return () => clearTimeout(handle);
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, [search, product, version, status, load]);

	// Drop any selection that no longer exists in the current filtered result.
	useEffect(() => {
		setSelected((prev) => {
			const ids = new Set(artifacts.map((a) => a.id));
			const next = new Set([...prev].filter((id) => ids.has(id)));
			return next.size === prev.size ? prev : next;
		});
	}, [artifacts]);

	const productOptions = useMemo(() => {
		const set = new Set(artifacts.map((a) => a.product));
		return Array.from(set).sort();
	}, [artifacts]);

	const versionOptions = useMemo(() => {
		const set = new Set(artifacts.map((a) => a.version));
		return Array.from(set).sort();
	}, [artifacts]);

	const toggleSelected = useCallback((id: string) => {
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

	const toggleSelectAll = useCallback(() => {
		setSelected((prev) => {
			if (prev.size === artifacts.length && artifacts.length > 0) {
				return new Set();
			}
			return new Set(artifacts.map((a) => a.id));
		});
	}, [artifacts]);

	const clearSelection = useCallback(() => setSelected(new Set()), []);

	const canQueue = user ? roleAtLeast(user.role, "Operator") : false;
	const queueGate = user
		? roleGateProps(user.role, "Operator", `Requires Operator or Admin — downloads are not available to ${user.role}`)
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
				// live progress from here on is SSE-only.
				load({
					search: search.trim() || undefined,
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
		[canQueue, clearSelection, load, search, product, version, status],
	);

	const retryArtifact = useCallback((id: string) => doQueue([id]), [doQueue]);

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

	const selectedArtifacts = artifacts.filter((a) => selected.has(a.id));
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
								{p}
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
					<div className="catalog-filterbar__spacer" />
					<div className="catalog-filterbar__sync mono">Index synced {formatSyncTime(indexSyncedAt)}</div>
					<button type="button" onClick={doSync} disabled={syncing}>
						{syncing ? "Syncing…" : "Sync catalog"}
					</button>
				</div>

				{loadError && <div className="catalog-screen__error">{loadError}</div>}

				<ArtifactTable
					artifacts={artifacts}
					loading={loading}
					selected={selected}
					onToggle={toggleSelected}
					onToggleAll={toggleSelectAll}
					byArtifact={byArtifact}
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
						{queueError && <div className="catalog-footer__error">{queueError}</div>}
						<div className="catalog-footer__spacer" />
						<button type="button" onClick={clearSelection}>
							Clear
						</button>
						<button
							type="button"
							className="catalog-footer__queue"
							onClick={() => doQueue(Array.from(selected))}
							{...queueGate}
							disabled={queueGate.disabled || queueing}
						>
							{queueing ? "Queuing…" : `Queue ${selected.size} downloads`}
						</button>
					</div>
				)}
			</div>

			<aside className="catalog-rail">
				<DownloadQueuePanel byArtifact={byArtifact} artifacts={artifacts} />
				<StoresUsagePanel />
			</aside>
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
}: {
	byArtifact: Map<string, DownloadQueueItem>;
	artifacts: CatalogArtifact[];
}) {
	const artifactNameById = useMemo(() => new Map(artifacts.map((a) => [a.id, a.name])), [artifacts]);
	const active = Array.from(byArtifact.values()).filter((item) => item.state !== "verified");

	return (
		<div className="catalog-panel">
			<div className="catalog-panel__title">DOWNLOAD QUEUE</div>
			{active.length === 0 && <div className="catalog-panel__empty">No active downloads.</div>}
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
