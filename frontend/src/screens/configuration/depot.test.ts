import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
	fetchDownloadReadiness,
	fetchManagedToolInstalls,
	formatManagedToolOutcome,
	formatSource,
	formatTimestamp,
	installManagedToolFromLocalRepository,
	uploadManagedTool,
} from "./depot";

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(body === undefined ? null : JSON.stringify(body), {
		status,
		headers: { "Content-Type": "application/json" },
	});
}

describe("depot.ts data layer (issue #571/#39/#560)", () => {
	let originalFetch: typeof fetch;
	let calls: { url: string; method: string; body?: unknown; init?: RequestInit }[];

	beforeEach(() => {
		originalFetch = globalThis.fetch;
		calls = [];
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			const method = init?.method ?? "GET";
			calls.push({
				url,
				method,
				body: init?.body && typeof init.body === "string" ? JSON.parse(init.body) : undefined,
				init,
			});
			return jsonResponse({ ok: true }, method === "POST" ? 202 : 200);
		}) as unknown as typeof fetch;
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
	});

	it("fetchDownloadReadiness issues GET /downloads/readiness", async () => {
		await fetchDownloadReadiness();
		expect(calls[0]).toEqual({ url: "/api/v1/downloads/readiness", method: "GET", body: undefined, init: calls[0].init });
	});

	it("installManagedToolFromLocalRepository issues POST /downloads/tool/install with source_path", async () => {
		await installManagedToolFromLocalRepository("vcf-download-tool/tool.tar.gz");
		expect(calls[0].url).toBe("/api/v1/downloads/tool/install");
		expect(calls[0].method).toBe("POST");
		expect(calls[0].body).toEqual({ source_path: "vcf-download-tool/tool.tar.gz", version: undefined });
	});

	it("installManagedToolFromLocalRepository carries an optional version", async () => {
		await installManagedToolFromLocalRepository("tool.tar.gz", "1.4.2");
		expect(calls[0].body).toEqual({ source_path: "tool.tar.gz", version: "1.4.2" });
	});

	it("uploadManagedTool posts artifact plus both published checksum fields", async () => {
		const artifact = new File(["binary"], "vcf-download-tool.tar.gz");
		await uploadManagedTool(artifact, { sha256: "a".repeat(64), md5: "b".repeat(32) }, "1.4.2");

		expect(calls[0].url).toBe("/api/v1/downloads/tool/upload");
		expect(calls[0].method).toBe("POST");
		const form = calls[0].init?.body as FormData;
		expect(form).toBeInstanceOf(FormData);
		expect(form.get("artifact")).toBe(artifact);
		expect(form.get("sha256")).toBe("a".repeat(64));
		expect(form.get("md5")).toBe("b".repeat(32));
		expect(form.get("version")).toBe("1.4.2");
		// Content-Type must NOT be set by hand — fetch derives the multipart
		// boundary itself; a hand-set header here would omit it and the
		// server could never parse the parts.
		const headers = new Headers(calls[0].init?.headers);
		expect(headers.has("Content-Type")).toBe(false);
	});

	it("uploadManagedTool omits the version field when not provided", async () => {
		const artifact = new File(["binary"], "tool.tar.gz");
		await uploadManagedTool(artifact, { sha256: "a".repeat(64) });

		const form = calls[0].init?.body as FormData;
		expect(form.get("version")).toBeNull();
	});

	it("fetchManagedToolInstalls issues GET /downloads/tool/installs", async () => {
		await fetchManagedToolInstalls();
		expect(calls[0].url).toBe("/api/v1/downloads/tool/installs");
		expect(calls[0].method).toBe("GET");
	});

	it("formatManagedToolOutcome/formatSource render known + unknown values", () => {
		expect(formatManagedToolOutcome("installed")).toBe("installed");
		expect(formatManagedToolOutcome("rejected")).toBe("rejected");
		expect(formatManagedToolOutcome("something-new")).toBe("something-new");
		expect(formatSource("local-repository")).toBe("local repository");
		expect(formatSource("upload")).toBe("manual upload");
		expect(formatSource("depot")).toBe("depot fetch");
	});

	it("formatTimestamp never invents a value for a missing/invalid timestamp", () => {
		expect(formatTimestamp(null)).toBe("—");
		expect(formatTimestamp(undefined)).toBe("—");
		expect(formatTimestamp("not-a-date")).toBe("—");
		expect(formatTimestamp("2026-08-08T00:00:00Z")).toBe("2026-08-08 00:00Z");
	});
});
