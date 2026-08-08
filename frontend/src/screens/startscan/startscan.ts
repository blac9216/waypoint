/**
 * Start-a-Scan data layer (docs/ui/prototype/README.md screen 3; issue #284,
 * third sub-issue of the #26 split), against:
 *
 *   GET  /targets/{id}/inventory   — #21 backend (DiscoveryController)
 *   POST /runs                     — #278/#281 backend (RunsController)
 *
 * Two request shapes matter here:
 *
 * - `ScanScope` mirrors `Waypoint.Core.Jobs.ScanScope` exactly (`site_id` +
 *   optional `target_ids`) — it is serialized to a JSON *string* and sent as
 *   `RunCreateRequest.Scope`, not a nested object, because the backend scope
 *   field is `string Scope` (raw JSON the server parses itself, see
 *   RunContracts.cs). Getting this wrong (sending an object) fails silently
 *   as a 400 `validation_error` with "scope is not valid JSON".
 * - `EphemeralCredentialInput` mirrors `EphemeralCredentialRequest` (PR #281,
 *   ADR-0011 personal tier): `{kind:"personal", username, secret}`, mutually
 *   exclusive with `credential_id`, Operator+ only. The secret is never
 *   retained anywhere in this module — callers clear it from component state
 *   immediately after the POST resolves, the same convention
 *   CredentialsTab.tsx uses for its write-only secret field.
 */

import { apiGet, apiPost } from "../../lib/api";

export interface InventoryItem {
	id: string;
	type: "cluster" | "host" | "vm" | string;
	moref: string;
	name: string;
	build: string | null;
	maintenance_mode: boolean | null;
	last_seen_at: string;
	removed: boolean;
	children: InventoryItem[];
}

export interface TargetInventory {
	target_id: string;
	discovery_status: string;
	last_refreshed: string | null;
	stale: boolean;
	items: InventoryItem[];
}

export function fetchTargetInventory(targetId: string): Promise<TargetInventory> {
	return apiGet<TargetInventory>(`/targets/${targetId}/inventory`);
}

export interface ScanScope {
	site_id: string;
	/** Omitted (not empty array) for "every target under the site" — matches
	 * the backend's `TargetIds is null || Count == 0` full-site-scan check. */
	target_ids?: string[];
}

export interface EphemeralCredentialInput {
	kind: "personal";
	username: string;
	secret: string;
}

export interface CreateScanRunInput {
	scope: ScanScope;
	/** Stored service credential id — mutually exclusive with `credential`. */
	credential_id?: string;
	/** Ad hoc "my credentials" — mutually exclusive with `credential_id`,
	 * Operator+ (server-enforced; see RunsController.ValidateEphemeralCredentialRequest). */
	credential?: EphemeralCredentialInput;
}

export interface RunCreatedResponse {
	run_id: string;
}

export function createScanRun(input: CreateScanRunInput): Promise<RunCreatedResponse> {
	return apiPost<RunCreatedResponse>("/runs", {
		run_type: "scan",
		scope: JSON.stringify(input.scope),
		credential_id: input.credential_id || undefined,
		credential: input.credential,
	});
}

/** Flattens the inventory tree into target-selectable leaf/parent ids for the
 * tri-state checkbox tree, in the same parent-before-child order the tree
 * arrives in. */
export function flattenInventory(items: InventoryItem[]): InventoryItem[] {
	const out: InventoryItem[] = [];
	const walk = (nodes: InventoryItem[]) => {
		for (const node of nodes) {
			out.push(node);
			if (node.children.length > 0) {
				walk(node.children);
			}
		}
	};
	walk(items);
	return out;
}
