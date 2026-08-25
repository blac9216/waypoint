import { describe, expect, it } from "vitest";
import { COMPLIANCE_RUN_TYPES as RESULTS_COMPLIANCE_RUN_TYPES } from "../results/useRunList";
import { isComplianceRun } from "./dashboard";

/**
 * Issue #717 drift guard. The Dashboard's RECENT RUNS card and the Results
 * screen must agree on which `run_type` values are compliance-owned — a row that
 * passes the dashboard filter but not Results' would deep-link into an empty
 * Results view (and vice versa). `dashboard.ts` re-encodes the closed set rather
 * than importing it (the repo's screen-decoupling convention), so — exactly like
 * `livejobs/runTypes.test.ts` binds `NON_COMPLIANCE_RUN_TYPES` to the backend
 * `RunTypes.All` list — this binds the dashboard set to `useRunList.ts`'s
 * exported `COMPLIANCE_RUN_TYPES` so the two cannot silently diverge.
 *
 * Both are, in turn, the backend `Waypoint.Core.Jobs.RunTypes.{Scan,Remediate}`
 * closed set the aggregate now filters on server-side (proven byte-identical to
 * the `runs_run_type_check` CHECK constraint by `RunTypesConstraintDriftTests`).
 */
describe("dashboard COMPLIANCE_RUN_TYPES parity with Results", () => {
	it("accepts exactly the run types Results treats as compliance-owned", () => {
		for (const runType of RESULTS_COMPLIANCE_RUN_TYPES) {
			expect(isComplianceRun(runType)).toBe(true);
		}
	});

	it("rejects any run type Results does not treat as compliance-owned", () => {
		const operational = ["discover", "credential-test", "content-pull", "catalog-index", "tool-install", "purge"];
		for (const runType of operational) {
			expect(RESULTS_COMPLIANCE_RUN_TYPES.has(runType)).toBe(false);
			expect(isComplianceRun(runType)).toBe(false);
		}
	});

	it("covers the whole Results set and nothing beyond it (no silent divergence)", () => {
		// Every compliance type the dashboard admits must be one Results admits.
		const dashboardCompliance = ["scan", "remediate"].filter((t) => isComplianceRun(t));
		expect(dashboardCompliance).toEqual([...RESULTS_COMPLIANCE_RUN_TYPES]);
	});
});
