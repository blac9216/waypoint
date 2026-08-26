/**
 * Start-a-Scan data layer (docs/ui/prototype/README.md screen 3; issue #284,
 * third sub-issue of the #26 split; issue #733 rewired this module from the
 * legacy `inventory_items` tree onto the stable `components` model), against:
 *
 *   GET  /targets/{id}/components  — #732 backend (ComponentsController)
 *   POST /runs                     — #278/#281 backend (RunsController)
 *
 * Two request shapes matter here:
 *
 * - `ScanScope` mirrors `Waypoint.Core.Jobs.ScanScope` exactly (`site_id` +
 *   optional `target_ids`/`profile_id`, plus issue #733's additive
 *   `target_scope`) — it is serialized to a JSON *string* and sent as
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
 *
 * Issue #733 (epic #726 Wave 2, ADR-0023, backend PR #854): the checkbox tree
 * now walks the stable `components` model (parent + catalog component key +
 * authoritative vendor identity) instead of the legacy `inventory_items`
 * table `/targets/{id}/inventory` served — that endpoint has no relationship
 * to the `component_ids` `scope.target_scope.component_ids` expects, so a
 * tree built from it could never resolve to a real component on the wire.
 * `/targets/{id}/components` (ComponentsController, issue #732) is the
 * correct source: a flat `ComponentResponse[]` with `parent_component_id`
 * pointers this module reassembles into the same cluster/host/vm-shaped tree
 * the wizard already rendered.
 */

import { apiGet, apiPost, type CredentialBindingGap, type ScanPlanItem, type ScanPlanSkip } from "../../lib/api";

/** `Waypoint.Core.Components.ComponentLifecycleStates` on the wire. Only `active` components are selectable/expandable into an `all`-mode scan; `absent`/`retired` ones are rendered but disabled (issue #733 AC: stale/removed selections fail with guidance rather than silently widening or silently vanishing). */
export type ComponentLifecycle = "active" | "absent" | "retired";

/** `Waypoint.Api.Contracts.ComponentResponse` — one row of `GET /targets/{id}/components`. */
export interface ComponentNode {
	id: string;
	parent_target_id: string;
	parent_component_id: string | null;
	catalog_component_id: string | null;
	catalog_component_key: string;
	vendor_identity: string | null;
	display_name: string;
	lifecycle: ComponentLifecycle | string;
	fact_conflict: boolean;
	retired_at: string | null;
}

/** One node of the tree `buildComponentTree` assembles from the flat `GET /targets/{id}/components` response — `ComponentNode` plus its already-nested `children`, the shape `InventoryNode`/the tri-state resolver both walk. */
export interface ComponentTreeNode extends ComponentNode {
	children: ComponentTreeNode[];
}

export function fetchTargetComponents(targetId: string): Promise<ComponentNode[]> {
	return apiGet<ComponentNode[]>(`/targets/${targetId}/components?includeRetired=true`);
}

/** Reassembles the flat `GET /targets/{id}/components` list into a tree via `parent_component_id`, preserving arrival order at each level (deterministic — see `useScanWizard`'s tri-state tests). Orphans (a `parent_component_id` naming a row not present in `items`, which should not happen but must never crash the wizard) are hoisted to the root rather than dropped, so a rendering gap is visible instead of a silently missing component. */
export function buildComponentTree(items: ComponentNode[]): ComponentTreeNode[] {
	const byId = new Map<string, ComponentTreeNode>();
	for (const item of items) {
		byId.set(item.id, { ...item, children: [] });
	}
	const roots: ComponentTreeNode[] = [];
	for (const item of items) {
		const node = byId.get(item.id);
		if (!node) {
			continue;
		}
		const parent = item.parent_component_id ? byId.get(item.parent_component_id) : undefined;
		if (parent) {
			parent.children.push(node);
		} else {
			roots.push(node);
		}
	}
	return roots;
}

/** Flattens a component tree into a single ordered list (parent before children, same order tests/selection-counting rely on). */
export function flattenComponentTree(nodes: ComponentTreeNode[]): ComponentTreeNode[] {
	const out: ComponentTreeNode[] = [];
	const walk = (list: ComponentTreeNode[]) => {
		for (const node of list) {
			out.push(node);
			if (node.children.length > 0) {
				walk(node.children);
			}
		}
	};
	walk(nodes);
	return out;
}

/** A component is eligible for `all`-mode inclusion / default-checked selection only while `active` — an `absent`/`retired` component is rendered (so the operator sees it and understands why it is excluded) but never auto-selected and never counted toward "select all". */
export function isSelectableComponent(node: ComponentNode): boolean {
	return node.lifecycle === "active";
}

/**
 * Issue #733: resolves one target's tri-state selection into the wire's
 * `target_scope` shape (docs/api-contract.md "Interim additive
 * `scope.target_scope`"). `allComponentIds` is every *selectable* (active)
 * leaf-or-parent id known for the target; `selectedIds` is exactly what the
 * operator left checked.
 *
 * - No components known at all (empty inventory / not yet discovered) →
 *   `null`: this target contributes nothing to `target_scope` and falls back
 *   to the legacy whole-target `target_ids` behavior, matching today's
 *   "no cached inventory — scanning the whole target" fallback note.
 * - Every selectable component checked → `{ mode: "all" }` (expands against
 *   refreshed inventory at scan time, per ADR-0023 — never freezes today's
 *   set as an explicit list when the operator meant "everything").
 * - Anything else, including a deliberately empty selection → `{ mode:
 *   "explicit", component_ids: [...] }`, never widened. An empty
 *   `selectedIds` yields `component_ids: []`, which the backend honors as an
 *   intentional empty plan (issue #733 AC) — this function does not special
 *   case it away.
 */
export function resolveTargetScope(allComponentIds: string[], selectedIds: ReadonlySet<string>): { mode: "all" } | { mode: "explicit"; component_ids: string[] } | null {
	if (allComponentIds.length === 0) {
		return null;
	}
	const allSelected = allComponentIds.length > 0 && allComponentIds.every((id) => selectedIds.has(id));
	if (allSelected) {
		return { mode: "all" };
	}
	return { mode: "explicit", component_ids: allComponentIds.filter((id) => selectedIds.has(id)) };
}

/** `Waypoint.Core.Jobs.TargetScopeRequest` on the wire — one target's resolved tri-state scope, keyed by `target_id` when the wizard sends more than one target's `target_scope` (the backend's `TargetScopeRequest` is itself target-agnostic within one site-wide request, so the wizard folds every selected target's resolution into one request per docs/api-contract.md's `{ mode, target_ids?, component_ids? }` shape: `all`-mode carries the contributing `target_ids`, `explicit`-mode carries the resolved `component_ids` across every target). */
export type TargetScopeInput = { mode: "all"; target_ids: string[] } | { mode: "explicit"; component_ids: string[] };

export interface ScanScope {
	site_id: string;
	/** Omitted (not empty array) for "every target under the site" — matches
	 * the backend's `TargetIds is null || Count == 0` full-site-scan check. */
	target_ids?: string[];
	/** Issue #639: which pulled compliance-content profile (`GET /profiles`
	 * `id`) this scan executes against — required by the backend
	 * (`RunCreationService.CreateScanRunAsync` 400s without it, 404s on an
	 * unknown id). */
	profile_id: string;
	/** Issue #733 (epic #726 Wave 2, ADR-0023): the operator's resolved
	 * component-tree selection, additive to `target_ids`/`profile_id` in this
	 * interim slice (docs/api-contract.md "Interim additive
	 * `scope.target_scope`"). Omitted when no target under this scope has any
	 * known components yet (nothing to resolve). */
	target_scope?: TargetScopeInput;
}

/** `Waypoint.Core.Components.ScopeOmission` on the wire — one machine-readable reason a requested component/target did not make it into the resolved scope (issue #733 AC: "actionable refresh guidance rather than silently widening"). */
export interface ScopeOmission {
	component_id?: string;
	target_id?: string;
	reason: string;
	detail: string;
}

/** `GET /profiles` — issue #639's Start-a-Scan profile picker only ever needs
 * enough to label the option and identify it; the fuller `ProfileResponse`
 * (version/commit/state/updated_at) is Benchmarks' concern, not this wizard's. */
export interface ProfileOption {
	id: string;
	profile_key: string;
	name: string;
	version: string | null;
}

export function fetchProfileOptions(): Promise<ProfileOption[]> {
	return apiGet<ProfileOption[]>("/profiles");
}

export interface EphemeralCredentialInput {
	kind: "personal";
	username: string;
	secret: string;
}

/**
 * Issue #587 (epic #582): one saved-credential override for a specific
 * (target, purpose) pair — mirrors `RunCredentialOverrideRequest` (PR #663).
 */
export interface CredentialOverrideInput {
	target_id: string;
	purpose: string;
	credential_id: string;
}

/**
 * Issue #587 (epic #582): one inline ad hoc credential for a specific
 * (target, purpose) pair — mirrors `RunAdHocCredentialRequest` (PR #666).
 * Never logged, never echoed; the wizard clears the `username`/`secret`
 * fields from its own component state immediately after a successful POST.
 */
export interface AdHocCredentialInput {
	target_id: string;
	purpose: string;
	username: string;
	secret: string;
}

export interface CreateScanRunInput {
	scope: ScanScope;
	/** Stored service credential id — mutually exclusive with `credential`.
	 * Legacy run-level tier (issue #587 retired it from the wizard's default
	 * path; still accepted by the backend for API compatibility). */
	credential_id?: string;
	/** Ad hoc "my credentials" — mutually exclusive with `credential_id`,
	 * Operator+ (server-enforced; see RunsController.ValidateEphemeralCredentialRequest).
	 * Legacy run-level tier (issue #587 retired it from the wizard's default
	 * path; still accepted by the backend for API compatibility). */
	credential?: EphemeralCredentialInput;
	/** Issue #587: per-target/per-purpose saved-credential overrides. */
	credential_overrides?: CredentialOverrideInput[];
	/** Issue #587: per-target/per-purpose ad hoc credential overrides. */
	ad_hoc_credentials?: AdHocCredentialInput[];
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
		credential_overrides: input.credential_overrides && input.credential_overrides.length > 0 ? input.credential_overrides : undefined,
		ad_hoc_credentials: input.ad_hoc_credentials && input.ad_hoc_credentials.length > 0 ? input.ad_hoc_credentials : undefined,
	});
}

/**
 * Issue #733 remainder (epic #726 Wave 2, PR #874): `POST /api/v1/runs/plan-preview`'s
 * request body — the SAME `scope`/`credential_overrides`/`ad_hoc_credentials` shapes
 * `POST /runs` accepts for a scan (`Waypoint.Api.Contracts.RunPlanPreviewRequest`),
 * restricted to the `target_scope` form: preview never selects a profile (ADR-0022 §7),
 * so `scope.profile_id` must be absent here even though `ScanScope.profile_id` is
 * required for create. `previewScanRun`/`toPreviewRequest` below build this from the
 * exact same `ScanScope` object `createScanRun` sends, minus `profile_id` — never a
 * re-derivation from wizard state that could drift from what create actually submits.
 */
export interface PreviewScanRunInput {
	scope: Omit<ScanScope, "profile_id">;
	credential_overrides?: CredentialOverrideInput[];
	ad_hoc_credentials?: AdHocCredentialInput[];
}

/**
 * `Waypoint.Api.Contracts.RunPlanPreviewResponse` on the wire — the honest would-be
 * plan for a `target_scope` (issue #733/#734 remainder, PR #874). `plan_digest` is
 * byte-for-byte identical to a subsequent `POST /runs` create's digest for the
 * identical scope (issue #734 AC-4) — the confirm step displays it so run history can
 * later correlate "this is the plan I previewed." `is_runnable` is false only when
 * *zero* items were accepted; a legitimately empty explicit selection (see
 * `hasEmptyExplicitSelection` in `useScanWizard`) is a separate, honest empty-plan
 * case the wizard renders without treating it as a failure — preview itself is always
 * 200 for a zero-runnable scope (never the `no_runnable_component` 400 create returns
 * for the same inputs), so the wizard's copy must say "preview warns, create blocks."
 */
export interface PlanPreviewResponse {
	requested_mode: string;
	resolved_component_ids: string[];
	scope_omissions: ScopeOmission[];
	plan_schema_version: number;
	items: ScanPlanItem[];
	skips: ScanPlanSkip[];
	plan_digest: string;
	explanation: string;
	is_runnable: boolean;
	credential_gaps: CredentialBindingGap[];
}

/**
 * Builds the exact `target_scope`-only scope object `POST /runs/plan-preview` accepts
 * from the same `ScanScope` the wizard would submit to `POST /runs` — dropping only
 * `profile_id` (preview-rejected, ADR-0022 §7). Sharing this function between the
 * preview call and `createScanRun`'s input is what guarantees "preview-request payload
 * equals create payload for identical selections" (issue #733 AC): there is exactly
 * one place scope is assembled from wizard state, and preview/create both consume it.
 */
export function toPreviewScope(scope: ScanScope): Omit<ScanScope, "profile_id"> {
	const { profile_id: _profileId, ...rest } = scope;
	return rest;
}

export function previewScanRun(input: PreviewScanRunInput): Promise<PlanPreviewResponse> {
	return apiPost<PlanPreviewResponse>("/runs/plan-preview", {
		scope: JSON.stringify(input.scope),
		credential_overrides: input.credential_overrides && input.credential_overrides.length > 0 ? input.credential_overrides : undefined,
		ad_hoc_credentials: input.ad_hoc_credentials && input.ad_hoc_credentials.length > 0 ? input.ad_hoc_credentials : undefined,
	});
}
