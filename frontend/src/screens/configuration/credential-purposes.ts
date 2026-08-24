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
