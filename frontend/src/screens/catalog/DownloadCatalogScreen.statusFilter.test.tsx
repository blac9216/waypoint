import { render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { SystemProvider } from "../../lib/system";
import { DownloadCatalogScreen } from "./DownloadCatalogScreen";
import type { CatalogArtifact } from "./catalog";

/**
 * Issue #1802 gap 2: the status filter's `STATUS_OPTIONS` used to coerce any
 * status `displayStatus` couldn't map (`?? "not_downloaded"`) to the "Not
 * downloaded" label — the exact mislabeling trap review round 1 finding F2
 * removed from `ArtifactTable.tsx`'s own rendering path. Today's five
 * `ArtifactStatus` members are all mapped, so the coercion is latent; this
 * test forces `displayStatus("indexed")` to return `null` (as it would for a
 * future backend status `artifactStatus.test.ts`'s parity guard hasn't
 * caught yet) and asserts the dropdown falls back to the raw wire value
 * "indexed" rather than mislabeling it "Not downloaded".
 */
vi.mock("./catalog", async () => {
	const actual = await vi.importActual<typeof import("./catalog")>("./catalog");
	return {
		...actual,
		displayStatus: (status: Parameters<typeof actual.displayStatus>[0]) =>
			status === "indexed" ? null : actual.displayStatus(status),
	};
});

const ARTIFACTS: CatalogArtifact[] = [
	{
		id: "art-1",
		name: "VCF-Installer-5.2.1.iso",
		sha256: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
		product: "VCF Installer",
		version: "5.2.1",
		size_bytes: 4_294_967_296,
		status: "indexed",
	},
];

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("DownloadCatalogScreen status filter (displayStatus forced to null for 'indexed')", () => {
	let originalFetch: typeof fetch;

	beforeEach(() => {
		originalFetch = globalThis.fetch;
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			if (url.startsWith("/api/v1/events") || /^\/api\/v1\/runs\/[^/]+\/events/.test(url)) {
				return { ok: true, status: 200, body: null } as unknown as Response;
			}
			if (url.startsWith("/api/v1/catalog/artifacts")) {
				return new Response(JSON.stringify(ARTIFACTS), {
					status: 200,
					headers: { "Content-Type": "application/json", "X-Total-Count": String(ARTIFACTS.length) },
				});
			}
			if (url === "/api/v1/catalog/pull" && (!init || init.method === undefined || init.method === "GET")) {
				return jsonResponse({ ready: true });
			}
			if (url === "/api/v1/downloads" && (!init || init.method === undefined || init.method === "GET")) {
				return jsonResponse([]);
			}
			if (url === "/api/v1/system") {
				return jsonResponse({ version: "2.4.1", build: "24817", mode: "connected", update_available: null });
			}
			if (url === "/api/v1/stigman") {
				return jsonResponse({ error: { code: "not_found", message: "No global STIG Manager connection is configured." } }, 404);
			}
			throw new Error(`unexpected fetch: ${url}`);
		}) as unknown as typeof fetch;

		window.sessionStorage.setItem(
			"waypoint.session",
			JSON.stringify({
				token: "tok-1",
				username: "j.moreno",
				role: "Operator",
				expiresAt: new Date(Date.now() + 60_000).toISOString(),
			}),
		);
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
		window.sessionStorage.clear();
	});

	it("falls back to the raw wire value instead of mislabeling an unmapped status 'Not downloaded'", async () => {
		render(
			<AuthProvider>
				<SystemProvider>
					<DownloadCatalogScreen />
				</SystemProvider>
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("VCF-Installer-5.2.1.iso")).toBeInTheDocument());

		const statusFilter = screen.getByLabelText("Filter by status") as HTMLSelectElement;
		expect(within(statusFilter).queryByRole("option", { name: "Not downloaded" })).not.toBeInTheDocument();
		expect(within(statusFilter).getByRole("option", { name: "indexed" })).toBeInTheDocument();
	});
});
