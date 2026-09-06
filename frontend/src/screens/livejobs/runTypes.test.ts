/// <reference types="node" />
// This file is the one exception in src/ that reads a file off disk (the
// backend's RunTypes.cs, at test time -- see below); @types/node is already a
// devDependency (tsconfig.node.json's vite.config.ts build already uses it),
// just not part of tsconfig.app.json's browser-facing `types` list that the
// rest of src/ typechecks against, so this reference is scoped to this file
// alone rather than widening the whole app project's global types.
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { NON_COMPLIANCE_RUN_TYPES } from "./HistoryPanel";

/**
 * PR #712 / issue #708: `NON_COMPLIANCE_RUN_TYPES` (the default History-view
 * `run_type` filter) must stay in sync with the backend closed set
 * `Waypoint.Core.Jobs.RunTypes.All` (authoritative `runs_run_type_check`) minus the
 * two compliance-owned types (`scan`, `remediate`).
 *
 * PR #1759's review caught this test's own bug: it used to hardcode its expected
 * view of the backend list (`EXPECTED_BACKEND_RUN_TYPES`) exactly like
 * `credential-purposes.test.ts` mirrors `CredentialPurposes.All` — but a hardcoded
 * mirror only catches drift the mirror's author remembers to update, and this one
 * had gone four migrations stale (0042's `credential-test`/`tool-install`/`purge`
 * was the last update; 0048/0049/0099 each added a type — `depot-enrollment`,
 * `catalog-pull`, `binaries-download` — that neither this test's expectation nor
 * `NON_COMPLIANCE_RUN_TYPES` itself ever picked up).
 *
 * There is no cross-runtime test harness in this repo (backend is .NET, frontend is
 * Vitest/Node) to run backend code from here, but this test does not need to run
 * any -- `RunTypes.cs`'s `All` array is plain source text, so this parses it
 * directly off disk (the same "read the authoritative source at test time" posture
 * the backend-side twin, `RunTypesConstraintDriftTests`, uses against the migration
 * SQL) rather than re-typing a second copy of the list that can go stale again. Any
 * future run type added to `RunTypes.All` without a matching update to
 * `NON_COMPLIANCE_RUN_TYPES` (or vice versa) fails here immediately.
 */

const RUN_TYPES_CS_PATH = path.resolve(
	path.dirname(fileURLToPath(import.meta.url)),
	"../../../../backend/Waypoint.Core/Jobs/RunTypes.cs",
);

/** Parses every `public const string Name = "value";` declaration plus the
 * `All = [ ... ]` array (in declaration order) out of `RunTypes.cs`, and resolves
 * the array's identifiers to their string values -- i.e. exactly what
 * `RunTypes.All` evaluates to on the backend, without executing any C#. */
function parseBackendRunTypesAll(): string[] {
	const source = readFileSync(RUN_TYPES_CS_PATH, "utf8");

	const constants = new Map<string, string>();
	for (const match of source.matchAll(/public const string (\w+) = "([^"]*)";/g)) {
		constants.set(match[1], match[2]);
	}

	const allMatch = source.match(/All\s*=\s*\r?\n?\s*\[([^\]]*)\]/);
	if (!allMatch) {
		throw new Error(`could not find 'All = [ ... ]' in ${RUN_TYPES_CS_PATH}`);
	}

	const identifiers = allMatch[1]
		.split(",")
		.map((s) => s.trim())
		.filter((s) => s.length > 0);

	return identifiers.map((identifier) => {
		const value = constants.get(identifier);
		if (value === undefined) {
			throw new Error(`RunTypes.All references '${identifier}', which has no matching 'public const string' declaration`);
		}
		return value;
	});
}

const COMPLIANCE_RUN_TYPES = ["scan", "remediate"];

describe("NON_COMPLIANCE_RUN_TYPES (backend RunTypes.All parity, parsed from RunTypes.cs)", () => {
	const backendAll = parseBackendRunTypesAll();
	const values = NON_COMPLIANCE_RUN_TYPES.split(",");

	it("parsed at least the values this test already knows about (parser sanity check)", () => {
		// Guards the parser itself, independent of NON_COMPLIANCE_RUN_TYPES: if a
		// future refactor of RunTypes.cs's shape breaks the regexes above into
		// silently returning an empty/truncated list, this fails before the parity
		// assertion below could misreport "in sync" against an empty expectation.
		expect(backendAll.length).toBeGreaterThanOrEqual(14);
		expect(backendAll).toContain("scan");
		expect(backendAll).toContain("binaries-download");
	});

	it("is exactly the backend RunTypes.All minus the compliance-owned types, in order", () => {
		const expected = backendAll.filter((t) => !COMPLIANCE_RUN_TYPES.includes(t));
		expect(values).toEqual(expected);
	});

	it("never includes scan or remediate (windowed out of the default view)", () => {
		expect(values).not.toContain("scan");
		expect(values).not.toContain("remediate");
	});

	it("includes every run type RunTypes.All defines today", () => {
		for (const runType of backendAll) {
			if (COMPLIANCE_RUN_TYPES.includes(runType)) {
				continue;
			}
			expect(values).toContain(runType);
		}
	});
});
