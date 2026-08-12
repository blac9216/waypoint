/**
 * Local `job.log` accumulator for the log-first layout's full-height pane.
 * Deliberately separate from `useLiveRun`/`applyEvent` — that reducer folds
 * `job.log` into a single `note` field per job (the queues/board layouts'
 * "latest note" column), not a scrollable history, and is intentionally
 * left untouched by this issue. This hook opens its own subscription to the
 * same per-run SSE stream (`connectEventStream`, same primitive `useLiveRun`
 * and the global JobLogDrawer both use) purely to keep a capped line
 * history; it never mutates the `RunSnapshot`.
 */
import { useEffect, useState } from "react";
import { API_BASE } from "../../lib/api";
import { useAuth } from "../../lib/auth-context";
import { connectEventStream, type WaypointEvent } from "../../lib/events";

/** Prototype "Live run simulation": "Log lines are appended on every state
 * transition and capped at 260 lines." */
export const LOG_FIRST_MAX_LINES = 260;

export interface LogFirstLine {
	seq: number;
	ts: string;
	target?: string;
	message: string;
}

interface JobLogEventData {
	target?: string;
	line?: string;
	message?: string;
}

export function useRunLog(runId: string | undefined, active: boolean): LogFirstLine[] {
	const { token, status } = useAuth();
	const [lines, setLines] = useState<LogFirstLine[]>([]);

	useEffect(() => {
		setLines([]);
		if (!active || !runId || status !== "signed-in" || !token) {
			return;
		}
		const close = connectEventStream(`${API_BASE}/runs/${runId}/events`, {
			getToken: () => token,
			onEvent: (event: WaypointEvent) => {
				if (event.type !== "job.log") {
					return;
				}
				const data = event.data as JobLogEventData;
				const message = data.line ?? data.message;
				if (!message) {
					return;
				}
				setLines((prev) => {
					const next = [...prev, { seq: event.seq, ts: event.ts, target: data.target, message }];
					return next.length > LOG_FIRST_MAX_LINES ? next.slice(next.length - LOG_FIRST_MAX_LINES) : next;
				});
			},
		});
		return close;
	}, [runId, active, status, token]);

	return lines;
}
