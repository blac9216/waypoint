/**
 * useScanWizard — all Start-a-Scan wizard state (issue #419 extraction from
 * StartScanScreen.tsx, no behavior change). Owns the five-step walk's data:
 * site load, scope/inventory load + selection, credential mode + options,
 * and confirm/submit. StartScanScreen.tsx wires this hook to the step
 * components in ./StartScanSteps and renders the stepper/nav chrome.
 */
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { ApiError, type CredentialBindingGap, type ScopeOmission } from "../../lib/api";
import { roleAtLeast, roleGateProps, type Role } from "../../lib/roles";
import { CREDENTIAL_PURPOSE_SATISFYING_TYPES, requiredScanPurposes, type CredentialPurpose } from "../configuration/credential-purposes";
import { fetchSites, fetchTargets, type Site, type Target, type TargetKind } from "../configuration/sites";
import { fetchCredentialOptions, type CredentialOption } from "../configuration/sites";
import {
	buildComponentTree,
	createScanRun,
	fetchProfileOptions,
	fetchTargetComponents,
	flattenComponentTree,
	isSelectableComponent,
	previewScanRun,
	resolveTargetScope,
	toPreviewScope,
	type AdHocCredentialInput,
	type ComponentTreeNode,
	type CredentialOverrideInput,
	type PlanPreviewResponse,
	type ProfileOption,
	type TargetScopeInput,
} from "./startscan";

export type StepKey = "site" | "scope" | "credential" | "schedule" | "preview" | "confirm";

export const STEPS: { key: StepKey; label: string }[] = [
	{ key: "site", label: "Site" },
	{ key: "scope", label: "Scope" },
	{ key: "credential", label: "Credential" },
	{ key: "schedule", label: "Schedule" },
	{ key: "preview", label: "Preview" },
	{ key: "confirm", label: "Confirm" },
];

/**
 * Issue #587 (epic #582): the credential step's top-level choice. `"assigned"`
 * (the default, ADR-0021 §4/§8 "default to target-assigned credentials") reads
 * coverage straight off the selected targets' own `bindings` (already on the
 * wire since #661 — no preflight endpoint needed) and sends nothing but the
 * scan scope; `"override"` lets an Operator/Cyber narrow or replace specific
 * (target, purpose) pairs with a saved or ad hoc credential. The pre-#587
 * single-service/personal-credential tiers are retired from this wizard (the
 * backend keeps accepting `credential_id`/`credential` for API compatibility,
 * but the UI no longer sends them — every case they covered is a degenerate
 * one-target-one-purpose override under this model).
 */
export type CredentialMode = "assigned" | "override";

/** One override entered for a (target, purpose) pair — either a saved credential id or an ad hoc username/secret, never both. */
export type OverrideEntry =
	| { kind: "saved"; credentialId: string }
	| { kind: "adhoc"; username: string; secret: string };

/** `${targetId}::${purpose}` — the map key overrides are stored/looked up by. */
export function overrideKey(targetId: string, purpose: CredentialPurpose): string {
	return `${targetId}::${purpose}`;
}

/** One row of the coverage summary: whether a required/optional purpose on a target resolves, and from where. */
export interface CoverageRow {
	targetId: string;
	targetName: string;
	targetKind: TargetKind | string;
	purpose: CredentialPurpose;
	required: boolean;
	/** `"override"` when an override entry covers this pair, `"binding"` when the target's own binding covers it (and there is no override), `"missing"` when a required purpose has neither. */
	source: "override" | "binding" | "missing";
	credentialName?: string;
}

/**
 * One target's selection state for the scope step. Issue #733 rewired this
 * from the legacy `inventory_items` tree onto the stable `components` model
 * (`GET /targets/{id}/components`) — `selectedItemIds` now holds
 * `component_id`s that are actually wired onto `POST /runs`'s
 * `scope.target_scope`, not discarded (the bug this issue's summary names).
 * `componentTree === null` means empty/not-yet-discovered: no components are
 * known for this target, so the target-level checkbox is the only control
 * and `target_scope` contributes nothing for it (legacy `target_ids`
 * fallback, same as before #733).
 */
export interface TargetSelection {
	target: Target;
	componentTree: ComponentTreeNode[] | null;
	loadingInventory: boolean;
	/** Selected component ids; ignored (target-level fallback) when
	 * `componentTree` is an empty array. */
	selectedItemIds: Set<string>;
	targetSelected: boolean;
}

export interface UseScanWizardArgs {
	userRole: Role | undefined;
	navigate: (path: string) => void;
}

export function useScanWizard({ userRole, navigate }: UseScanWizardArgs) {
	const [step, setStep] = useState<StepKey>("site");

	const allowed = userRole ? roleAtLeast(userRole, "Cyber") : false;
	const gate = userRole ? roleGateProps(userRole, "Cyber", "Requires Cyber or higher — scans are not available to your role") : { disabled: true };

	// -- step 1: site --------------------------------------------------
	const [sites, setSites] = useState<Site[]>([]);
	const [sitesLoading, setSitesLoading] = useState(true);
	const [sitesError, setSitesError] = useState<string | null>(null);
	const [siteId, setSiteId] = useState<string>("");

	useEffect(() => {
		if (!allowed) {
			return;
		}
		setSitesLoading(true);
		fetchSites()
			.then(setSites)
			.catch((err: unknown) => setSitesError(err instanceof ApiError ? err.message : "Could not load sites."))
			.finally(() => setSitesLoading(false));
	}, [allowed]);

	// -- step 2: scope ---------------------------------------------------
	const [selections, setSelections] = useState<TargetSelection[]>([]);
	const [scopeLoading, setScopeLoading] = useState(false);
	const [scopeError, setScopeError] = useState<string | null>(null);
	/** Issue #733: the last submit attempt's `scope_omissions` (400 `no_runnable_component`) — stale/removed selections that resolved to nothing runnable. Rendered on the Scope step with actionable refresh guidance (epic #726 §3: "fail with actionable refresh guidance rather than silently widening"), never auto-retried with a widened scope. Cleared on the next scope edit or submit attempt so a fixed selection never shows a stale error. */
	const [scopeOmissionErrors, setScopeOmissionErrors] = useState<ScopeOmission[]>([]);

	// Issue #639: the InSpec profile a scan executes against, fed by GET
	// /profiles (docs/ui/prototype/README.md screen 3 step 2: "the list of
	// InSpec profiles that will apply"). Fetched once the wizard becomes
	// usable, same lazy-on-relevant-step convention the credential step below
	// uses for fetchCredentialOptions — loaded here (not gated to the scope
	// step) since profile choice belongs to the same step and the operator
	// may open the picker before scope finishes loading.
	const [profiles, setProfiles] = useState<ProfileOption[]>([]);
	const [profilesLoading, setProfilesLoading] = useState(true);
	const [profilesError, setProfilesError] = useState<string | null>(null);
	const [profileId, setProfileId] = useState<string>("");

	useEffect(() => {
		if (!allowed) {
			return;
		}
		setProfilesLoading(true);
		fetchProfileOptions()
			.then(setProfiles)
			.catch((err: unknown) => setProfilesError(err instanceof ApiError ? err.message : "Could not load profiles."))
			.finally(() => setProfilesLoading(false));
	}, [allowed]);

	const loadScope = useCallback((site: string) => {
		setScopeLoading(true);
		setScopeError(null);
		fetchTargets(site)
			.then(async (targets) => {
				const next: TargetSelection[] = targets.map((target) => ({
					target,
					componentTree: null,
					loadingInventory: true,
					selectedItemIds: new Set<string>(),
					targetSelected: true,
				}));
				setSelections(next);
				await Promise.all(
					targets.map(async (target, index) => {
						try {
							const items = await fetchTargetComponents(target.id);
							const tree = buildComponentTree(items);
							const selectableIds = flattenComponentTree(tree)
								.filter(isSelectableComponent)
								.map((node) => node.id);
							setSelections((prev) => {
								const copy = [...prev];
								if (copy[index]?.target.id === target.id) {
									copy[index] = {
										...copy[index],
										componentTree: tree,
										loadingInventory: false,
										selectedItemIds: new Set(selectableIds),
									};
								}
								return copy;
							});
						} catch {
							// Component fetch failure for one target falls back to the
							// target-level checkbox, same as an empty component tree — a
							// single target's discovery gap must not block scoping the rest.
							setSelections((prev) => {
								const copy = [...prev];
								if (copy[index]?.target.id === target.id) {
									copy[index] = { ...copy[index], componentTree: [], loadingInventory: false };
								}
								return copy;
							});
						}
					}),
				);
			})
			.catch((err: unknown) => setScopeError(err instanceof ApiError ? err.message : "Could not load targets."))
			.finally(() => setScopeLoading(false));
	}, []);

	const selectSite = useCallback(
		(id: string) => {
			setSiteId(id);
			setSelections([]);
			if (id) {
				loadScope(id);
			}
		},
		[loadScope],
	);

	/**
	 * Issue #733 AC: "Parent tri-state semantics are deterministic." Toggling a
	 * parent node (including the whole-target row) on/off always sets every
	 * *selectable* (active) descendant to the same state — an explicit,
	 * total function of the tree and the new state, never a partial/leave-as-is
	 * merge. A parent's own checked/indeterminate/unchecked rendering is a pure
	 * derivation from its children's selection at render time (`componentCheckState`
	 * in StartScanSteps.tsx), never written into `selectedItemIds` itself, so
	 * the same selection set always renders the same tri-state tree regardless
	 * of the order clicks arrived in.
	 */
	const toggleTarget = useCallback((targetId: string, on: boolean) => {
		setSelections((prev) =>
			prev.map((sel) =>
				sel.target.id === targetId
					? {
							...sel,
							targetSelected: on,
							selectedItemIds: on ? new Set(flattenComponentTree(sel.componentTree ?? []).filter(isSelectableComponent).map((n) => n.id)) : new Set(),
						}
					: sel,
			),
		);
		setScopeOmissionErrors([]);
	}, []);

	/** Sets `node` and every selectable descendant of `node` to `on` in `next` (mutates the passed `Set`). */
	function setSubtreeSelection(node: ComponentTreeNode, on: boolean, next: Set<string>): void {
		if (isSelectableComponent(node)) {
			if (on) {
				next.add(node.id);
			} else {
				next.delete(node.id);
			}
		}
		for (const child of node.children) {
			setSubtreeSelection(child, on, next);
		}
	}

	function findNode(tree: ComponentTreeNode[], id: string): ComponentTreeNode | undefined {
		for (const node of tree) {
			if (node.id === id) {
				return node;
			}
			const found = findNode(node.children, id);
			if (found) {
				return found;
			}
		}
		return undefined;
	}

	/** Toggles one component node. Toggling a parent cascades to every selectable descendant (deterministic tri-state: "select all children" and "select this parent" are the same operation); toggling a leaf only ever changes that leaf — ancestor checked/indeterminate state is a pure read-time derivation (`componentCheckState` in StartScanSteps.tsx), never written here. */
	const toggleInventoryItem = useCallback((targetId: string, itemId: string, on: boolean) => {
		setSelections((prev) =>
			prev.map((sel) => {
				if (sel.target.id !== targetId) {
					return sel;
				}
				const tree = sel.componentTree ?? [];
				const node = findNode(tree, itemId);
				const next = new Set(sel.selectedItemIds);
				if (node) {
					setSubtreeSelection(node, on, next);
				} else if (on) {
					next.add(itemId);
				} else {
					next.delete(itemId);
				}
				const total = flattenComponentTree(tree).filter(isSelectableComponent).length;
				return { ...sel, selectedItemIds: next, targetSelected: next.size > 0 || total === 0 };
			}),
		);
		setScopeOmissionErrors([]);
	}, []);

	const selectedTargetIds = useMemo(
		() =>
			selections
				.filter((s) => s.targetSelected && (s.componentTree === null || s.componentTree.length === 0 || s.selectedItemIds.size > 0))
				.map((s) => s.target.id),
		[selections],
	);

	/**
	 * Issue #733: resolves every target's tree selection into the wire's
	 * `target_scope` shape. `all`-mode targets contribute their `target_id` to
	 * one combined `{ mode: "all", target_ids }` request; any target with a
	 * deliberately partial/empty selection instead contributes its resolved
	 * component ids to one combined `{ mode: "explicit", component_ids }`
	 * request — the two never mix within a single submitted `target_scope`
	 * (docs/api-contract.md's `target_scope` is one mode for the whole
	 * request). Mixed usage (some targets "all", others explicit) is resolved
	 * conservatively to `explicit`: an `all`-mode target's *currently known*
	 * selectable components are included by id rather than silently
	 * broadening the explicit request back out to "all" scope wide. A target
	 * with no known components (`componentTree` null/empty) contributes
	 * nothing here — it still rides the legacy `target_ids` fallback.
	 */
	const targetScope = useMemo<TargetScopeInput | undefined>(() => {
		const withComponents = selections.filter((s) => s.componentTree !== null && s.componentTree.length > 0);
		if (withComponents.length === 0) {
			return undefined;
		}
		const resolutions = withComponents.map((s) => ({
			targetId: s.target.id,
			resolved: resolveTargetScope(
				flattenComponentTree(s.componentTree ?? []).filter(isSelectableComponent).map((n) => n.id),
				s.selectedItemIds,
			),
		}));
		const allMode = resolutions.every((r) => r.resolved?.mode === "all");
		if (allMode) {
			return { mode: "all", target_ids: resolutions.map((r) => r.targetId) };
		}
		const componentIds = resolutions.flatMap((r) => (r.resolved?.mode === "all" ? flattenComponentTree(selections.find((s) => s.target.id === r.targetId)?.componentTree ?? []).filter(isSelectableComponent).map((n) => n.id) : (r.resolved?.component_ids ?? [])));
		return { mode: "explicit", component_ids: componentIds };
	}, [selections]);

	/** Issue #733 AC: "An empty explicit selection is presented honestly" — true when at least one target with known components resolved to an explicit selection of zero components (every component deliberately unchecked), so the wizard can warn without blocking or silently widening. */
	const hasEmptyExplicitSelection = useMemo(
		() => targetScope?.mode === "explicit" && targetScope.component_ids.length === 0 && selections.some((s) => s.componentTree !== null && s.componentTree.length > 0),
		[targetScope, selections],
	);

	// -- step 3: credential ------------------------------------------------
	// Issue #587 (epic #582): defaults to "assigned" (ADR-0021 §4/§8) — the
	// coverage summary below is derived entirely from data already on the
	// wire (`Target.bindings`, issue #661), no preflight call needed.
	// Saved-credential overrides need the same picker options the old
	// service-credential tier used; ad hoc overrides need the same Operator+
	// gate the old personal tier used — both floors are unchanged, just
	// applied per (target, purpose) instead of once for the whole run.
	const [credentialMode, setCredentialMode] = useState<CredentialMode>("assigned");
	const [credentialOptions, setCredentialOptions] = useState<CredentialOption[]>([]);
	const [credentialOptionsError, setCredentialOptionsError] = useState<string | null>(null);
	const [overrides, setOverrides] = useState<Map<string, OverrideEntry>>(new Map());

	const canUseAdHoc = userRole ? roleAtLeast(userRole, "Operator") : false;
	const adHocGate = userRole
		? roleGateProps(userRole, "Operator", "Requires Operator or higher — ad hoc personal credentials are not available to Cyber")
		: { disabled: true };

	useEffect(() => {
		if (!allowed || step !== "credential") {
			return;
		}
		fetchCredentialOptions()
			.then(setCredentialOptions)
			.catch((err: unknown) => setCredentialOptionsError(err instanceof ApiError ? err.message : "Could not load credentials."));
	}, [allowed, step]);

	/** The selected targets, deduplicated by (targetId, purpose), that a scan of the current scope requires or optionally uses. Component-conditional purposes (e.g. vsphere's `vcsa-ssh`) are not on the wire yet (no scan-component selection exists), so only the unconditionally required set drives coverage — see `requiredScanPurposes`. */
	const selectedTargets = useMemo(
		() => selections.filter((s) => selectedTargetIds.includes(s.target.id)).map((s) => s.target),
		[selections, selectedTargetIds],
	);

	/** Coverage summary (issue #587 AC: "show each required purpose and whether it's bound... missing/incompatible bindings surfaced before submission"). One row per (target, purpose) an override OR a scan of the target's kind cares about. */
	const coverage = useMemo<CoverageRow[]>(() => {
		const rows: CoverageRow[] = [];
		for (const target of selectedTargets) {
			const required = requiredScanPurposes(target.kind);
			const bindingsByPurpose = new Map(target.bindings.map((b) => [b.purpose, b]));
			for (const purpose of required) {
				const key = overrideKey(target.id, purpose);
				const override = overrides.get(key);
				const binding = bindingsByPurpose.get(purpose);
				if (override) {
					rows.push({
						targetId: target.id,
						targetName: target.name,
						targetKind: target.kind,
						purpose,
						required: true,
						source: "override",
						credentialName: override.kind === "saved" ? credentialOptions.find((c) => c.id === override.credentialId)?.name : `${override.username} (ad hoc)`,
					});
				} else if (binding) {
					rows.push({
						targetId: target.id,
						targetName: target.name,
						targetKind: target.kind,
						purpose,
						required: true,
						source: "binding",
						credentialName: binding.credential_name ?? undefined,
					});
				} else {
					rows.push({ targetId: target.id, targetName: target.name, targetKind: target.kind, purpose, required: true, source: "missing" });
				}
			}
		}
		return rows;
	}, [selectedTargets, overrides, credentialOptions]);

	const missingCoverage = useMemo(() => coverage.filter((row) => row.required && row.source === "missing"), [coverage]);

	/** A gap the last submit attempt reported (issue #587: parse `binding_gaps` into per-target/purpose messages mapped onto this step, not a generic toast). Cleared whenever the coverage/override state changes so a stale gap never lingers past the edit that fixes it. */
	const [bindingGapErrors, setBindingGapErrors] = useState<CredentialBindingGap[]>([]);

	const setSavedOverride = useCallback((targetId: string, purpose: CredentialPurpose, credentialId: string) => {
		setOverrides((prev) => {
			const next = new Map(prev);
			next.set(overrideKey(targetId, purpose), { kind: "saved", credentialId });
			return next;
		});
		setBindingGapErrors([]);
	}, []);

	const setAdHocOverride = useCallback((targetId: string, purpose: CredentialPurpose, username: string, secret: string) => {
		setOverrides((prev) => {
			const next = new Map(prev);
			next.set(overrideKey(targetId, purpose), { kind: "adhoc", username, secret });
			return next;
		});
		setBindingGapErrors([]);
	}, []);

	const clearOverride = useCallback((targetId: string, purpose: CredentialPurpose) => {
		setOverrides((prev) => {
			const next = new Map(prev);
			next.delete(overrideKey(targetId, purpose));
			return next;
		});
	}, []);

	/**
	 * Issue #587 AC: "bulk selection only for compatible groups" — applies one
	 * saved credential to every (target, purpose) pair in the coverage list
	 * whose purpose's compatibility set (the shared matrix) includes the
	 * credential's type. Incompatible pairs are silently skipped, never
	 * force-applied — the picker itself is already filtered per row, this is
	 * the same filter applied across a whole purpose column at once.
	 */
	const bulkApplySaved = useCallback(
		(purpose: CredentialPurpose, credentialId: string) => {
			const credential = credentialOptions.find((c) => c.id === credentialId);
			if (!credential || !CREDENTIAL_PURPOSE_SATISFYING_TYPES[purpose].includes(credential.credential_type as never)) {
				return;
			}
			setOverrides((prev) => {
				const next = new Map(prev);
				for (const target of selectedTargets) {
					if (requiredScanPurposes(target.kind).includes(purpose)) {
						next.set(overrideKey(target.id, purpose), { kind: "saved", credentialId });
					}
				}
				return next;
			});
			setBindingGapErrors([]);
		},
		[credentialOptions, selectedTargets],
	);

	// -- step 4b: preview ---------------------------------------------------
	// Issue #733 remainder (epic #726 Wave 2, PR #874): `scope` below is the
	// SINGLE assembly point for the wizard's `ScanScope` — both `runPreview`
	// (via `toPreviewScope`, which only drops `profile_id`) and `submit` build
	// their request from this exact object, never a separate re-derivation
	// that could drift between what was previewed and what gets created.
	const scope = useMemo(
		() => ({
			site_id: siteId,
			profile_id: profileId,
			...(selectedTargetIds.length > 0 && selectedTargetIds.length < selections.length ? { target_ids: selectedTargetIds } : {}),
			...(targetScope ? { target_scope: targetScope } : {}),
		}),
		[siteId, profileId, selectedTargetIds, selections.length, targetScope],
	);

	/** Same (target_id, purpose) override map, reshaped into the wire arrays both preview and create send — one place, so preview and create never disagree about which overrides were requested. */
	const credentialRequestPayload = useMemo(() => {
		const credentialOverrides: CredentialOverrideInput[] = [];
		const adHocCredentials: AdHocCredentialInput[] = [];
		for (const [key, entry] of overrides) {
			const [targetId, purpose] = key.split("::");
			if (entry.kind === "saved") {
				credentialOverrides.push({ target_id: targetId, purpose, credential_id: entry.credentialId });
			} else {
				adHocCredentials.push({ target_id: targetId, purpose, username: entry.username, secret: entry.secret });
			}
		}
		return { credentialOverrides, adHocCredentials };
	}, [overrides]);

	const [preview, setPreview] = useState<PlanPreviewResponse | null>(null);
	const [previewLoading, setPreviewLoading] = useState(false);
	const [previewError, setPreviewError] = useState<string | null>(null);

	/**
	 * Issue #733 remainder: calls `POST /runs/plan-preview` with the identical
	 * `scope`/override payload `submit` below would send to `POST /runs` (minus
	 * `profile_id`, which preview rejects — ADR-0022 §7). Preview errors (400s —
	 * malformed scope, not the zero-runnable case, which is a 200 honest empty
	 * plan) render via the same `ApiError.message` idiom the rest of the wizard
	 * uses, distinct from `submitError` so a stale preview failure never bleeds
	 * into the confirm step's own error slot.
	 */
	const runPreview = useCallback(async () => {
		setPreviewLoading(true);
		setPreviewError(null);
		try {
			const result = await previewScanRun({
				scope: toPreviewScope(scope),
				credential_overrides: credentialRequestPayload.credentialOverrides.length > 0 ? credentialRequestPayload.credentialOverrides : undefined,
				ad_hoc_credentials: credentialRequestPayload.adHocCredentials.length > 0 ? credentialRequestPayload.adHocCredentials : undefined,
			});
			setPreview(result);
		} catch (err) {
			setPreview(null);
			setPreviewError(err instanceof ApiError ? err.message : "Could not preview the plan.");
		} finally {
			setPreviewLoading(false);
		}
	}, [scope, credentialRequestPayload]);

	// -- step 6: confirm ---------------------------------------------------
	const [submitting, setSubmitting] = useState(false);
	const [submitError, setSubmitError] = useState<string | null>(null);
	// House pattern: guard against a double-fire (double click / double Enter)
	// racing two POST /runs before `submitting` state has re-rendered the
	// disabled button — a plain ref check settles synchronously, state does not.
	const submitGuardRef = useRef(false);

	// Coverage (and therefore missingCoverage) already reflects overrides
	// layered on top of assigned bindings regardless of which radio is
	// selected — the mode only changes what the UI shows, not how coverage is
	// computed — so submission is gated on the same missing-coverage check in
	// both modes.
	const canConfirm = siteId !== "" && profileId !== "" && missingCoverage.length === 0;

	const submit = useCallback(async () => {
		if (submitGuardRef.current) {
			return;
		}
		submitGuardRef.current = true;
		setSubmitting(true);
		setSubmitError(null);
		setBindingGapErrors([]);
		setScopeOmissionErrors([]);
		try {
			const { credentialOverrides, adHocCredentials } = credentialRequestPayload;

			// Issue #895: POST /runs now rejects scope.profile_id whenever
			// scope.target_scope is set (the same rule plan-preview already
			// enforces), so submit must drop it there too -- toPreviewScope is the
			// one place that already knows how (shared with runPreview above), so
			// this is the identical payload runPreview just sent, never a second
			// hand-rolled omission that could drift from it. The legacy
			// (no target_scope) path is unaffected: profile_id still rides along.
			const result = await createScanRun({
				scope: targetScope ? toPreviewScope(scope) : scope,
				credential_overrides: credentialOverrides.length > 0 ? credentialOverrides : undefined,
				ad_hoc_credentials: adHocCredentials.length > 0 ? adHocCredentials : undefined,
			});
			// Write-only ad hoc secrets never linger in component state past a
			// successful submit — same convention CredentialsTab.tsx/the retired
			// personal-credential tier used.
			setOverrides((prev) => {
				const next = new Map<string, OverrideEntry>();
				for (const [key, entry] of prev) {
					if (entry.kind === "saved") {
						next.set(key, entry);
					}
				}
				return next;
			});
			// Issue #707 (epic #706): lands on the restored Live Run console again
			// (not the global Live Jobs workspace) — this is the compliance
			// scan/remediate monitoring screen Start-a-Scan has always targeted;
			// #688 pointed it at /live-jobs while the console was deleted (#693).
			navigate(`/live-run?run=${result.run_id}`);
		} catch (err) {
			if (err instanceof ApiError && err.bindingGaps && err.bindingGaps.length > 0) {
				setBindingGapErrors(err.bindingGaps);
				setSubmitError(err.message);
			} else if (err instanceof ApiError && err.code === "no_runnable_component") {
				// Issue #733 AC: "Stale or removed selections fail with actionable
				// refresh guidance rather than silently widening scope" — surfaced on
				// the Scope step (scopeOmissionErrors), never auto-retried.
				setScopeOmissionErrors(err.scopeOmissions ?? []);
				setSubmitError(err.message);
			} else {
				setSubmitError(err instanceof ApiError ? err.message : "Could not start the scan.");
			}
		} finally {
			setSubmitting(false);
			submitGuardRef.current = false;
		}
	}, [scope, targetScope, credentialRequestPayload, navigate]);

	const stepIndex = STEPS.findIndex((s) => s.key === step);
	const canAdvance = useCallback(
		(key: StepKey): boolean => {
			if (key === "scope" || key === "credential" || key === "schedule" || key === "preview" || key === "confirm") {
				return siteId !== "";
			}
			return true;
		},
		[siteId],
	);

	// Issue #733 remainder: entering the Preview step (or re-entering it after
	// a scope/credential edit invalidated the last preview) fetches a fresh
	// plan preview automatically — the wizard never shows a stale preview next
	// to a changed selection. `preview`/`previewError` are cleared whenever the
	// step is left so returning to Preview always re-fetches rather than
	// flashing last time's (possibly stale) result before the new one arrives.
	useEffect(() => {
		if (step !== "preview" || !allowed) {
			return;
		}
		setPreview(null);
		setPreviewError(null);
		void runPreview();
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, [step, allowed]);

	const siteName = sites.find((s) => s.id === siteId)?.name ?? "";
	const selectedProfileName = profiles.find((p) => p.id === profileId)?.name ?? "";
	/** Confirm-step summary text: "N assigned" when every purpose resolves off target bindings with no override, otherwise a short override count — never a single flat credential name, since this model can name a different credential per (target, purpose). */
	const selectedCredentialSummary = useMemo(() => {
		if (coverage.length === 0) {
			return "—";
		}
		const overrideCount = coverage.filter((row) => row.source === "override").length;
		if (overrideCount === 0) {
			return `${coverage.length} assigned from target bindings`;
		}
		return `${overrideCount} override${overrideCount === 1 ? "" : "s"}, ${coverage.length - overrideCount} from target bindings`;
	}, [coverage]);

	return {
		step,
		setStep,
		stepIndex,
		canAdvance,
		allowed,
		gate,

		sites,
		sitesLoading,
		sitesError,
		siteId,
		selectSite,
		siteName,

		selections,
		scopeLoading,
		scopeError,
		toggleTarget,
		toggleInventoryItem,
		selectedTargetIds,
		targetScope,
		hasEmptyExplicitSelection,
		scopeOmissionErrors,

		profiles,
		profilesLoading,
		profilesError,
		profileId,
		setProfileId,
		selectedProfileName,

		credentialMode,
		setCredentialMode,
		credentialOptions,
		credentialOptionsError,
		overrides,
		setSavedOverride,
		setAdHocOverride,
		clearOverride,
		bulkApplySaved,
		canUseAdHoc,
		adHocGate,
		coverage,
		missingCoverage,
		bindingGapErrors,
		selectedCredentialSummary,

		preview,
		previewLoading,
		previewError,
		runPreview,

		submitting,
		submitError,
		canConfirm,
		submit,
	};
}

export type ScanWizard = ReturnType<typeof useScanWizard>;
