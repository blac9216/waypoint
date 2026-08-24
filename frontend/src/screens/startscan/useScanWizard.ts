/**
 * useScanWizard — all Start-a-Scan wizard state (issue #419 extraction from
 * StartScanScreen.tsx, no behavior change). Owns the five-step walk's data:
 * site load, scope/inventory load + selection, credential mode + options,
 * and confirm/submit. StartScanScreen.tsx wires this hook to the step
 * components in ./StartScanSteps and renders the stepper/nav chrome.
 */
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { ApiError, type CredentialBindingGap } from "../../lib/api";
import { roleAtLeast, roleGateProps, type Role } from "../../lib/roles";
import { CREDENTIAL_PURPOSE_SATISFYING_TYPES, requiredScanPurposes, type CredentialPurpose } from "../configuration/credential-purposes";
import { fetchSites, fetchTargets, type Site, type Target, type TargetKind } from "../configuration/sites";
import { fetchCredentialOptions, type CredentialOption } from "../configuration/sites";
import {
	createScanRun,
	fetchProfileOptions,
	fetchTargetInventory,
	flattenInventory,
	type AdHocCredentialInput,
	type CredentialOverrideInput,
	type InventoryItem,
	type ProfileOption,
} from "./startscan";

export type StepKey = "site" | "scope" | "credential" | "schedule" | "confirm";

export const STEPS: { key: StepKey; label: string }[] = [
	{ key: "site", label: "Site" },
	{ key: "scope", label: "Scope" },
	{ key: "credential", label: "Credential" },
	{ key: "schedule", label: "Schedule" },
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

/** One target's selection state for the scope step. `"all"` means every
 * inventory item under the target is included (also the state a target with
 * empty inventory falls back to when the whole target is toggled on),
 * `"partial"` drives the tri-state parent checkboxes, `"none"` excludes it. */
export interface TargetSelection {
	target: Target;
	inventory: InventoryItem[] | null;
	loadingInventory: boolean;
	/** Selected inventory item ids; ignored (target-level fallback) when
	 * `inventory` is an empty array. */
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
					inventory: null,
					loadingInventory: true,
					selectedItemIds: new Set<string>(),
					targetSelected: true,
				}));
				setSelections(next);
				await Promise.all(
					targets.map(async (target, index) => {
						try {
							const inv = await fetchTargetInventory(target.id);
							const flat = flattenInventory(inv.items);
							setSelections((prev) => {
								const copy = [...prev];
								if (copy[index]?.target.id === target.id) {
									copy[index] = {
										...copy[index],
										inventory: inv.items,
										loadingInventory: false,
										selectedItemIds: new Set(flat.map((item) => item.id)),
									};
								}
								return copy;
							});
						} catch {
							// Inventory fetch failure for one target falls back to the
							// target-level checkbox, same as an empty inventory — a single
							// target's discovery gap must not block scoping the rest.
							setSelections((prev) => {
								const copy = [...prev];
								if (copy[index]?.target.id === target.id) {
									copy[index] = { ...copy[index], inventory: [], loadingInventory: false };
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

	const toggleTarget = useCallback((targetId: string, on: boolean) => {
		setSelections((prev) =>
			prev.map((sel) =>
				sel.target.id === targetId
					? { ...sel, targetSelected: on, selectedItemIds: on ? new Set(flattenInventory(sel.inventory ?? []).map((i) => i.id)) : new Set() }
					: sel,
			),
		);
	}, []);

	const toggleInventoryItem = useCallback((targetId: string, itemId: string, on: boolean) => {
		setSelections((prev) =>
			prev.map((sel) => {
				if (sel.target.id !== targetId) {
					return sel;
				}
				const next = new Set(sel.selectedItemIds);
				if (on) {
					next.add(itemId);
				} else {
					next.delete(itemId);
				}
				const total = flattenInventory(sel.inventory ?? []).length;
				return { ...sel, selectedItemIds: next, targetSelected: next.size > 0 || total === 0 };
			}),
		);
	}, []);

	const selectedTargetIds = useMemo(
		() => selections.filter((s) => s.targetSelected && (s.inventory === null || s.inventory.length === 0 || s.selectedItemIds.size > 0)).map((s) => s.target.id),
		[selections],
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

	// -- step 5: confirm ---------------------------------------------------
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
		try {
			const scope = selectedTargetIds.length > 0 && selectedTargetIds.length < selections.length
				? { site_id: siteId, target_ids: selectedTargetIds, profile_id: profileId }
				: { site_id: siteId, profile_id: profileId };

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

			const result = await createScanRun({
				scope,
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
			navigate(`/live-run?run=${result.run_id}`);
		} catch (err) {
			if (err instanceof ApiError && err.bindingGaps && err.bindingGaps.length > 0) {
				setBindingGapErrors(err.bindingGaps);
				setSubmitError(err.message);
			} else {
				setSubmitError(err instanceof ApiError ? err.message : "Could not start the scan.");
			}
		} finally {
			setSubmitting(false);
			submitGuardRef.current = false;
		}
	}, [selectedTargetIds, selections.length, siteId, profileId, overrides, navigate]);

	const stepIndex = STEPS.findIndex((s) => s.key === step);
	const canAdvance = useCallback(
		(key: StepKey): boolean => {
			if (key === "scope" || key === "credential" || key === "schedule" || key === "confirm") {
				return siteId !== "";
			}
			return true;
		},
		[siteId],
	);

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

		submitting,
		submitError,
		canConfirm,
		submit,
	};
}

export type ScanWizard = ReturnType<typeof useScanWizard>;
