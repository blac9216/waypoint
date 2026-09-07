import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { ArtifactTable } from "./ArtifactTable";
import type { CatalogArtifact } from "./catalog";

/**
 * Review round 1 finding F2: `displayStatus` has no runtime fallback for a
 * wire `status` outside `ArtifactStatus` (backend drift this table has no
 * other way to detect) — before the fix it silently rendered "not
 * downloaded", the opposite of the truth for a status this code has never
 * seen. This proves the raw wire value is surfaced instead.
 */
describe("ArtifactTable (issue #1792 F2: unrecognized status fallback)", () => {
	function artifact(status: string): CatalogArtifact {
		return {
			id: "a1",
			name: "VCF-Installer-5.2.1.iso",
			sha256: "deadbeef",
			product: "VCF Installer",
			version: "5.2.1",
			size_bytes: 1024,
			// Cast past the closed `ArtifactStatus` union — this is exactly
			// the "backend sent a value this frontend doesn't know about yet"
			// case `displayStatus`'s fallback exists for.
			status: status as CatalogArtifact["status"],
		};
	}

	it("renders the raw wire status rather than silently mislabeling it as 'not downloaded'", () => {
		render(
			<ArtifactTable
				artifacts={[artifact("archived")]}
				loading={false}
				selected={new Set()}
				onToggle={() => {}}
				onToggleAll={() => {}}
				byArtifact={new Map()}
				onRetry={() => {}}
				canQueue={true}
			/>,
		);

		expect(screen.getByText("archived")).toBeInTheDocument();
		expect(screen.queryByText("not downloaded")).not.toBeInTheDocument();
	});

	it("still renders 'not downloaded' for the genuine indexed status", () => {
		render(
			<ArtifactTable
				artifacts={[artifact("indexed")]}
				loading={false}
				selected={new Set()}
				onToggle={() => {}}
				onToggleAll={() => {}}
				byArtifact={new Map()}
				onRetry={() => {}}
				canQueue={true}
			/>,
		);

		expect(screen.getByText("not downloaded")).toBeInTheDocument();
	});
});
