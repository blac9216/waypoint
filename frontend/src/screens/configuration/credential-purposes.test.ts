import { describe, expect, it } from "vitest";
import {
	CREDENTIAL_PURPOSE_MATRIX,
	CREDENTIAL_PURPOSE_SATISFYING_TYPES,
	CREDENTIAL_PURPOSES,
	type CredentialPurpose,
} from "./credential-purposes";
import { CREDENTIAL_TYPES } from "./credentials";
import { TARGET_KINDS } from "./sites";

/**
 * ADR-0021 / issue #583: this file's constants and matrix must stay in sync with the
 * backend closed set (`Waypoint.Core.Secrets.CredentialPurposes`/`CredentialPurposeMatrix`,
 * `backend/Waypoint.Core/Secrets/CredentialPurposes.cs`) and its completeness tests
 * (`backend/Waypoint.Tests/Core/Secrets/CredentialPurposeMatrixTests.cs`). There is no
 * cross-runtime test harness in this repo (backend is .NET, frontend is Vitest/Node),
 * so each side hardcodes its expected view of the other and both must be updated
 * together -- the same convention this file's sibling contracts already use
 * (sites.ts/credentials.ts doc comments naming their backend counterparts).
 */
const EXPECTED_BACKEND_PURPOSES: readonly CredentialPurpose[] = ["vsphere-api", "vcsa-ssh", "nsx-api", "srg-ssh"];

describe("CREDENTIAL_PURPOSES (backend Waypoint.Core.Secrets.CredentialPurposes.All parity)", () => {
	it("has exactly the wire values CredentialPurposes.All defines on the backend", () => {
		expect(CREDENTIAL_PURPOSES.map((p) => p.value).sort()).toEqual([...EXPECTED_BACKEND_PURPOSES].sort());
	});

	it("has a distinct value for vsphere-api and vcsa-ssh (ADR-0021's headline AC)", () => {
		const values = CREDENTIAL_PURPOSES.map((p) => p.value);
		expect(values).toContain("vsphere-api");
		expect(values).toContain("vcsa-ssh");
		expect("vsphere-api").not.toBe("vcsa-ssh");
	});
});

describe("CREDENTIAL_PURPOSE_SATISFYING_TYPES", () => {
	it("only references credential types in the closed CREDENTIAL_TYPES set", () => {
		const validTypes = new Set(CREDENTIAL_TYPES.map((t) => t.value as string));
		for (const purpose of Object.keys(CREDENTIAL_PURPOSE_SATISFYING_TYPES) as CredentialPurpose[]) {
			for (const type of CREDENTIAL_PURPOSE_SATISFYING_TYPES[purpose]) {
				expect(validTypes.has(type)).toBe(true);
			}
		}
	});

	it("gives every purpose at least one satisfying credential type", () => {
		for (const purpose of EXPECTED_BACKEND_PURPOSES) {
			expect(CREDENTIAL_PURPOSE_SATISFYING_TYPES[purpose].length).toBeGreaterThan(0);
		}
	});
});

describe("CREDENTIAL_PURPOSE_MATRIX", () => {
	it("covers every current target kind", () => {
		const coveredKinds = new Set(CREDENTIAL_PURPOSE_MATRIX.map((e) => e.kind));
		for (const { value } of TARGET_KINDS) {
			expect(coveredKinds.has(value)).toBe(true);
		}
	});

	it("gives discovery an entry only for vsphere (the only INVENTORY_CAPABLE kind)", () => {
		const discoveryKinds = [...new Set(CREDENTIAL_PURPOSE_MATRIX.filter((e) => e.operation === "discovery").map((e) => e.kind))];
		expect(discoveryKinds).toEqual(["vsphere"]);
	});

	it("requires only vsphere-api for vsphere discovery, never vcsa-ssh", () => {
		const discovery = CREDENTIAL_PURPOSE_MATRIX.find((e) => e.kind === "vsphere" && e.operation === "discovery");
		expect(discovery?.requiredPurposes).toEqual(["vsphere-api"]);
		expect(discovery?.optionalPurposes).not.toContain("vcsa-ssh");
	});

	it("requires both vsphere-api and vcsa-ssh for the vsphere vcsa scan component", () => {
		const vcsaScan = CREDENTIAL_PURPOSE_MATRIX.find((e) => e.kind === "vsphere" && e.operation === "scan" && e.component === "vcsa");
		expect([...(vcsaScan?.requiredPurposes ?? [])].sort()).toEqual(["vcsa-ssh", "vsphere-api"]);
	});

	it("gives credential-test, scan, and remediation-ready-planning entries for every target kind", () => {
		for (const { value } of TARGET_KINDS) {
			const operations = new Set(CREDENTIAL_PURPOSE_MATRIX.filter((e) => e.kind === value).map((e) => e.operation));
			expect(operations.has("credential-test")).toBe(true);
			expect(operations.has("scan")).toBe(true);
			expect(operations.has("remediation-ready-planning")).toBe(true);
		}
	});

	it("only references purposes in the closed CREDENTIAL_PURPOSES set", () => {
		const validPurposes = new Set(CREDENTIAL_PURPOSES.map((p) => p.value as string));
		for (const entry of CREDENTIAL_PURPOSE_MATRIX) {
			for (const purpose of [...entry.requiredPurposes, ...entry.optionalPurposes]) {
				expect(validPurposes.has(purpose)).toBe(true);
			}
		}
	});

	it("every purpose in the closed set is referenced by at least one matrix entry", () => {
		const referenced = new Set(CREDENTIAL_PURPOSE_MATRIX.flatMap((e) => [...e.requiredPurposes, ...e.optionalPurposes]));
		for (const purpose of EXPECTED_BACKEND_PURPOSES) {
			expect(referenced.has(purpose)).toBe(true);
		}
	});

	it("every entry has at least one required purpose", () => {
		for (const entry of CREDENTIAL_PURPOSE_MATRIX) {
			expect(entry.requiredPurposes.length).toBeGreaterThan(0);
		}
	});
});
