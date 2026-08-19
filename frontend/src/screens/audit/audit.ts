/**
 * Audit data layer (issue #531, epic #14), against the `/audit` backend
 * (PR #529/#512, `AuditController`, `docs/api-contract.md` `/audit`:
 * "Cyber+; decrypt events, config versions, run initiations,
 * imports/updates.").
 *
 * Wire shape matched field-for-field against `AuditEntryResponse`
 * (AuditContracts.cs) — `credential_id`/`job_id`/`run_id` are all nullable
 * GUID strings, `detail` is a JSON string rendered as-is (the backend never
 * parses it back out either; it's opaque per-event context), `occurred_at`
 * is `DateTimeOffset` "O" format.
 *
 * Paging goes through `apiGetPaged` (`lib/api.ts`) rather than `apiGet` —
 * this is the first list screen with a real page-through UI, so it needs
 * the actual `X-Total-Count` rather than the `items.length` placeholder
 * `results.ts`'s `fetchRunList` uses.
 */
import { apiGetPaged, type PagedResult } from "../../lib/api";

export interface AuditEntry {
	id: string;
	event_type: string;
	actor: string;
	credential_id: string | null;
	job_id: string | null;
	run_id: string | null;
	detail: string;
	occurred_at: string;
}

export interface AuditFilter {
	/** Maps to the `?kind=` query param — `AuditController.List`'s `eventType`. */
	kind?: string;
	actor?: string;
	/** ISO-8601 timestamps; the controller rejects anything
	 * `DateTimeOffset.TryParse` can't parse with a 400 validation error. */
	from?: string;
	to?: string;
}

export interface AuditPage {
	limit: number;
	offset: number;
}

function buildQuery(filter: AuditFilter, page?: AuditPage, format?: "csv"): string {
	const params = new URLSearchParams();
	if (filter.kind) {
		params.set("kind", filter.kind);
	}
	if (filter.actor) {
		params.set("actor", filter.actor);
	}
	if (filter.from) {
		params.set("from", filter.from);
	}
	if (filter.to) {
		params.set("to", filter.to);
	}
	if (page) {
		params.set("limit", String(page.limit));
		params.set("offset", String(page.offset));
	}
	if (format) {
		params.set("format", format);
	}
	const qs = params.toString();
	return qs ? `?${qs}` : "";
}

/** `GET /audit` — Cyber+, filtered + paged. */
export function fetchAuditLog(filter: AuditFilter, page: AuditPage): Promise<PagedResult<AuditEntry>> {
	return apiGetPaged<AuditEntry>(`/audit${buildQuery(filter, page)}`);
}

/** Builds the `?format=csv` URL for the exact same filter the list is
 * currently showing (no page params — the controller ignores paging and
 * returns the whole filtered set for CSV, `AuditController.List`'s
 * `asCsv` branch). Relative to `API_BASE`; callers that need an absolute
 * fetch (to attach the bearer token by hand, matching `useCklExport.ts`'s
 * pattern for the other binary-download surface in this codebase) prefix
 * it with `API_BASE` themselves. */
export function auditCsvQuery(filter: AuditFilter): string {
	return buildQuery(filter, undefined, "csv");
}
