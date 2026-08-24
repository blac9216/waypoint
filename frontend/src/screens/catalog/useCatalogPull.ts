/**
 * Connected vendor catalog-pull state (issue #687 frontend remainder against
 * PR #763's `GET/POST /catalog/pull`). Distinct from the local `catalog-index`
 * re-index (`doSync` in DownloadCatalogScreen.tsx) — this hook drives the
 * "Pull vendor catalog" action that actually contacts Broadcom.
 *
 * Follows the same conventions already proven in this codebase rather than
 * inventing new ones:
 *   - `useDepotEnrollment.ts` / `useCredentialTest.ts`: a queued action
 *     returns `202 {run_id, job_id}`, never an inline result.
 *   - `useCredentialTest.ts`: the queued job is followed via
 *     `lib/events.ts`'s `connectEventStream` on `/runs/{id}/events`, scoped
 *     to `job_id`, until a terminal `job.state` (`done`/`failed`/
 *     `auth-failed` — this is a `JobShape.Simple` job, same shape as
 *     `credential-test`), then the authoritative state is re-fetched — the
 *     job handler, not the SSE payload, is the source of truth for
 *     `catalog_pull_state`.
 *   - `useRunLog.ts`: `job.log` lines are accumulated locally (capped) for
 *     progress/terminal log visibility while the job runs.
 *
 * Readiness (`ready`/`not_ready_reason`) is read verbatim from the API on
 * every load — never re-derived client-side — so the disabled-with-reason
 * button always matches the server's own enrollment-gate evaluation
 * (`CatalogController.EvaluateReadinessAsync`), including the 409
 * `catalog_pull_not_ready` case if the gate flips between load and click.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "../../lib/auth-context";
import { API_BASE, ApiError } from "../../lib/api";
import { connectEventStream, type WaypointEvent } from "../../lib/events";
import { fetchCatalogPullStatus, pullCatalog, type CatalogPullStatus } from "./catalog";

/** `JobShape.Simple` terminal states (api-contract.md state machine; same set
 * `useCredentialTest.ts` uses for `credential-test`, another Simple job). */
const TERMINAL_JOB_STATES = new Set(["done", "failed", "auth-failed"]);

/** Capped the same way useRunLog.ts caps the log-first pane, so a
 * long-running pull can't grow the in-memory line list unbounded. */
const MAX_LOG_LINES = 260;

interface JobStateEventData {
	to?: string;
	note?: string;
}

interface JobLogEventData {
	line?: string;
	message?: string;
}

export interface CatalogPullLogLine {
	seq: number;
	message: string;
}

export interface UseCatalogPullResult {
	status: CatalogPullStatus | null;
	loading: boolean;
	loadError: string | null;
	reload: () => void;

	/** True from the moment `POST /catalog/pull` is accepted until the job
	 * reaches a terminal state — drives the button's busy label and the
	 * progress/log panel's visibility. */
	running: boolean;
	logLines: CatalogPullLogLine[];
	actionError: string | null;

	doPull: () => Promise<void>;
}

export function useCatalogPull(): UseCatalogPullResult {
	const { token } = useAuth();
	const [status, setStatus] = useState<CatalogPullStatus | null>(null);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);
	const [running, setRunning] = useState(false);
	const [logLines, setLogLines] = useState<CatalogPullLogLine[]>([]);
	const [actionError, setActionError] = useState<string | null>(null);

	const tokenRef = useRef(token);
	tokenRef.current = token;
	const mountedRef = useRef(true);
	const closeStreamRef = useRef<(() => void) | null>(null);

	useEffect(() => {
		mountedRef.current = true;
		return () => {
			mountedRef.current = false;
			closeStreamRef.current?.();
			closeStreamRef.current = null;
		};
	}, []);

	const load = useCallback(() => {
		setLoading(true);
		setLoadError(null);
		fetchCatalogPullStatus()
			.then(setStatus)
			.catch((err: unknown) => setLoadError(err instanceof ApiError ? err.message : "Could not load catalog pull status."))
			.finally(() => setLoading(false));
	}, []);

	useEffect(() => {
		load();
	}, [load]);

	const doPull = useCallback(async () => {
		if (running) {
			return;
		}
		// A prior pull's stream (if somehow still open) is closed before
		// starting a new one, mirroring useCredentialTest.ts's per-row
		// discipline so overlapping clicks never leak two subscriptions.
		closeStreamRef.current?.();
		closeStreamRef.current = null;

		setActionError(null);
		setLogLines([]);
		setRunning(true);

		let queued: { run_id: string; job_id: string };
		try {
			queued = await pullCatalog();
		} catch (err) {
			if (mountedRef.current) {
				setActionError(err instanceof ApiError ? err.message : "Could not start the catalog pull.");
				setRunning(false);
			}
			return;
		}

		if (!mountedRef.current) {
			return;
		}

		let settled = false;
		const close = connectEventStream(`${API_BASE}/runs/${queued.run_id}/events`, {
			getToken: () => tokenRef.current,
			onEvent: (event: WaypointEvent) => {
				if (settled || event.job_id !== queued.job_id) {
					return;
				}
				if (event.type === "job.log") {
					const data = event.data as JobLogEventData;
					const message = data.line ?? data.message;
					if (message && mountedRef.current) {
						setLogLines((prev) => {
							const next = [...prev, { seq: event.seq, message }];
							return next.length > MAX_LOG_LINES ? next.slice(next.length - MAX_LOG_LINES) : next;
						});
					}
					return;
				}
				if (event.type !== "job.state") {
					return;
				}
				const data = event.data as JobStateEventData;
				if (!data.to || !TERMINAL_JOB_STATES.has(data.to)) {
					return;
				}
				settled = true;
				if (data.note && mountedRef.current) {
					setLogLines((prev) => [...prev, { seq: event.seq, message: data.note! }]);
				}
				// catalog_pull_state (attempt/outcome/failure/success/item-count)
				// is server-authoritative — re-fetch rather than infer from the
				// event payload.
				fetchCatalogPullStatus()
					.then((refreshed) => {
						if (mountedRef.current) {
							setStatus(refreshed);
						}
					})
					.catch(() => {
						// Keep the last-known status; the terminal log line above
						// already told the operator the outcome.
					})
					.finally(() => {
						if (mountedRef.current) {
							setRunning(false);
						}
						closeStreamRef.current = null;
						close();
					});
			},
		});
		closeStreamRef.current = close;
	}, [running]);

	return { status, loading, loadError, reload: load, running, logLines, actionError, doPull };
}
