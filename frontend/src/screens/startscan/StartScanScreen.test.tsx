/**
 * StartScanScreen — the five-step Start-a-Scan wizard (issue #284; #587
 * epic #582's final slice reworked the credential step to default to
 * target-assigned bindings with per-target/per-purpose overrides).
 *
 * Covers: the default assigned-credentials path with fully-bound targets
 * (no credential input touched), the coverage summary for a target missing
 * a required binding, saved and ad hoc per-target/per-purpose overrides,
 * ad hoc write-only clearing, a `credential_binding_gaps` 400 mapped onto
 * the credential step, bulk-apply compatibility gating, role gating (Cyber
 * vs. Operator for ad hoc), scope tree fallback, and API error surfacing.
 */
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { RouterProvider } from "../../lib/router";
import type { Target } from "../configuration/sites";
import { StartScanScreen } from "./StartScanScreen";

const SITES = [{ id: "site-1", name: "Alpha Enclave", description: "Primary", stigman_override: null, created_at: "", updated_at: "" }];

const BOUND_VSPHERE_TARGET: Target = {
	id: "target-1",
	site_id: "site-1",
	kind: "vsphere",
	name: "vcsa-01.example.internal",
	connection: "{}",
	credential_ref: "cred-1",
	discovery_status: "discovered",
	last_refreshed: "2026-08-01T00:00:00Z",
	created_at: "",
	updated_at: "",
	bindings: [
		{
			purpose: "vsphere-api",
			credential_ref: "cred-1",
			credential_name: "Alpha vCenter service account",
			credential_type: "vcenter",
			created_at: "",
			updated_at: "",
		},
	],
};

const BOUND_SSH_TARGET: Target = {
	id: "target-2",
	site_id: "site-1",
	kind: "ssh",
	name: "esx-01.example.internal",
	connection: "{}",
	credential_ref: "cred-2",
	discovery_status: "never_discovered",
	last_refreshed: null,
	created_at: "",
	updated_at: "",
	bindings: [
		{
			purpose: "srg-ssh",
			credential_ref: "cred-2",
			credential_name: "Alpha SSH service account",
			credential_type: "ssh",
			created_at: "",
			updated_at: "",
		},
	],
};

const UNBOUND_SSH_TARGET: Target = {
	...BOUND_SSH_TARGET,
	id: "target-3",
	name: "esx-02.example.internal",
	credential_ref: null,
	bindings: [],
};

const INVENTORY_WITH_ITEMS = {
	target_id: "target-1",
	discovery_status: "discovered",
	last_refreshed: "2026-08-01T00:00:00Z",
	stale: false,
	items: [
		{
			id: "cluster-1",
			type: "cluster",
			moref: "domain-c1",
			name: "Cluster-A",
			build: null,
			maintenance_mode: null,
			last_seen_at: "2026-08-01T00:00:00Z",
			removed: false,
			children: [
				{
					id: "host-1",
					type: "host",
					moref: "host-1",
					name: "esx-01a.example.internal",
					build: "23000000",
					maintenance_mode: false,
					last_seen_at: "2026-08-01T00:00:00Z",
					removed: false,
					children: [],
				},
			],
		},
	],
};

const INVENTORY_EMPTY = (targetId: string) => ({
	target_id: targetId,
	discovery_status: "never_discovered",
	last_refreshed: null,
	stale: true,
	items: [],
});

const CREDENTIAL_OPTIONS = [
	{ id: "cred-1", name: "Alpha vCenter service account", credential_type: "vcenter" },
	{ id: "cred-2", name: "Alpha SSH service account", credential_type: "ssh" },
	{ id: "cred-3", name: "Bravo vCenter service account", credential_type: "vcenter" },
	{ id: "cred-4", name: "Charlie NSX service account", credential_type: "nsx" },
];

const PROFILE_OPTIONS = [
	{ id: "profile-1", profile_key: "vmware/vsphere/vsphere8-vcenter-stig-baseline", name: "VMware vSphere 8.0 vCenter STIG", version: "1.1.0" },
];

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(body === undefined ? null : JSON.stringify(body), {
		status,
		headers: { "Content-Type": "application/json" },
	});
}

describe("StartScanScreen (issue #284, credential-binding rework issue #587)", () => {
	let originalFetch: typeof fetch;
	let fetchCalls: { url: string; init?: RequestInit }[];
	let targets: Target[];
	let runPostStatus: number;
	let runPostError: unknown;

	function installFetchMock(role: string) {
		fetchCalls = [];
		targets = [BOUND_VSPHERE_TARGET];
		runPostStatus = 202;
		runPostError = undefined;

		globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
			const url = typeof input === "string" ? input : input.toString();
			const method = init?.method ?? "GET";
			fetchCalls.push({ url, init });

			if (url === "/api/v1/sites" && method === "GET") {
				return jsonResponse(SITES);
			}
			if (url === "/api/v1/sites/site-1/targets" && method === "GET") {
				return jsonResponse(targets);
			}
			if (url === "/api/v1/targets/target-1/inventory" && method === "GET") {
				return jsonResponse(INVENTORY_WITH_ITEMS);
			}
			if (url.match(/^\/api\/v1\/targets\/target-\d+\/inventory$/) && method === "GET") {
				const targetId = url.split("/")[4];
				return jsonResponse(INVENTORY_EMPTY(targetId));
			}
			if (url === "/api/v1/credentials" && method === "GET") {
				return jsonResponse(CREDENTIAL_OPTIONS);
			}
			if (url === "/api/v1/profiles" && method === "GET") {
				return jsonResponse(PROFILE_OPTIONS);
			}
			if (url === "/api/v1/runs" && method === "POST") {
				if (runPostStatus !== 202) {
					return jsonResponse({ error: runPostError }, runPostStatus);
				}
				return jsonResponse({ run_id: "run-123" }, 202);
			}
			throw new Error(`unexpected fetch: ${method} ${url}`);
		}) as unknown as typeof fetch;

		window.sessionStorage.setItem(
			"waypoint.session",
			JSON.stringify({
				token: "tok-1",
				username: "j.moreno",
				role,
				expiresAt: new Date(Date.now() + 60_000).toISOString(),
			}),
		);
	}

	beforeEach(() => {
		originalFetch = globalThis.fetch;
		window.history.pushState(null, "", "/scan/new");
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
		window.sessionStorage.clear();
	});

	async function mount() {
		render(
			<RouterProvider>
				<AuthProvider>
					<StartScanScreen />
				</AuthProvider>
			</RouterProvider>,
		);
		await waitFor(() => expect(screen.getByText("Alpha Enclave")).toBeInTheDocument());
	}

	async function goToScope() {
		fireEvent.click(screen.getByText("Alpha Enclave"));
		await waitFor(() => expect(screen.getByText("Next")).not.toBeDisabled());
		fireEvent.click(screen.getByText("Next"));
		await waitFor(() => expect(screen.getByText("Scope — inventory")).toBeInTheDocument());
		await waitFor(() => expect(screen.queryByText("Loading inventory…")).not.toBeInTheDocument());

		await waitFor(() => expect(screen.getByText("VMware vSphere 8.0 vCenter STIG (1.1.0)")).toBeInTheDocument());
		fireEvent.change(screen.getByRole("combobox"), { target: { value: "profile-1" } });
	}

	async function goToCredential() {
		await goToScope();
		fireEvent.click(screen.getByText("Next"));
		await waitFor(() => expect(screen.getByText("Coverage")).toBeInTheDocument());
	}

	it("defaults to 'Use credentials assigned to each target' and submits a fully-bound scan without any credential input", async () => {
		installFetchMock("Cyber");
		await mount();
		await goToCredential();

		// Default mode is selected — the radio for "assigned" is checked.
		expect((screen.getByText("Use credentials assigned to each target").closest("label")!.querySelector("input") as HTMLInputElement).checked).toBe(true);

		// Coverage shows the single required purpose resolved from the target's own binding.
		await waitFor(() => expect(screen.getByText("Assigned: Alpha vCenter service account")).toBeInTheDocument());
		expect(screen.queryByText(/Missing required binding/)).not.toBeInTheDocument();

		fireEvent.click(screen.getByText("Next")); // -> schedule
		fireEvent.click(screen.getByText("Next")); // -> confirm
		await waitFor(() => expect(screen.getByText("Start scan")).toBeInTheDocument());
		expect(screen.getByText("Start scan")).not.toBeDisabled();

		fireEvent.click(screen.getByText("Start scan"));
		await waitFor(() => expect(window.location.pathname + window.location.search).toBe("/live-jobs?run=run-123"));

		const call = fetchCalls.find((c) => c.url === "/api/v1/runs" && c.init?.method === "POST")!;
		const body = JSON.parse(call.init!.body as string) as Record<string, unknown>;
		expect(body.run_type).toBe("scan");
		// Legacy tiers are retired from the wizard — neither is ever sent.
		expect(body.credential_id).toBeUndefined();
		expect(body.credential).toBeUndefined();
		expect(body.credential_overrides).toBeUndefined();
		expect(body.ad_hoc_credentials).toBeUndefined();
	});

	it("shows a coverage gap and blocks submission when a selected target has no assigned binding", async () => {
		installFetchMock("Cyber");
		targets = [BOUND_VSPHERE_TARGET, UNBOUND_SSH_TARGET];
		await mount();
		await goToCredential();

		await waitFor(() => expect(screen.getByText(/Missing required binding/)).toBeInTheDocument());
		expect(screen.getByText("bind a credential for this target").closest("a")).toHaveAttribute("href", "/config");
		expect(screen.getByText(/1 required credential missing/)).toBeInTheDocument();

		fireEvent.click(screen.getByText("Next")); // -> schedule
		fireEvent.click(screen.getByText("Next")); // -> confirm
		await waitFor(() => expect(screen.getByText("Start scan")).toBeInTheDocument());
		expect(screen.getByText("Start scan")).toBeDisabled();
	});

	it("Cyber can apply a saved per-target/per-purpose override that resolves the gap", async () => {
		installFetchMock("Cyber");
		targets = [UNBOUND_SSH_TARGET];
		await mount();
		await goToCredential();

		await waitFor(() => expect(screen.getByText(/Missing required binding/)).toBeInTheDocument());

		fireEvent.click(screen.getByText("Customize per target/purpose"));
		await waitFor(() => expect(screen.getByLabelText(`${UNBOUND_SSH_TARGET.name} SRG SSH saved credential`)).toBeInTheDocument());

		fireEvent.change(screen.getByLabelText(`${UNBOUND_SSH_TARGET.name} SRG SSH saved credential`), { target: { value: "cred-2" } });

		await waitFor(() => expect(screen.getByText("Override: Alpha SSH service account")).toBeInTheDocument());
		expect(screen.queryByText(/Missing required binding/)).not.toBeInTheDocument();

		fireEvent.click(screen.getByText("Next"));
		fireEvent.click(screen.getByText("Next"));
		await waitFor(() => expect(screen.getByText("Start scan")).toBeInTheDocument());
		expect(screen.getByText("Start scan")).not.toBeDisabled();

		fireEvent.click(screen.getByText("Start scan"));
		await waitFor(() => expect(window.location.pathname + window.location.search).toBe("/live-jobs?run=run-123"));

		const call = fetchCalls.find((c) => c.url === "/api/v1/runs" && c.init?.method === "POST")!;
		const body = JSON.parse(call.init!.body as string) as { credential_overrides?: { target_id: string; purpose: string; credential_id: string }[] };
		expect(body.credential_overrides).toEqual([{ target_id: "target-3", purpose: "srg-ssh", credential_id: "cred-2" }]);
	});

	it("Cyber cannot enter an ad hoc override (Operator gate)", async () => {
		installFetchMock("Cyber");
		targets = [UNBOUND_SSH_TARGET];
		await mount();
		await goToCredential();
		fireEvent.click(screen.getByText("Customize per target/purpose"));

		await waitFor(() => expect(screen.getByText("Ad hoc credentials require Operator or higher.")).toBeInTheDocument());
		expect(screen.queryByLabelText("srg-ssh ad hoc username for this target")).not.toBeInTheDocument();
	});

	it("Operator can enter an ad hoc override; the secret is never echoed and is cleared from state after submit", async () => {
		installFetchMock("Operator");
		targets = [UNBOUND_SSH_TARGET];
		await mount();
		await goToCredential();
		fireEvent.click(screen.getByText("Customize per target/purpose"));

		await waitFor(() => expect(screen.getByLabelText("srg-ssh ad hoc username for this target")).toBeInTheDocument());
		fireEvent.change(screen.getByLabelText("srg-ssh ad hoc username for this target"), { target: { value: "j.moreno" } });
		fireEvent.change(screen.getByLabelText("srg-ssh ad hoc secret for this target"), { target: { value: "invented-wizard-secret-abc" } });

		await waitFor(() => expect(screen.getByText("Override: j.moreno (ad hoc)")).toBeInTheDocument());

		fireEvent.click(screen.getByText("Next"));
		fireEvent.click(screen.getByText("Next"));
		await waitFor(() => expect(screen.getByText("Start scan")).toBeInTheDocument());

		// The confirm summary never renders the raw secret anywhere on screen.
		expect(screen.queryByText("invented-wizard-secret-abc")).not.toBeInTheDocument();

		fireEvent.click(screen.getByText("Start scan"));
		await waitFor(() => expect(window.location.pathname + window.location.search).toBe("/live-jobs?run=run-123"));

		const call = fetchCalls.find((c) => c.url === "/api/v1/runs" && c.init?.method === "POST")!;
		const body = JSON.parse(call.init!.body as string) as {
			ad_hoc_credentials?: { target_id: string; purpose: string; username: string; secret: string }[];
		};
		expect(body.ad_hoc_credentials).toEqual([{ target_id: "target-3", purpose: "srg-ssh", username: "j.moreno", secret: "invented-wizard-secret-abc" }]);
	});

	it("clears an ad hoc override when the username is emptied back out", async () => {
		installFetchMock("Operator");
		targets = [UNBOUND_SSH_TARGET];
		await mount();
		await goToCredential();
		fireEvent.click(screen.getByText("Customize per target/purpose"));

		await waitFor(() => expect(screen.getByLabelText("srg-ssh ad hoc username for this target")).toBeInTheDocument());
		fireEvent.change(screen.getByLabelText("srg-ssh ad hoc username for this target"), { target: { value: "j.moreno" } });
		fireEvent.change(screen.getByLabelText("srg-ssh ad hoc secret for this target"), { target: { value: "secret-1" } });
		await waitFor(() => expect(screen.getByText("Override: j.moreno (ad hoc)")).toBeInTheDocument());

		fireEvent.change(screen.getByLabelText("srg-ssh ad hoc username for this target"), { target: { value: "" } });
		await waitFor(() => expect(screen.getByText(/Missing required binding/)).toBeInTheDocument());
	});

	it("bulk-applies a saved credential only to compatible (kind/purpose) targets", async () => {
		installFetchMock("Cyber");
		targets = [{ ...UNBOUND_SSH_TARGET, id: "target-3" }, { ...UNBOUND_SSH_TARGET, id: "target-4", name: "esx-03.example.internal" }];
		await mount();
		await goToCredential();
		fireEvent.click(screen.getByText("Customize per target/purpose"));

		await waitFor(() => expect(screen.getByLabelText("Bulk apply SRG SSH credential")).toBeInTheDocument());

		// The bulk-apply select for SRG SSH only lists ssh-typed credentials —
		// cred-1/cred-3 (vcenter) and cred-4 (nsx) are excluded (compatibility gate).
		const select = screen.getByLabelText("Bulk apply SRG SSH credential") as HTMLSelectElement;
		const optionLabels = Array.from(select.options).map((o) => o.textContent);
		expect(optionLabels).toContain("Alpha SSH service account");
		expect(optionLabels).not.toContain("Alpha vCenter service account");
		expect(optionLabels).not.toContain("Bravo vCenter service account");
		expect(optionLabels).not.toContain("Charlie NSX service account");

		fireEvent.change(select, { target: { value: "cred-2" } });

		await waitFor(() => expect(screen.getAllByText("Override: Alpha SSH service account")).toHaveLength(2));
	});

	it("maps a credential_binding_gaps 400 onto the credential step instead of a generic toast", async () => {
		installFetchMock("Cyber");
		await mount();
		await goToCredential();
		fireEvent.click(screen.getByText("Next"));
		fireEvent.click(screen.getByText("Next"));
		await waitFor(() => expect(screen.getByText("Start scan")).toBeInTheDocument());

		runPostStatus = 400;
		runPostError = {
			code: "credential_binding_gaps",
			message: "One or more targets are missing a required credential binding.",
			binding_gaps: [
				{ target_id: "target-1", target_name: "vcsa-01.example.internal", purpose: "vsphere-api", reason: "incompatible_credential_type" },
			],
		};
		fireEvent.click(screen.getByText("Start scan"));

		await waitFor(() => expect(screen.getByText("One or more targets are missing a required credential binding.")).toBeInTheDocument());

		// Go back to the credential step — the specific gap is mapped onto its (target, purpose) row.
		fireEvent.click(screen.getByText("Back")); // -> schedule
		fireEvent.click(screen.getByText("Back")); // -> credential
		await waitFor(() =>
			expect(screen.getByText("The selected credential's type is not compatible with this purpose.")).toBeInTheDocument(),
		);
	});

	it("scope tree falls back to a target-level checkbox when inventory is empty", async () => {
		installFetchMock("Cyber");
		targets = [BOUND_VSPHERE_TARGET, BOUND_SSH_TARGET];
		await mount();
		await goToScope();

		expect(screen.getByText("vcsa-01.example.internal")).toBeInTheDocument();
		expect(screen.getByText("esx-01.example.internal")).toBeInTheDocument();
		expect(screen.getByText("Cluster-A")).toBeInTheDocument();
		expect(screen.getByText("No cached inventory — scanning the whole target.")).toBeInTheDocument();
	});

	it("Confirm's Start scan button stays disabled until a profile is selected", async () => {
		installFetchMock("Cyber");
		await mount();

		fireEvent.click(screen.getByText("Alpha Enclave"));
		await waitFor(() => expect(screen.getByText("Next")).not.toBeDisabled());
		fireEvent.click(screen.getByText("Next")); // -> scope
		await waitFor(() => expect(screen.getByText("Scope — inventory")).toBeInTheDocument());
		await waitFor(() => expect(screen.getByText("VMware vSphere 8.0 vCenter STIG (1.1.0)")).toBeInTheDocument());
		// Deliberately no profile selection here.

		fireEvent.click(screen.getByText("Next")); // -> credential
		await waitFor(() => expect(screen.getByText("Coverage")).toBeInTheDocument());
		fireEvent.click(screen.getByText("Next")); // -> schedule
		fireEvent.click(screen.getByText("Next")); // -> confirm
		await waitFor(() => expect(screen.getByText("Start scan")).toBeInTheDocument());

		expect(screen.getByText("Start scan")).toBeDisabled();
	});

	it("surfaces a 403 from POST /runs on confirm", async () => {
		installFetchMock("Cyber");
		await mount();
		await goToCredential();
		fireEvent.click(screen.getByText("Next"));
		fireEvent.click(screen.getByText("Next"));
		await waitFor(() => expect(screen.getByText("Start scan")).toBeInTheDocument());

		runPostStatus = 403;
		runPostError = { code: "forbidden", message: "Ad hoc credentials require the Operator role." };
		fireEvent.click(screen.getByText("Start scan"));

		await waitFor(() => expect(screen.getByText("Ad hoc credentials require the Operator role.")).toBeInTheDocument());
	});

	it("surfaces a 404 from POST /runs on confirm", async () => {
		installFetchMock("Cyber");
		await mount();
		await goToCredential();
		fireEvent.click(screen.getByText("Next"));
		fireEvent.click(screen.getByText("Next"));
		await waitFor(() => expect(screen.getByText("Start scan")).toBeInTheDocument());

		runPostStatus = 404;
		runPostError = { code: "not_found", message: "Site 'site-1' does not exist." };
		fireEvent.click(screen.getByText("Start scan"));

		await waitFor(() => expect(screen.getByText("Site 'site-1' does not exist.")).toBeInTheDocument());
	});

	it("does not double-submit on a rapid double click", async () => {
		installFetchMock("Cyber");
		await mount();
		await goToCredential();
		fireEvent.click(screen.getByText("Next"));
		fireEvent.click(screen.getByText("Next"));
		await waitFor(() => expect(screen.getByText("Start scan")).toBeInTheDocument());

		const button = screen.getByText("Start scan");
		fireEvent.click(button);
		fireEvent.click(button);

		await waitFor(() => expect(window.location.pathname + window.location.search).toBe("/live-jobs?run=run-123"));
		expect(fetchCalls.filter((c) => c.url === "/api/v1/runs" && c.init?.method === "POST")).toHaveLength(1);
	});

	it("Viewer sees the flow gated (visible but disabled), no data fetched", async () => {
		installFetchMock("Viewer");
		render(
			<RouterProvider>
				<AuthProvider>
					<StartScanScreen />
				</AuthProvider>
			</RouterProvider>,
		);
		await waitFor(() => expect(screen.getByText(/Starting a scan requires Cyber or higher/)).toBeInTheDocument());
		expect(fetchCalls.some((c) => c.url === "/api/v1/sites")).toBe(false);
	});
});
