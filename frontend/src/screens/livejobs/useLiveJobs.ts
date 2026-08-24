/**
 * Live Jobs subscription hook (issue #590). Seeds a `LiveJobsSnapshot` from
 * `GET /runs` + per-active-run `GET /runs/{id}/jobs` (REST reconciliation —
 * "Refresh/reconnect reconciles with authoritative REST state and does not
 * duplicate or lose jobs", ADR-0019/#590 AC3), then opens the GLOBAL SSE
 * stream (`/api/v1/events` — the same feed `JobLogDrawer` already consumes,
 * not the per-run `/runs/{id}/events` stream `useLiveRun.ts` uses) and folds
 * every event through `applyEvent` (livejobs.ts). Global, because this
 * workspace shows every concurrently active run, not one.
 *
 * Re-seeds on reconnect: `connectEventStream`'s `onStateChange` fires
 * `"open"` both on first connect and every successful reconnect. A
 * reconnect can only replay events the server buffered since
 * `Last-Event-ID` (docs/api-contract.md's replay guarantee is bounded, not
 * infinite retention) — a run or job that changed state entirely during a
 * long disconnect would otherwise never reach this snapshot. Re-fetching
 * `GET /runs` on every reconnect (not just mount) is what "reconciles with
 * authoritative REST state" means for a client that can be offline for an
 * unbounded time, and de-duplicates against the in-flight event queue the
 * same way the initial seed does.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { API_BASE } from "../../lib/api";
import { useAuth } from "../../lib/auth-context";
import { connectEventStream, type ConnectionState, type WaypointEvent } from "../../lib/events";
import { fetchRunJobs, fetchRunList, isTerminalRunState as isTerminalRunStateWire, type RunListItem } from "../results/results";
import { applyEvent, isActiveGroup, mapRunJobItem, mapRunListItem, type LiveJobsSnapshot } from "./livejobs";

export interface UseLiveJobsResult {
	snapshot: LiveJobsSnapshot | null;
	loading: boolean;
	loadError: string | null;
	connectionState: ConnectionState;
}

/** How many most-recent runs to seed jobs for. `GET /runs` has no active-only
 * filter yet (ADR-0019 "planned" section), so the seed fetches the newest
 * page and narrows to non-terminal groups client-side; a bound here keeps
 * the per-run `GET /runs/{id}/jobs` fan-out proportional to a page, not
 * unbounded, per #590's "large active sets require bounded rendering" risk
 * note. */
const RUN_PAGE_SIZE = 50;

async function seedSnapshot(): Promise<LiveJobsSnapshot> {
	const { items } = await fetchRunList(RUN_PAGE_SIZE, 0);
	const candidates: RunListItem[] = items.filter((r) => !isTerminalRunStateWire(r.state));
	const jobsByRun = await Promise.all(
		candidates.map(async (r) => {
			try {
				return await fetchRunJobs(r.id);
			} catch {
				// One run's job fetch failing must not blank the whole workspace —
				// it simply renders with zero jobs until the next reconcile.
				return [];
			}
		}),
	);
	const groups = candidates
		.map((r, i) => mapRunListItem(r, jobsByRun[i].map((j) => mapRunJobItem(r.id, j))))
		.filter(isActiveGroup);
	return { runs: groups, lastSeq: 0 };
}

export function useLiveJobs(): UseLiveJobsResult {
	const { token, status } = useAuth();
	const [snapshot, setSnapshot] = useState<LiveJobsSnapshot | null>(null);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);
	const [connectionState, setConnectionState] = useState<ConnectionState>("connecting");

	// Events can arrive before a (re)seed resolves; queue and drain rather
	// than drop them — same pattern as useLiveRun.ts.
	const pendingEvents = useRef<WaypointEvent[]>([]);
	const seeded = useRef(false);

	const reseed = useCallback(async () => {
		seeded.current = false;
		try {
			const seed = await seedSnapshot();
			let next = seed;
			for (const event of pendingEvents.current) {
				next = applyEvent(next, event);
			}
			pendingEvents.current = [];
			seeded.current = true;
			setSnapshot(next);
			setLoadError(null);
		} catch (err) {
			setLoadError(err instanceof Error ? err.message : "Could not load active runs.");
		} finally {
			setLoading(false);
		}
	}, []);

	useEffect(() => {
		if (status !== "signed-in") {
			setLoading(false);
			return;
		}
		setLoading(true);
		void reseed();
	}, [status, reseed]);

	useEffect(() => {
		if (status !== "signed-in" || !token) {
			return;
		}
		const close = connectEventStream(`${API_BASE}/events`, {
			getToken: () => token,
			onStateChange: (state) => {
				setConnectionState((prev) => {
					// A transition INTO "open" from anything but the initial
					// "connecting" means this is a reconnect, not first connect —
					// re-seed from REST so state that changed while disconnected
					// is reconciled (see module doc comment).
					if (state === "open" && prev !== "connecting") {
						void reseed();
					}
					return state;
				});
			},
			onEvent: (event) => {
				if (!seeded.current) {
					pendingEvents.current.push(event);
					return;
				}
				setSnapshot((prev) => (prev ? applyEvent(prev, event) : prev));
			},
		});
		return close;
	}, [status, token, reseed]);

	return { snapshot, loading, loadError, connectionState };
}
