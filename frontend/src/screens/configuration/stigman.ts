/**
 * Config → STIG Manager data layer (issue #312, epic #25/#13), against the
 * merged backend (PR #314, `StigManagerController`/`StigManagerContracts.cs`):
 *
 *   GET/PUT /stigman                — Viewer+ read, Admin write (global connection)
 *   GET/PUT /sites/{id}/stigman     — Viewer+ read, Admin write (resolved: site
 *                                      override when present, else global — `source`
 *                                      tells the caller which layer answered)
 *   POST    /stigman/test[?site_id=] — Admin, side-effect-free reachability/auth check
 *
 * `credential_id` is a reference into the existing typed credential store (a
 * `token`-type credential, #249/#247) — there is no secret field anywhere on
 * this wire, matching ADR-0011's "no new secret path" and the same
 * write-only-secret shape `credentials.ts` already uses. The picker only
 * ever collects an id/name pair (`CredentialOption`, reused from `sites.ts`),
 * never secret material.
 */

import { apiGet, apiPost, apiPut } from "../../lib/api";

export interface StigManagerConnection {
	endpoint: string;
	authority: string;
	collection: string;
	client_id: string;
	scope: string;
	credential_id: string | null;
	created_at: string;
	updated_at: string;
}

export type StigManagerSource = "site_override" | "global";

export interface ResolvedStigManagerConnection {
	endpoint: string;
	authority: string;
	collection: string;
	client_id: string;
	scope: string;
	credential_id: string | null;
	source: StigManagerSource;
}

export interface StigManagerWriteInput {
	endpoint: string;
	authority: string;
	collection: string;
	client_id: string;
	/** Blank falls back to the backend's default scope (`ValidateOrThrow` only
	 * requires the other four fields; `PutGlobal`/`PutForSite` substitute
	 * `"openid stig-manager:stig:read"` server-side when this is blank). */
	scope: string;
	credential_id: string | null;
}

function toBody(input: StigManagerWriteInput) {
	return {
		endpoint: input.endpoint,
		authority: input.authority,
		collection: input.collection,
		client_id: input.client_id,
		scope: input.scope || undefined,
		credential_id: input.credential_id || undefined,
	};
}

export function fetchGlobalStigManager(): Promise<StigManagerConnection> {
	return apiGet<StigManagerConnection>("/stigman");
}

export function putGlobalStigManager(input: StigManagerWriteInput): Promise<StigManagerConnection> {
	return apiPut<StigManagerConnection>("/stigman", toBody(input));
}

export function fetchSiteStigManager(siteId: string): Promise<ResolvedStigManagerConnection> {
	return apiGet<ResolvedStigManagerConnection>(`/sites/${siteId}/stigman`);
}

export function putSiteStigManager(siteId: string, input: StigManagerWriteInput): Promise<ResolvedStigManagerConnection> {
	return apiPut<ResolvedStigManagerConnection>(`/sites/${siteId}/stigman`, toBody(input));
}

export const STIG_MANAGER_TEST_OUTCOMES = [
	"ok",
	"unreachable",
	"auth_failed",
	"not_configured",
	"master_key_unavailable",
] as const;
export type StigManagerTestOutcome = (typeof STIG_MANAGER_TEST_OUTCOMES)[number];

export interface StigManagerTestResult {
	reachable: boolean;
	auth_ok: boolean;
	outcome: StigManagerTestOutcome | string;
	message: string;
	api_version: string | null;
}

/** `siteId` omitted tests the global connection; supplied, it tests that
 * site's resolved connection (override when present, else global) — the
 * same `?site_id=` the backend's `Test` action reads. */
export function testStigManager(siteId?: string): Promise<StigManagerTestResult> {
	return apiPost<StigManagerTestResult>(siteId ? `/stigman/test?site_id=${siteId}` : "/stigman/test");
}

export function formatStigManagerOutcome(outcome: string): string {
	switch (outcome) {
		case "ok":
			return "reachable, authenticated";
		case "unreachable":
			return "unreachable";
		case "auth_failed":
			return "authentication failed";
		case "not_configured":
			return "not configured";
		case "master_key_unavailable":
			return "master key unavailable";
		default:
			return outcome;
	}
}
