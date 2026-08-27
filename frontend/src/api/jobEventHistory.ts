/**
 * Typed client for `GET /runs/{id}/events/history` (issue #581, PR #684) —
 * the bounded, cursor-paged read over persisted `job_events` that
 * complements the live/replay SSE transport (`lib/events.ts`). PR #684's
 * review (comment on issue #590) deferred this frontend client here: #590
 * already owns `frontend/src/api/*` run/job query integration, and the
 * Live Jobs workspace's historical log view (for a selected terminal
 * run/job) is its first consumer.
 *
 * Wire shapes below are matched field-for-field against
 * `RunEventHistoryResponse`/`JobEventHistoryItemResponse`
 * (backend/Waypoint.Api/Contracts/RunContracts.cs), not guessed:
 *   - `items`: newest-appended-last page of events, each `{ seq, ts, type,
 *     run_id?, job_id?, data }` — the same envelope shape `lib/events.ts`'s
 *     `WaypointEvent` uses for the live stream, so a caller can render
 *     history and live events through one component.
 *   - `next_cursor`: opaque, versioned (`v1:...`) token. Present exactly
 *     when more events exist past this page; absent (null) means "reached
 *     the end of currently-persisted history" — never a silent truncation
 *     (PR #684's own doc comment on `RunEventHistoryResponse.NextCursor`).
 *     Callers must never parse or construct this string themselves — it is
 *     opaque, echoed back verbatim as the next request's `cursor` param.
 *
 * `kind`/`level` are closed server-side allow-lists (event type / job.log
 * severity); an unrecognized value is a 400 `validation_error`, not
 * silently ignored — this client passes them through as-is and lets
 * `apiGet`'s `ApiError` surface that the same way every other 400 does.
 */
import { apiGet } from "../lib/api";
import type { WaypointEvent, WaypointEventType } from "../lib/events";

export interface JobEventHistoryQuery {
	/** Narrow to one job's events within the run. Omit for the whole run. */
	jobId?: string;
	/** Comma-separated allow-list of event types, e.g. `"job.log,job.state"`. */
	kind?: string;
	/** Comma-separated allow-list of `job.log` severities. */
	level?: string;
	/** Opaque cursor from a previous page's `next_cursor`. Omit for the first page. */
	cursor?: string;
	/** Page size; server applies its own default/max when omitted. */
	limit?: number;
}

export interface JobEventHistoryPage {
	items: WaypointEvent[];
	/** Opaque; pass verbatim as the next call's `cursor`. Null at the end of history. */
	nextCursor: string | null;
}

/** `JobEventHistoryItemResponse` on the wire — identical envelope shape to
 * the live `WaypointEvent`, so no separate view model is needed. `type` is
 * validated against the same closed set the SSE layer already trusts. */
interface JobEventHistoryItemWire {
	seq: number;
	ts: string;
	type: string;
	run_id?: string | null;
	job_id?: string | null;
	data: unknown;
}

interface RunEventHistoryResponseWire {
	items: JobEventHistoryItemWire[];
	next_cursor: string | null;
}

const KNOWN_EVENT_TYPES: ReadonlySet<string> = new Set<WaypointEventType>([
	"job.state",
	"job.log",
	"run.progress",
	"queue.state",
	"download.progress",
	"system.notice",
]);

/** Widens an unrecognized `type` value to `"system.notice"` rather than
 * dropping the row — a history page must render every event it was billed
 * for (AC "never a silent truncation"); an unknown type is still shown, just
 * generically, the same fail-open posture the generic detail renderer takes
 * for unknown job types (ADR-0019 decision 2). */
function asEventType(value: string): WaypointEventType {
	return KNOWN_EVENT_TYPES.has(value) ? (value as WaypointEventType) : "system.notice";
}

function fromWire(item: JobEventHistoryItemWire): WaypointEvent {
	return {
		seq: item.seq,
		ts: item.ts,
		type: asEventType(item.type),
		run_id: item.run_id ?? undefined,
		job_id: item.job_id ?? undefined,
		data: item.data,
	};
}

function buildQueryString(query: JobEventHistoryQuery): string {
	const params = new URLSearchParams();
	if (query.jobId) {
		params.set("job_id", query.jobId);
	}
	if (query.kind) {
		params.set("kind", query.kind);
	}
	if (query.level) {
		params.set("level", query.level);
	}
	if (query.cursor) {
		params.set("cursor", query.cursor);
	}
	if (query.limit !== undefined) {
		params.set("limit", String(query.limit));
	}
	const qs = params.toString();
	return qs ? `?${qs}` : "";
}

/**
 * Reads one page of persisted history for `runId`. Viewer+ (same floor as
 * every other run read — ADR-0019 decision 6: observing operational history
 * is not a domain action). Throws `ApiError` with `status === 404` for a run
 * that does not exist, and `status === 400`/`code === "validation_error"`
 * for a malformed `jobId`, unrecognized `kind`/`level`, or garbage `cursor`
 * — never a 500 on client-abusable input, matching the backend contract.
 */
export async function fetchJobEventHistory(runId: string, query: JobEventHistoryQuery = {}): Promise<JobEventHistoryPage> {
	const wire = await apiGet<RunEventHistoryResponseWire>(`/runs/${runId}/events/history${buildQueryString(query)}`);
	return {
		items: wire.items.map(fromWire),
		nextCursor: wire.next_cursor,
	};
}

export interface AllJobEventHistoryResult {
	events: WaypointEvent[];
	/** Opaque; pass verbatim as the next `fetchAllJobEventHistory` call's `resumeCursor` to fetch the remainder. Null once history is exhausted. */
	nextCursor: string | null;
	/**
	 * True when this call stopped short of the end of history because
	 * `stopAfter` was reached, NOT because the server ran out of pages.
	 * Callers (issue #721) must treat `truncated: true` as an honest
	 * "more exists" signal — never render it as a complete history. Always
	 * `false` when `nextCursor` is null.
	 */
	truncated: boolean;
}

/**
 * Pages through history for `runId` starting at `resumeCursor` (or the
 * beginning, if omitted), stopping after collecting `stopAfter` events OR
 * reaching the end of history, whichever comes first. Used by the
 * workspace's historical log view for a selected terminal job.
 *
 * `stopAfter` bounds a single call's browser-memory/request cost for scale
 * (epic #726 §7: the design must support tens of thousands of job events
 * without loading all rows into browser memory) — it is a batch-size
 * control, not an end-of-history signal. Unlike the pre-#721 behavior, a
 * bound that stops mid-history is reported via `truncated: true` +
 * `nextCursor`, so callers can offer an honest "load more" continuation
 * instead of silently presenting a prefix as the whole history (issue
 * #721).
 */
export async function fetchAllJobEventHistory(
	runId: string,
	query: Omit<JobEventHistoryQuery, "cursor"> = {},
	stopAfter = 1000,
	resumeCursor?: string,
): Promise<AllJobEventHistoryResult> {
	const events: WaypointEvent[] = [];
	let cursor = resumeCursor;
	for (;;) {
		const page = await fetchJobEventHistory(runId, { ...query, cursor });
		events.push(...page.items);
		if (!page.nextCursor) {
			return { events, nextCursor: null, truncated: false };
		}
		if (events.length >= stopAfter) {
			return { events, nextCursor: page.nextCursor, truncated: true };
		}
		cursor = page.nextCursor;
	}
}
