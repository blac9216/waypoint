/// <reference types="node" />
// Same posture as ../livejobs/runTypes.test.ts: reads the backend's
// authoritative source off disk at test time rather than re-typing a second
// copy of its value list that can go stale again (issue #1768's own root
// cause — the previous `ArtifactStatus` union was a hand-maintained mirror
// that had drifted from `depot_artifacts.status` entirely).
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { displayStatus, type ArtifactStatus } from "./catalog";

/**
 * Issue #1768: `ArtifactStatus` must stay in sync with the backend's closed
 * `depot_artifacts.status` vocabulary — `Waypoint.Core.Catalog.DepotArtifactStatuses.All`
 * (`backend/Waypoint.Core/Catalog/DepotArtifact.cs`), authoritative against
 * migration 0129's `depot_artifacts_status_check` (the backend side already
 * guards this constraint against drift via
 * `DepotArtifactStatusesConstraintDriftTests`). There is no cross-runtime
 * harness in this repo to run the backend's C# from here, but
 * `DepotArtifactStatuses.All`'s declaration is plain source text, so this
 * parses it directly off disk rather than hand-mirroring the list, exactly
 * the way `livejobs/runTypes.test.ts` treats `RunTypes.cs`.
 */

const DEPOT_ARTIFACT_CS_PATH = path.resolve(
	path.dirname(fileURLToPath(import.meta.url)),
	"../../../../backend/Waypoint.Core/Catalog/DepotArtifact.cs",
);

/** Parses every `public const string Name = "value";` declaration inside
 * `DepotArtifactStatuses` plus its `All = [ ... ]` array (in declaration
 * order), and resolves the array's identifiers to their string values —
 * i.e. exactly what `DepotArtifactStatuses.All` evaluates to on the
 * backend, without executing any C#. */
function parseBackendDepotArtifactStatusesAll(): string[] {
	const source = readFileSync(DEPOT_ARTIFACT_CS_PATH, "utf8");

	const classMatch = source.match(/class DepotArtifactStatuses\s*\{([\s\S]*?)\n\}/);
	if (!classMatch) {
		throw new Error(`could not find 'class DepotArtifactStatuses { ... }' in ${DEPOT_ARTIFACT_CS_PATH}`);
	}
	const body = classMatch[1];

	const constants = new Map<string, string>();
	for (const match of body.matchAll(/public const string (\w+) = "([^"]*)";/g)) {
		constants.set(match[1], match[2]);
	}

	const allMatch = body.match(/\bAll\s*=\s*\[([^\]]*)\];/);
	if (!allMatch) {
		throw new Error(`could not find 'All = [ ... ]' inside DepotArtifactStatuses in ${DEPOT_ARTIFACT_CS_PATH}`);
	}

	const identifiers = allMatch[1]
		.split(",")
		.map((s) => s.trim())
		.filter((s) => s.length > 0);

	return identifiers.map((identifier) => {
		const value = constants.get(identifier);
		if (value === undefined) {
			throw new Error(
				`DepotArtifactStatuses.All references '${identifier}', which has no matching 'public const string' declaration`,
			);
		}
		return value;
	});
}

describe("ArtifactStatus (backend DepotArtifactStatuses.All parity, parsed from DepotArtifact.cs)", () => {
	const backendAll = parseBackendDepotArtifactStatusesAll();

	it("parsed at least the values this test already knows about (parser sanity check)", () => {
		expect(backendAll.length).toBeGreaterThanOrEqual(5);
		expect(backendAll).toContain("indexed");
		expect(backendAll).toContain("missing");
	});

	it("is exactly the backend DepotArtifactStatuses.All, in order", () => {
		const values: ArtifactStatus[] = ["indexed", "downloading", "present", "failed", "missing"];
		expect(values).toEqual(backendAll);
	});

	it("displayStatus maps every backend status to a non-null rendering label", () => {
		// Review round 2 finding G1: a `not.toThrow()` assertion here could
		// never fail — `displayStatus` is a pure switch ending in
		// `default: return null` and throws for nothing. Asserting the
		// returned label is not `null` is what actually pins "every backend
		// status has a rendering label": splicing a case out of
		// `displayStatus`'s switch (so that status falls through to the
		// `default: return null` fallback) turns this red.
		for (const status of backendAll) {
			expect(displayStatus(status as ArtifactStatus)).not.toBeNull();
		}
	});

	it("issue #1792 F2: displayStatus returns null (not a silent 'not_downloaded' mislabel) for a status outside ArtifactStatus", () => {
		expect(displayStatus("archived" as ArtifactStatus)).toBeNull();
	});
});
