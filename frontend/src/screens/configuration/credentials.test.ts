import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
	createCredential,
	deleteCredential,
	fetchCredentials,
	formatHealth,
	formatTimestamp,
	testCredential,
	updateCredential,
} from "./credentials";

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(body === undefined ? null : JSON.stringify(body), {
		status,
		headers: { "Content-Type": "application/json" },
	});
}

describe("credentials.ts data layer", () => {
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
			return jsonResponse({ id: "cred-1" }, method === "POST" ? 201 : 200);
		}) as unknown as typeof fetch;
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
	});

	it("fetchCredentials issues GET /credentials", async () => {
		await fetchCredentials();
		expect(calls).toEqual([{ url: "/api/v1/credentials", method: "GET", body: undefined }]);
	});

	it("createCredential issues POST /credentials with name/type/username/secret", async () => {
		await createCredential({
			name: "Alpha vCenter service account",
			credential_type: "vcenter",
			username: "svc-stig@example.internal",
			sudo_enabled: false,
			secret: "s3cret-value",
		});
		expect(calls[0]).toEqual({
			url: "/api/v1/credentials",
			method: "POST",
			body: {
				name: "Alpha vCenter service account",
				credential_type: "vcenter",
				username: "svc-stig@example.internal",
				secret: "s3cret-value",
			},
		});
	});

	it("createCredential omits username/secret when blank, and sudo_enabled unless credential_type is ssh", async () => {
		await createCredential({ name: "Token cred", credential_type: "token", username: "", sudo_enabled: true, secret: "" });
		expect(calls[0].body).toEqual({ name: "Token cred", credential_type: "token" });
	});

	it("createCredential includes sudo_enabled only for credential_type ssh", async () => {
		await createCredential({ name: "SSH svc", credential_type: "ssh", username: "svc", sudo_enabled: true, secret: "hunter2" });
		expect(calls[0].body).toEqual({
			name: "SSH svc",
			credential_type: "ssh",
			username: "svc",
			sudo_enabled: true,
			secret: "hunter2",
		});
	});

	it("createCredential never sends a field named 'owner' — ADR-0011, shared is implicit server-side", async () => {
		await createCredential({ name: "x", credential_type: "vcenter", username: "", sudo_enabled: false, secret: "" });
		expect(Object.keys(calls[0].body as object)).not.toContain("owner");
	});

	it("updateCredential issues PUT /credentials/{id}, omitting secret when blank (leaves stored secret untouched)", async () => {
		await updateCredential("cred-1", { name: "Renamed", credential_type: "vcenter", username: "new-user", sudo_enabled: false, secret: "" });
		expect(calls[0]).toEqual({
			url: "/api/v1/credentials/cred-1",
			method: "PUT",
			body: { name: "Renamed", username: "new-user", secret: undefined },
		});
		expect(calls[0].body).not.toHaveProperty("secret", expect.anything());
	});

	it("updateCredential carries a new secret only when the user typed one", async () => {
		await updateCredential("cred-1", { name: "Renamed", credential_type: "ssh", username: "u", sudo_enabled: true, secret: "new-secret" });
		expect(calls[0].body).toEqual({ name: "Renamed", username: "u", sudo_enabled: true, secret: "new-secret" });
	});

	it("deleteCredential issues DELETE /credentials/{id}", async () => {
		await deleteCredential("cred-1");
		expect(calls[0]).toEqual({ url: "/api/v1/credentials/cred-1", method: "DELETE", body: undefined });
	});

	it("testCredential issues POST /credentials/{id}/test and returns the queued run/job ids (issue #323, 202 shape)", async () => {
		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			calls.push({ url, method: init?.method ?? "GET", body: init?.body ? JSON.parse(init.body as string) : undefined });
			return jsonResponse({ run_id: "run-1", job_id: "job-1" }, 202);
		}) as unknown as typeof fetch;

		const result = await testCredential("cred-1");
		expect(calls[0]).toEqual({ url: "/api/v1/credentials/cred-1/test", method: "POST", body: undefined });
		expect(result).toEqual({ run_id: "run-1", job_id: "job-1" });
	});
});

describe("formatHealth", () => {
	it("maps every closed-set health value to a human label", () => {
		expect(formatHealth("valid")).toBe("valid");
		expect(formatHealth("auth_failing")).toBe("auth failing");
		expect(formatHealth("unknown")).toBe("unknown");
	});

	it("passes through an unrecognized value verbatim", () => {
		expect(formatHealth("something_new")).toBe("something_new");
	});
});

describe("formatTimestamp", () => {
	it("formats an ISO timestamp to the app's short UTC form", () => {
		expect(formatTimestamp("2026-08-01T12:34:56Z")).toBe("2026-08-01 12:34Z");
	});

	it("returns an em dash for missing or invalid input", () => {
		expect(formatTimestamp(undefined)).toBe("—");
		expect(formatTimestamp(null)).toBe("—");
		expect(formatTimestamp("not a date")).toBe("—");
	});
});
