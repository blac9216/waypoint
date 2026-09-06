import { describe, expect, it } from "vitest";
import { friendlyProductName } from "./catalog";

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
