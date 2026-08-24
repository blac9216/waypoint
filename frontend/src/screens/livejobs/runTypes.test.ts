import { describe, expect, it } from "vitest";
import { NON_COMPLIANCE_RUN_TYPES } from "./HistoryPanel";

/**
 * PR #712 / issue #708: `NON_COMPLIANCE_RUN_TYPES` (the default History-view
 * `run_type` filter) must stay in sync with the backend closed set
 * `Waypoint.Core.Jobs.RunTypes.All` (authoritative `runs_run_type_check` as of
 * migration 0042) minus the two compliance-owned types (`scan`, `remediate`).
 *
 * There is no cross-runtime harness in this repo (backend is .NET, frontend is
 * Vitest/Node), so — exactly like `credential-purposes.test.ts` mirrors
 * `CredentialPurposes.All` — this side hardcodes its expected view of the backend
 * list and both must be updated together. The backend-side twin is
 * `RunTypesConstraintDriftTests`, which proves `RunTypes.All` equals the CHECK
 * constraint; this proves the frontend default view equals `RunTypes.All` minus
 * scan/remediate. The stale-set bug the review caught (omitting `credential-test`,
 * `tool-install`, `purge`) fails here.
 */

// Waypoint.Core.Jobs.RunTypes.All, in declaration order (0042's constraint order).
const EXPECTED_BACKEND_RUN_TYPES = [
	"scan",
	"remediate",
	"discover",
	"download",
	"catalog-index",
	"bundle-export",
	"bundle-import",
	"content-library-sync",
	"content-pull",
	"content-import",
	"update",
	"credential-test",
	"tool-install",
	"purge",
] as const;

const COMPLIANCE_RUN_TYPES = ["scan", "remediate"];

describe("NON_COMPLIANCE_RUN_TYPES (backend RunTypes.All parity)", () => {
	const values = NON_COMPLIANCE_RUN_TYPES.split(",");

	it("is exactly RunTypes.All minus the compliance-owned types, in order", () => {
		const expected = EXPECTED_BACKEND_RUN_TYPES.filter((t) => !COMPLIANCE_RUN_TYPES.includes(t));
		expect(values).toEqual(expected);
	});

	it("never includes scan or remediate (windowed out of the default view)", () => {
		expect(values).not.toContain("scan");
		expect(values).not.toContain("remediate");
	});

	it("includes the three types migration 0042 added (credential-test, tool-install, purge)", () => {
		expect(values).toContain("credential-test");
		expect(values).toContain("tool-install");
		expect(values).toContain("purge");
	});
});
