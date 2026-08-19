/**
 * Users & Roles data layer (issue #32, epic #14), against the `/users`
 * backend (PR #529, `UsersController`/`Waypoint.Core.Users`).
 *
 * Role is an **IdP-derived read-only mirror** (ADR-0004: Keycloak group
 * membership is authoritative), refreshed on every sign-in — this client
 * exposes no way to write it. `updateUser` only ever sends `site_scope`,
 * mirroring the backend's `UserUpdateRequest` (which has no `role` field at
 * all to send). An operator who wants to change a user's role edits Keycloak
 * group membership; the UI states that rather than offering a control that
 * would silently drift from the IdP's own answer.
 */

import { apiGet, apiPut } from "../../lib/api";
import type { Role } from "../../lib/roles";

export interface WaypointUser {
	id: string;
	oidc_sub: string;
	username: string;
	role: Role | string;
	/** JSON array (site ids), stored/transmitted as a string — mirrors
	 * `sites.ts`'s treatment of other JSON-array-as-string fields on this
	 * wire; there is no dedicated site-scope endpoint to resolve names from
	 * here, so the tab renders the raw scope. */
	site_scope: string;
	auth_method: string;
	last_seen_at: string;
	created_at: string;
	updated_at: string;
}

export function fetchUsers(): Promise<WaypointUser[]> {
	return apiGet<WaypointUser[]>("/users");
}

export function updateUserSiteScope(id: string, siteScopeJson: string): Promise<WaypointUser> {
	return apiPut<WaypointUser>(`/users/${id}`, { site_scope: siteScopeJson });
}

/** domain-model.md "Roles" table, restated one line per role for the tab's
 * capability column — kept as a plain lookup (not fetched) since the table
 * is fixed product doctrine, not per-instance data. */
export const ROLE_CAPABILITIES: Record<string, string> = {
	Viewer: "Read-only: dashboards, runs, results.",
	Cyber: "Viewer + initiate scans with service credentials + export results + full audit history.",
	Operator: "Cyber + ad hoc scans with own credentials + download/catalog/content-library management.",
	Admin: "Everything: sites, targets, shared credentials, users/roles, STIG config, remediation, updates, transfer.",
};

export function describeRole(role: string): string {
	return ROLE_CAPABILITIES[role] ?? "Unrecognized role.";
}

/** Best-effort render of the stored JSON-array site scope. Falls back to the
 * raw string for a malformed value rather than throwing — this is a display
 * concern, not a validation one (the server is the source of truth for
 * shape). */
export function formatSiteScope(siteScopeJson: string): string {
	try {
		const parsed: unknown = JSON.parse(siteScopeJson);
		if (Array.isArray(parsed)) {
			return parsed.length === 0 ? "All sites" : parsed.join(", ");
		}
	} catch {
		// fall through
	}
	return siteScopeJson || "All sites";
}
