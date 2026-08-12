/**
 * useScanWizard — all Start-a-Scan wizard state (issue #419 extraction from
 * StartScanScreen.tsx, no behavior change). Owns the five-step walk's data:
 * site load, scope/inventory load + selection, credential mode + options,
 * and confirm/submit. StartScanScreen.tsx wires this hook to the step
 * components in ./StartScanSteps and renders the stepper/nav chrome.
 */
import { useCallback, useEffect, useMemo, useState } from "react";
import { ApiError } from "../../lib/api";
import { roleAtLeast, roleGateProps, type Role } from "../../lib/roles";
import { fetchSites, fetchTargets, type Site, type Target } from "../configuration/sites";
import { fetchCredentialOptions, type CredentialOption } from "../configuration/sites";
import { createScanRun, fetchTargetInventory, flattenInventory, type InventoryItem } from "./startscan";

export type StepKey = "site" | "scope" | "credential" | "schedule" | "confirm";

export const STEPS: { key: StepKey; label: string }[] = [
	{ key: "site", label: "Site" },
	{ key: "scope", label: "Scope" },
	{ key: "credential", label: "Credential" },
	{ key: "schedule", label: "Schedule" },
	{ key: "confirm", label: "Confirm" },
];

export type CredentialMode = "service" | "personal";

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

	// -- step 3: credential ----------------------------------------------
	const [credentialMode, setCredentialMode] = useState<CredentialMode>("service");
	const [credentialOptions, setCredentialOptions] = useState<CredentialOption[]>([]);
	const [credentialOptionsError, setCredentialOptionsError] = useState<string | null>(null);
	const [serviceCredentialId, setServiceCredentialId] = useState("");
	const [personalUsername, setPersonalUsername] = useState("");
	const [personalSecret, setPersonalSecret] = useState("");

	const canUsePersonal = userRole ? roleAtLeast(userRole, "Operator") : false;
	const personalGate = userRole
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

	/** Clears the write-only personal secret from component state — same
	 * convention as CredentialsTab.tsx's form reset after submit, so nothing
	 * lingers in a closure after the request is done with it. */
	const clearPersonalSecret = useCallback(() => setPersonalSecret(""), []);

	// -- step 5: confirm ---------------------------------------------------
	const [submitting, setSubmitting] = useState(false);
	const [submitError, setSubmitError] = useState<string | null>(null);

	const canConfirm = siteId !== "" && (credentialMode === "service" ? serviceCredentialId !== "" : personalUsername !== "" && personalSecret !== "");

	const submit = useCallback(async () => {
		setSubmitting(true);
		setSubmitError(null);
		try {
			const scope = selectedTargetIds.length > 0 && selectedTargetIds.length < selections.length
				? { site_id: siteId, target_ids: selectedTargetIds }
				: { site_id: siteId };
			const result = await createScanRun({
				scope,
				credential_id: credentialMode === "service" ? serviceCredentialId : undefined,
				credential: credentialMode === "personal" ? { kind: "personal", username: personalUsername, secret: personalSecret } : undefined,
			});
			clearPersonalSecret();
			navigate(`/live-run?run=${result.run_id}`);
		} catch (err) {
			setSubmitError(err instanceof ApiError ? err.message : "Could not start the scan.");
		} finally {
			setSubmitting(false);
		}
	}, [selectedTargetIds, selections.length, siteId, credentialMode, serviceCredentialId, personalUsername, personalSecret, clearPersonalSecret, navigate]);

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
	const selectedCredentialName =
		credentialMode === "service" ? (credentialOptions.find((c) => c.id === serviceCredentialId)?.name ?? "") : `${personalUsername} (personal, not stored)`;

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

		credentialMode,
		setCredentialMode,
		credentialOptions,
		credentialOptionsError,
		serviceCredentialId,
		setServiceCredentialId,
		personalUsername,
		setPersonalUsername,
		personalSecret,
		setPersonalSecret,
		canUsePersonal,
		personalGate,
		selectedCredentialName,

		submitting,
		submitError,
		canConfirm,
		submit,
	};
}

export type ScanWizard = ReturnType<typeof useScanWizard>;
