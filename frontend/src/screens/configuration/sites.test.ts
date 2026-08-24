import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
	connectionHost,
	createSite,
	createTarget,
	deleteSite,
	deleteTarget,
	fetchCredentialOptions,
	fetchDiscoveryRun,
	fetchSites,
	fetchTargets,
	formatDiscoveryStatus,
	formatTimestamp,
	isInventoryCapable,
	isTerminalRunState,
	queueDiscover,
	updateSite,
	updateTarget,
} from "./sites";

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(body === undefined ? null : JSON.stringify(body), {
		status,
		headers: { "Content-Type": "application/json" },
	});
}

describe("sites.ts data layer", () => {
	let originalFetch: typeof fetch;
	let calls: { url: string; method: string; body?: unknown }[];

	beforeEach(() => {
		originalFetch = globalThis.fetch;
		calls = [];
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			const method = init?.method ?? "GET";
			calls.push({ url, method, body: init?.body ? JSON.parse(init.body as string) : undefined });
			if (method === "DELETE") {
				return jsonResponse(undefined, 204);
			}
			return jsonResponse({ id: "x" }, method === "POST" ? 201 : 200);
		}) as unknown as typeof fetch;
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
	});

	it("fetchSites issues GET /sites", async () => {
		await fetchSites();
		expect(calls).toEqual([{ url: "/api/v1/sites", method: "GET", body: undefined }]);
	});

	it("createSite issues POST /sites with name/description", async () => {
		await createSite({ name: "Alpha Enclave", description: "Primary enclave" });
		expect(calls[0]).toEqual({
			url: "/api/v1/sites",
			method: "POST",
			body: { name: "Alpha Enclave", description: "Primary enclave" },
		});
	});

	it("createSite omits description when blank", async () => {
		await createSite({ name: "Alpha Enclave", description: "" });
		expect(calls[0].body).toEqual({ name: "Alpha Enclave" });
	});

	it("updateSite issues PUT /sites/{id} with clear_description when description is blank", async () => {
		await updateSite("site-1", { name: "Alpha", description: "" });
		expect(calls[0]).toEqual({
			url: "/api/v1/sites/site-1",
			method: "PUT",
			body: { name: "Alpha", clear_description: true },
		});
	});

	it("updateSite carries description and leaves clear_description false when present", async () => {
		await updateSite("site-1", { name: "Alpha", description: "new desc" });
		expect(calls[0].body).toEqual({ name: "Alpha", description: "new desc", clear_description: false });
	});

	it("deleteSite issues DELETE /sites/{id}, and appends ?force=true only when requested", async () => {
		await deleteSite("site-1");
		expect(calls[0]).toEqual({ url: "/api/v1/sites/site-1", method: "DELETE", body: undefined });

		await deleteSite("site-1", true);
		expect(calls[1].url).toBe("/api/v1/sites/site-1?force=true");
	});

	it("fetchTargets issues GET /sites/{id}/targets", async () => {
		await fetchTargets("site-1");
		expect(calls[0]).toEqual({ url: "/api/v1/sites/site-1/targets", method: "GET", body: undefined });
	});

	it("createTarget issues POST /sites/{id}/targets with connection as {host} only — never a raw secret field", async () => {
		await createTarget("site-1", { kind: "vsphere", name: "vcsa-01", host: "vcsa-01.example.internal", credential_ref: "cred-1" });
		expect(calls[0]).toEqual({
			url: "/api/v1/sites/site-1/targets",
			method: "POST",
			body: {
				kind: "vsphere",
				name: "vcsa-01",
				connection: { host: "vcsa-01.example.internal" },
				credential_ref: "cred-1",
			},
		});
	});

	it("createTarget omits credential_ref when none is selected", async () => {
		await createTarget("site-1", { kind: "ssh", name: "photon-01", host: "photon-01.example.internal" });
		expect(calls[0].body).toEqual({
			kind: "ssh",
			name: "photon-01",
			connection: { host: "photon-01.example.internal" },
		});
	});

	it("updateTarget issues PUT /targets/{id} with clear_credential_ref when no credential is selected", async () => {
		await updateTarget("target-1", { kind: "nsx-api", name: "nsx-01", host: "nsx-01.example.internal" });
		expect(calls[0]).toEqual({
			url: "/api/v1/targets/target-1",
			method: "PUT",
			body: {
				kind: "nsx-api",
				name: "nsx-01",
				connection: { host: "nsx-01.example.internal" },
				clear_credential_ref: true,
			},
		});
	});

	it("updateTarget carries credential_ref and leaves clear_credential_ref false when present", async () => {
		await updateTarget("target-1", {
			kind: "nsx-api",
			name: "nsx-01",
			host: "nsx-01.example.internal",
			credential_ref: "cred-2",
		});
		expect(calls[0].body).toEqual({
			kind: "nsx-api",
			name: "nsx-01",
			connection: { host: "nsx-01.example.internal" },
			credential_ref: "cred-2",
			clear_credential_ref: false,
		});
	});

	it("deleteTarget issues DELETE /targets/{id}", async () => {
		await deleteTarget("target-1");
		expect(calls[0]).toEqual({ url: "/api/v1/targets/target-1", method: "DELETE", body: undefined });
	});

	it("fetchCredentialOptions issues GET /credentials", async () => {
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			calls.push({ url: input.toString(), method: init?.method ?? "GET", body: undefined });
			return jsonResponse([{ id: "cred-1", name: "Alpha vCenter", credential_type: "vcenter" }]);
		}) as unknown as typeof fetch;

		const options = await fetchCredentialOptions();
		expect(calls[0]).toEqual({ url: "/api/v1/credentials", method: "GET", body: undefined });
		expect(options).toEqual([{ id: "cred-1", name: "Alpha vCenter", credential_type: "vcenter" }]);
	});

	it("fetchCredentialOptions excludes depot-token credentials (issue #571/#560 AC)", async () => {
		globalThis.fetch = vi.fn(async () =>
			jsonResponse([
				{ id: "cred-1", name: "Alpha vCenter", credential_type: "vcenter" },
				{ id: "cred-2", name: "Broadcom Support Portal", credential_type: "depot-token" },
			]),
		) as unknown as typeof fetch;

		const options = await fetchCredentialOptions();
		expect(options).toEqual([{ id: "cred-1", name: "Alpha vCenter", credential_type: "vcenter" }]);
		expect(options.some((o) => o.name === "Broadcom Support Portal")).toBe(false);
	});

	it("queueDiscover issues POST /targets/{id}/discover with no body", async () => {
		await queueDiscover("target-1");
		expect(calls[0]).toEqual({ url: "/api/v1/targets/target-1/discover", method: "POST", body: undefined });
	});

	it("fetchDiscoveryRun issues GET /runs/{id}", async () => {
		await fetchDiscoveryRun("run-1");
		expect(calls[0]).toEqual({ url: "/api/v1/runs/run-1", method: "GET", body: undefined });
	});
});

describe("isInventoryCapable (issue #557's shared target-kind contract)", () => {
	it("is true only for vsphere today, matching DiscoveryController.Discover's server-side guard", () => {
		expect(isInventoryCapable("vsphere")).toBe(true);
		expect(isInventoryCapable("nsx-api")).toBe(false);
		expect(isInventoryCapable("ssh")).toBe(false);
	});

	it("is false for an unrecognized kind", () => {
		expect(isInventoryCapable("something-new")).toBe(false);
	});
});

describe("isTerminalRunState", () => {
	it("is true for every run state with no further transition", () => {
		expect(isTerminalRunState("completed")).toBe(true);
		expect(isTerminalRunState("completed_with_failures")).toBe(true);
		expect(isTerminalRunState("aborted")).toBe(true);
	});

	it("is false for pending/running", () => {
		expect(isTerminalRunState("pending")).toBe(false);
		expect(isTerminalRunState("running")).toBe(false);
	});
});

describe("connectionHost", () => {
	it("extracts the host field from the connection JSON", () => {
		expect(connectionHost(JSON.stringify({ host: "vcsa-01.example.internal" }))).toBe("vcsa-01.example.internal");
	});

	it("returns an empty string for malformed JSON", () => {
		expect(connectionHost("not json")).toBe("");
	});

	it("returns an empty string when host is missing or not a string", () => {
		expect(connectionHost(JSON.stringify({}))).toBe("");
		expect(connectionHost(JSON.stringify({ host: 42 }))).toBe("");
	});
});

describe("formatDiscoveryStatus", () => {
	it("maps every closed-set status to a human label", () => {
		expect(formatDiscoveryStatus("never_discovered")).toBe("never discovered");
		expect(formatDiscoveryStatus("discovering")).toBe("discovering…");
		expect(formatDiscoveryStatus("discovered")).toBe("discovered");
		expect(formatDiscoveryStatus("failed")).toBe("discovery failed");
	});

	it("passes through an unrecognized status verbatim", () => {
		expect(formatDiscoveryStatus("something_new")).toBe("something_new");
	});
});

describe("formatTimestamp", () => {
	it("formats an ISO timestamp to the app's short UTC form", () => {
		expect(formatTimestamp("2026-08-01T12:34:56Z")).toBe("2026-08-01 12:34Z");
	});

	it("returns an em dash for null or invalid input", () => {
		expect(formatTimestamp(null)).toBe("—");
		expect(formatTimestamp("not a date")).toBe("—");
	});
});
