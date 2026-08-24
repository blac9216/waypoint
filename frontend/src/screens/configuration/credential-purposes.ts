/**
 * The credential-purpose model (design/contracts only -- ADR-0021
 * `docs/adr/0021-credential-purpose-matrix.md`, issue #583). Mirrors the backend's
 * closed set (`Waypoint.Core.Secrets.CredentialPurposes`,
 * `backend/Waypoint.Core/Secrets/CredentialPurposes.cs`) and its target-kind x
 * operation matrix (`CredentialPurposeMatrix`), following the same "explicit named
 * identifiers, never numbered slots" convention `TargetKind`/`TARGET_KINDS` (sites.ts)
 * and `CredentialType`/`CREDENTIAL_TYPES` (credentials.ts) already use.
 *
 * This is a design/contracts-only slice: nothing outside this file's own test consumes
 * these values yet. Persistence lands in issue #584; execution resolution in #585/#586;
 * the wizard UI (which will actually read this matrix to compute coverage/gaps) in #587.
 */

import type { CredentialType } from "./credentials";
import type { TargetKind } from "./sites";

export type CredentialPurpose = "vsphere-api" | "vcsa-ssh" | "nsx-api" | "srg-ssh";

export const CREDENTIAL_PURPOSES: { value: CredentialPurpose; label: string }[] = [
	{ value: "vsphere-api", label: "vSphere API" },
	{ value: "vcsa-ssh", label: "VCSA SSH" },
	{ value: "nsx-api", label: "NSX API" },
	{ value: "srg-ssh", label: "SRG SSH" },
];

/** Purpose → the credential type(s) that can satisfy it (ADR-0021 §2). */
export const CREDENTIAL_PURPOSE_SATISFYING_TYPES: Readonly<Record<CredentialPurpose, readonly CredentialType[]>> = {
	"vsphere-api": ["vcenter"],
	"vcsa-ssh": ["ssh"],
	"nsx-api": ["nsx"],
	"srg-ssh": ["ssh"],
};

/** The closed set of operations the matrix covers (ADR-0021 §3). */
export type CredentialPurposeOperation = "discovery" | "credential-test" | "scan" | "remediation-ready-planning";

export interface CredentialPurposeMatrixEntry {
	kind: TargetKind;
	operation: CredentialPurposeOperation;
	/** Distinguishes multiple rows sharing a `kind`/`operation` (e.g. vsphere scan has one row per component). `null` when there is exactly one row for that kind/operation. */
	component: string | null;
	requiredPurposes: readonly CredentialPurpose[];
	optionalPurposes: readonly CredentialPurpose[];
}

/**
 * The full target-kind x operation matrix (ADR-0021 §3), mirroring the backend's
 * `CredentialPurposeMatrix.Entries` exactly. Discovery has an entry only for
 * `"vsphere"` -- `"nsx-api"` and `"ssh"` targets have no discovery operation today
 * (only vsphere is `INVENTORY_CAPABLE`, see sites.ts).
 */
export const CREDENTIAL_PURPOSE_MATRIX: readonly CredentialPurposeMatrixEntry[] = [
	// vsphere
	{ kind: "vsphere", operation: "discovery", component: null, requiredPurposes: ["vsphere-api"], optionalPurposes: [] },
	{ kind: "vsphere", operation: "credential-test", component: "vcenter-api", requiredPurposes: ["vsphere-api"], optionalPurposes: [] },
	{ kind: "vsphere", operation: "credential-test", component: "vcsa-ssh", requiredPurposes: ["vcsa-ssh"], optionalPurposes: [] },
	{ kind: "vsphere", operation: "scan", component: "vcenter", requiredPurposes: ["vsphere-api"], optionalPurposes: [] },
	{ kind: "vsphere", operation: "scan", component: "esxi", requiredPurposes: ["vsphere-api"], optionalPurposes: [] },
	{ kind: "vsphere", operation: "scan", component: "vm", requiredPurposes: ["vsphere-api"], optionalPurposes: [] },
	{ kind: "vsphere", operation: "scan", component: "vcsa", requiredPurposes: ["vsphere-api", "vcsa-ssh"], optionalPurposes: [] },
	{ kind: "vsphere", operation: "remediation-ready-planning", component: null, requiredPurposes: ["vsphere-api"], optionalPurposes: ["vcsa-ssh"] },

	// nsx-api (no discovery operation exists for this kind)
	{ kind: "nsx-api", operation: "credential-test", component: null, requiredPurposes: ["nsx-api"], optionalPurposes: [] },
	{ kind: "nsx-api", operation: "scan", component: null, requiredPurposes: ["nsx-api"], optionalPurposes: [] },
	{ kind: "nsx-api", operation: "remediation-ready-planning", component: null, requiredPurposes: ["nsx-api"], optionalPurposes: [] },

	// ssh (SRG) (no discovery operation exists for this kind)
	{ kind: "ssh", operation: "credential-test", component: null, requiredPurposes: ["srg-ssh"], optionalPurposes: [] },
	{ kind: "ssh", operation: "scan", component: null, requiredPurposes: ["srg-ssh"], optionalPurposes: [] },
	{ kind: "ssh", operation: "remediation-ready-planning", component: null, requiredPurposes: ["srg-ssh"], optionalPurposes: [] },
];

/**
 * Every purpose applicable to a target kind (issue #584), across every
 * operation row for that kind (required or optional). Hoisted here from
 * SiteTargetsPanel.tsx (its original, pre-#587 home) so the Start-a-Scan
 * wizard (#587) can share the exact same derivation instead of re-deriving a
 * second copy of the same matrix walk.
 */
export function applicablePurposes(kind: TargetKind | string): CredentialPurpose[] {
	const purposes = new Set<CredentialPurpose>();
	for (const entry of CREDENTIAL_PURPOSE_MATRIX) {
		if (entry.kind === kind) {
			entry.requiredPurposes.forEach((p) => purposes.add(p));
			entry.optionalPurposes.forEach((p) => purposes.add(p));
		}
	}
	return CREDENTIAL_PURPOSES.map((p) => p.value).filter((p) => purposes.has(p));
}

/** Every purpose REQUIRED (not merely optional) by at least one operation row for the kind — drives the "missing required binding" coverage warning. */
export function requiredPurposes(kind: TargetKind | string): CredentialPurpose[] {
	const purposes = new Set<CredentialPurpose>();
	for (const entry of CREDENTIAL_PURPOSE_MATRIX) {
		if (entry.kind === kind) {
			entry.requiredPurposes.forEach((p) => purposes.add(p));
		}
	}
	return CREDENTIAL_PURPOSES.map((p) => p.value).filter((p) => purposes.has(p));
}

/**
 * Issue #587: the purposes a SCAN of `kind` unconditionally requires —
 * mirrors the backend's `CredentialPurposeMatrix.RequiredScanPurposes`
 * (`backend/Waypoint.Core/Secrets/CredentialPurposes.cs`): the intersection
 * of every `scan`-operation row's `requiredPurposes` for the kind. Purposes
 * required by only SOME scan components (e.g. `vcsa-ssh` for vsphere's VCSA
 * component) are `conditionalScanPurposes` instead — until scan-component
 * selection exists on the wire, they resolve opportunistically and never
 * block submission on their own.
 */
export function requiredScanPurposes(kind: TargetKind | string): CredentialPurpose[] {
	const scanEntries = CREDENTIAL_PURPOSE_MATRIX.filter((e) => e.kind === kind && e.operation === "scan");
	if (scanEntries.length === 0) {
		return [];
	}
	const intersection = scanEntries.reduce<Set<CredentialPurpose>>((acc, entry, index) => {
		if (index === 0) {
			return new Set(entry.requiredPurposes);
		}
		const entryPurposes = new Set(entry.requiredPurposes);
		return new Set([...acc].filter((p) => entryPurposes.has(p)));
	}, new Set());
	return CREDENTIAL_PURPOSES.map((p) => p.value).filter((p) => intersection.has(p));
}

/**
 * Issue #587: purposes required by SOME but not ALL scan components of
 * `kind` (mirrors the backend's `ConditionalScanPurposes`) — resolve
 * opportunistically when bound/overridden, never a hard coverage gap by
 * themselves.
 */
export function conditionalScanPurposes(kind: TargetKind | string): CredentialPurpose[] {
	const required = new Set(requiredScanPurposes(kind));
	const scanEntries = CREDENTIAL_PURPOSE_MATRIX.filter((e) => e.kind === kind && e.operation === "scan");
	const all = new Set<CredentialPurpose>();
	for (const entry of scanEntries) {
		entry.requiredPurposes.forEach((p) => all.add(p));
		entry.optionalPurposes.forEach((p) => all.add(p));
	}
	return CREDENTIAL_PURPOSES.map((p) => p.value).filter((p) => all.has(p) && !required.has(p));
}

export function purposeLabel(purpose: CredentialPurpose): string {
	return CREDENTIAL_PURPOSES.find((p) => p.value === purpose)?.label ?? purpose;
}
