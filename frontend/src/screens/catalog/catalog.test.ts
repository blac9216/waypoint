import { describe, expect, it } from "vitest";
import {
	type CatalogArtifact,
	filterArtifactsBySearch,
	friendlyProductName,
	groupArtifactsByProduct,
	isKubernetesProduct,
	productType,
} from "./catalog";

/**
 * `friendlyProductName`/`humanizeProductKey` table tests (issue #1588).
 * `humanizeProductKey` itself is not exported — every case here goes through
 * `friendlyProductName` on a key deliberately absent from `KNOWN_PRODUCT_NAMES`
 * so the fallback humanizer is what's actually under test, using the repo's
 * own fixture keys (`DownloadCatalogScreen.test.tsx`'s `ARTIFACTS`/
 * `dominantVkrArtifacts` use `"ESXi"` and `"VCF Installer"` verbatim as raw
 * `product` values) plus this module's own doc-comment example.
 */
describe("friendlyProductName (issue #1588: mixed-case keys keep interior capitalisation)", () => {
	it.each([
		// Already mixed-case, single segment (no underscore) — left untouched.
		["ESXi", "ESXi"],
		// Already mixed-case, contains a literal space — left untouched (the
		// space is not a `_` delimiter, so this is one "segment").
		["VCF Installer", "VCF Installer"],
		// Pure UPPER_SNAKE_CASE, unmapped — this module's own doc-comment
		// example: every segment is upper-case, so title-casing applies.
		["SUPERVISOR_SERVICE_ABC", "Supervisor Service ABC"],
		// Pure UPPER_SNAKE_CASE with only short (<=3 char) segments — stays
		// upper-cased (acronyms/version-style tokens), not title-cased.
		["VKS_ABC", "VKS ABC"],
		// Mixed: one short upper segment, one longer upper segment.
		["VKS_CLUSTER", "VKS Cluster"],
	])("friendlyProductName(%s) -> %s", (key, expected) => {
		expect(friendlyProductName(key)).toBe(expected);
	});

	it("still prefers a KNOWN_PRODUCT_NAMES mapping over the humanizer", () => {
		expect(friendlyProductName("ESX_HOST")).toBe("ESXi");
		expect(friendlyProductName("VCENTER")).toBe("vCenter Server");
	});
});

/**
 * Issue #797: a malformed row (the vendor catalog document itself, indexed
 * with NULL product/version by the backend defect this issue also fixes)
 * must never crash the search filter or the product grouping — both used to
 * call `.toLowerCase()`/`.split("_")` on a field that could be missing.
 */
function malformedArtifact(overrides: Partial<CatalogArtifact> = {}): CatalogArtifact {
	return {
		id: "malformed-1",
		name: null as unknown as string,
		sha256: null as unknown as string,
		product: null as unknown as string,
		version: null as unknown as string,
		size_bytes: 0,
		status: "indexed",
		...overrides,
	};
}

function validArtifact(overrides: Partial<CatalogArtifact> = {}): CatalogArtifact {
	return {
		id: "valid-1",
		name: "VCSA-8.0U3.iso",
		sha256: "aa".repeat(32),
		product: "VCENTER",
		version: "8.0.3",
		size_bytes: 100,
		status: "indexed",
		...overrides,
	};
}

describe("filterArtifactsBySearch (issue #797: null-unsafe name/sha256 crashed the search filter)", () => {
	it("never throws on a row with a null name and sha256", () => {
		const artifacts = [malformedArtifact(), validArtifact()];
		expect(() => filterArtifactsBySearch(artifacts, "vcsa")).not.toThrow();
	});

	it("excludes the malformed row from a non-empty search match, includes the valid one", () => {
		const artifacts = [malformedArtifact(), validArtifact()];
		const result = filterArtifactsBySearch(artifacts, "vcsa");
		expect(result).toHaveLength(1);
		expect(result[0].id).toBe("valid-1");
	});

	it("keeps every row, malformed included, when search is empty", () => {
		const artifacts = [malformedArtifact(), validArtifact()];
		expect(filterArtifactsBySearch(artifacts, "")).toHaveLength(2);
	});
});

describe("groupArtifactsByProduct (issue #797: null product crashed the group key's friendly-name lookup)", () => {
	it("never throws on a row with a null product, grouping it separately from real products", () => {
		const artifacts = [malformedArtifact(), validArtifact()];
		let groups: ReturnType<typeof groupArtifactsByProduct> = [];
		expect(() => {
			groups = groupArtifactsByProduct(artifacts);
		}).not.toThrow();
		expect(groups).toHaveLength(2);
		expect(groups.map((g) => g.friendlyName)).toContain("Unknown");
		expect(groups.map((g) => g.friendlyName)).toContain("vCenter Server");
	});
});

describe("isKubernetesProduct (issue #797: null product crashed the core/Kubernetes type filter)", () => {
	it("never throws on a null/undefined product and classifies it as non-Kubernetes (core)", () => {
		expect(() => isKubernetesProduct(null)).not.toThrow();
		expect(() => isKubernetesProduct(undefined)).not.toThrow();
		expect(isKubernetesProduct(null)).toBe(false);
		expect(isKubernetesProduct(undefined)).toBe(false);
		expect(productType(null as unknown as string)).toBe("core");
	});

	it("still recognises the real Kubernetes-stack keys", () => {
		expect(isKubernetesProduct("VKR")).toBe(true);
		expect(isKubernetesProduct("VKS_CLUSTER")).toBe(true);
		expect(isKubernetesProduct("SUPERVISOR_SERVICE_ABC")).toBe(true);
		expect(isKubernetesProduct("VCENTER")).toBe(false);
	});

	// Mirrors DownloadCatalogScreen's `typedArtifacts` filter
	// (`isKubernetesProduct(a.product) === (type === "kubernetes")`), the exact
	// path that threw a TypeError on a null-product row before this fix.
	it("lets the screen's type filter run over a malformed null-product row without throwing", () => {
		const artifacts = [malformedArtifact(), validArtifact({ product: "VKR" })];
		const coreFilter = () => artifacts.filter((a) => isKubernetesProduct(a.product) === false);
		const k8sFilter = () => artifacts.filter((a) => isKubernetesProduct(a.product) === true);

		expect(coreFilter).not.toThrow();
		expect(k8sFilter).not.toThrow();
		// The null-product row is treated as core (kept by the core filter, excluded by the Kubernetes filter).
		expect(coreFilter().map((a) => a.id)).toEqual(["malformed-1"]);
		expect(k8sFilter().map((a) => a.id)).toEqual(["valid-1"]);
	});
});
