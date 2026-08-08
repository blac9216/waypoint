/**
 * Live download-queue state, driven only by SSE (docs/api-contract.md
 * "Event streams (SSE)": `download.progress` is job-scoped without
 * exception, and every progress bar in the prototype binds to one of the
 * six event types — never a poll). Seeded once from `GET /downloads` so a
 * mid-flight queue is visible immediately on mount, then kept live purely
 * by the global event stream.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { API_BASE } from "../../lib/api";
import { connectEventStream, type WaypointEvent } from "../../lib/events";
import { fetchDownloadQueue, type DownloadQueueItem } from "./catalog";

interface DownloadProgressData {
	artifact_id?: string;
	progress_percent?: number;
	rate_bytes_per_sec?: number;
	eta_seconds?: number;
	retries?: number;
	state?: DownloadQueueItem["state"];
}

interface JobStateData {
	to?: string;
}

export interface UseDownloadQueueResult {
	items: DownloadQueueItem[];
	/** artifact_id -> latest queue item, for the table's inline per-row progress. */
	byArtifact: Map<string, DownloadQueueItem>;
}

export function useDownloadQueue(token: string | null, signedIn: boolean): UseDownloadQueueResult {
	const [items, setItems] = useState<DownloadQueueItem[]>([]);
	const itemsRef = useRef<DownloadQueueItem[]>([]);
	itemsRef.current = items;

	// Seed from the REST snapshot once per session so a mid-flight queue
	// renders immediately; everything after that is event-driven only.
	useEffect(() => {
		if (!signedIn) {
			setItems([]);
			return;
		}
		let cancelled = false;
		fetchDownloadQueue()
			.then((seed) => {
				if (!cancelled) {
					setItems(seed);
				}
			})
			.catch(() => {
				// Best-effort seed; the live stream still drives updates from here.
			});
		return () => {
			cancelled = true;
		};
	}, [signedIn]);

	const applyProgress = useCallback((event: WaypointEvent) => {
		const data = event.data as DownloadProgressData;
		const jobId = event.job_id;
		if (!jobId) {
			return;
		}
		setItems((prev) => {
			const idx = prev.findIndex((i) => i.job_id === jobId);
			if (idx === -1) {
				// A download this client didn't have a REST snapshot row for yet
				// (e.g. queued by another session) — synthesize a minimal row so
				// its progress is still visible rather than silently dropped.
				if (!data.artifact_id) {
					return prev;
				}
				const created: DownloadQueueItem = {
					id: jobId,
					artifact_id: data.artifact_id,
					job_id: jobId,
					run_id: event.run_id ?? "",
					state: data.state ?? "downloading",
					progress_percent: data.progress_percent ?? 0,
					rate_bytes_per_sec: data.rate_bytes_per_sec ?? null,
					eta_seconds: data.eta_seconds ?? null,
					retries: data.retries ?? 0,
				};
				return [...prev, created];
			}
			const next = [...prev];
			const existing = next[idx];
			next[idx] = {
				...existing,
				state: data.state ?? existing.state,
				progress_percent: data.progress_percent ?? existing.progress_percent,
				rate_bytes_per_sec: data.rate_bytes_per_sec ?? existing.rate_bytes_per_sec,
				eta_seconds: data.eta_seconds ?? existing.eta_seconds,
				retries: data.retries ?? existing.retries,
			};
			return next;
		});
	}, []);

	const applyJobState = useCallback((event: WaypointEvent) => {
		const jobId = event.job_id;
		if (!jobId) {
			return;
		}
		const data = event.data as JobStateData;
		const to = data.to;
		if (!to) {
			return;
		}
		setItems((prev) => {
			const idx = prev.findIndex((i) => i.job_id === jobId);
			if (idx === -1) {
				return prev;
			}
			const next = [...prev];
			const existing = next[idx];
			const mappedState = (["queued", "downloading", "verifying", "verified", "failed"] as const).includes(
				to as DownloadQueueItem["state"],
			)
				? (to as DownloadQueueItem["state"])
				: existing.state;
			next[idx] = { ...existing, state: mappedState };
			return next;
		});
	}, []);

	useEffect(() => {
		if (!signedIn || !token) {
			return;
		}
		const close = connectEventStream(`${API_BASE}/events`, {
			getToken: () => token,
			onEvent: (event) => {
				if (event.type === "download.progress") {
					applyProgress(event);
				} else if (event.type === "job.state") {
					applyJobState(event);
				}
			},
		});
		return close;
	}, [signedIn, token, applyProgress, applyJobState]);

	const byArtifact = new Map<string, DownloadQueueItem>();
	for (const item of items) {
		byArtifact.set(item.artifact_id, item);
	}

	return { items, byArtifact };
}
