/**
 * Live Run subscription hook. Seeds a `RunSnapshot` from the REST endpoints
 * (`GET /runs/{id}`, `GET /runs/{id}/jobs`) so the board paints immediately,
 * then opens the per-run SSE stream (`/runs/{id}/events`) and folds every
 * event through `applyEvent` (liverun.ts) — never a poll, per
 * docs/api-contract.md's "Event streams (SSE)" section.
 *
 * Last-Event-ID replay (AC2): `connectEventStream` (lib/events.ts) already
 * implements the reconnect-with-Last-Event-ID protocol; this hook's job is
 * only to make sure replayed events land through the exact same
 * `applyEvent` reducer live events do, so "reload and let the stream replay"
 * converges on the identical board `applyEvent` would have produced by
 * following the events live. That equivalence is what LiveRunScreen.test.tsx
 * proves with a mocked EventSource.
 */
import { useEffect, useRef, useState } from "react";
import { useAuth } from "../../lib/auth";
import { API_BASE } from "../../lib/api";
import { connectEventStream, type ConnectionState, type WaypointEvent } from "../../lib/events";
import { applyEvent, fetchRun, fetchRunJobs, type RunSnapshot } from "./liverun";

export interface UseLiveRunResult {
	snapshot: RunSnapshot | null;
	loading: boolean;
	loadError: string | null;
	connectionState: ConnectionState;
}

export function useLiveRun(runId: string | undefined): UseLiveRunResult {
	const { token, status } = useAuth();
	const [snapshot, setSnapshot] = useState<RunSnapshot | null>(null);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);
	const [connectionState, setConnectionState] = useState<ConnectionState>("connecting");

	// Events can arrive before the REST seed resolves (or before this effect
	// re-runs after a runId change); queue them and drain once the seed lands
	// rather than dropping the ones that raced ahead of it.
	const pendingEvents = useRef<WaypointEvent[]>([]);
	const seeded = useRef(false);

	useEffect(() => {
		setSnapshot(null);
		setLoadError(null);
		seeded.current = false;
		pendingEvents.current = [];
		if (!runId || status !== "signed-in") {
			setLoading(false);
			return;
		}
		setLoading(true);

		let cancelled = false;
		Promise.all([fetchRun(runId), fetchRunJobs(runId)])
			.then(([header, jobs]) => {
				if (cancelled) {
					return;
				}
				let next: RunSnapshot = { header, jobs, lastSeq: 0 };
				for (const event of pendingEvents.current) {
					next = applyEvent(next, event);
				}
				pendingEvents.current = [];
				seeded.current = true;
				setSnapshot(next);
			})
			.catch((err: unknown) => {
				if (!cancelled) {
					setLoadError(err instanceof Error ? err.message : "Could not load the run.");
				}
			})
			.finally(() => {
				if (!cancelled) {
					setLoading(false);
				}
			});

		return () => {
			cancelled = true;
		};
	}, [runId, status]);

	useEffect(() => {
		if (!runId || status !== "signed-in" || !token) {
			return;
		}
		const close = connectEventStream(`${API_BASE}/runs/${runId}/events`, {
			getToken: () => token,
			onStateChange: setConnectionState,
			onEvent: (event) => {
				if (!seeded.current) {
					pendingEvents.current.push(event);
					return;
				}
				setSnapshot((prev) => (prev ? applyEvent(prev, event) : prev));
			},
		});
		return close;
	}, [runId, status, token]);

	return { snapshot, loading, loadError, connectionState };
}
