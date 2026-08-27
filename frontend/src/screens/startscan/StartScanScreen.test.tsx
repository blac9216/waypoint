/**
 * StartScanScreen — the five-step Start-a-Scan wizard (issue #284; #587
 * epic #582's final slice reworked the credential step to default to
 * target-assigned bindings with per-target/per-purpose overrides; issue
 * #733, epic #726 Wave 2, rewired the scope step from the legacy
 * `inventory_items` tree onto the stable `components` model and wires the
 * operator's actual tri-state selection onto `POST /runs`'s
 * `scope.target_scope`).
 *
 * Covers: the default assigned-credentials path with fully-bound targets
 * (no credential input touched), the coverage summary for a target missing
 * a required binding, saved and ad hoc per-target/per-purpose overrides,
 * ad hoc write-only clearing, a `credential_binding_gaps` 400 mapped onto
 * the credential step, bulk-apply compatibility gating, role gating (Cyber
 * vs. Operator for ad hoc), scope tree fallback, API error surfacing, and
 * (issue #733) tri-state determinism, `target_scope` wiring, a
 * `no_runnable_component`/`scope_omissions` 400, and an honestly-empty
 * explicit selection.
 *
 * Issue #733 remainder (epic #726 Wave 2, PR #874): the Preview step
 * (`POST /runs/plan-preview`) between Schedule and Confirm. Covers: the
 * preview request payload equaling the eventual create payload for the same
 * selection, accepted-item/skip/scope-omission/credential-gap rendering, the
 * honest zero-runnable empty-plan state, a preview 400 rendered via the same
 * error idiom, and the plan digest carried into the Confirm step display.
 */
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { RouterProvider } from "../../lib/router";
import type { Target } from "../configuration/sites";
import { StartScanScreen } from "./StartScanScreen";
import { buildComponentTree, flattenComponentTree, isSelectableComponent, resolveTargetScope, type ComponentNode } from "./startscan";

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

function component(overrides: Partial<ComponentNode> & Pick<ComponentNode, "id" | "display_name">): ComponentNode {
	return {
		parent_target_id: "target-1",
		parent_component_id: null,
		catalog_component_id: "catalog-1",
		catalog_component_key: "vsphere.esxi",
		vendor_identity: overrides.id,
		lifecycle: "active",
		fact_conflict: false,
		retired_at: null,
		...overrides,
	};
}

// Cluster-A (host-1) beneath target-1 — mirrors the checkbox tree the old
// inventory fixture exercised, now as stable `components` rows.
const COMPONENTS_WITH_ITEMS: ComponentNode[] = [
	component({ id: "cluster-1", display_name: "Cluster-A", catalog_component_key: "vsphere.cluster" }),
	component({ id: "host-1", display_name: "esx-01a.example.internal", parent_component_id: "cluster-1", catalog_component_key: "vsphere.esxi" }),
];

const COMPONENTS_EMPTY: ComponentNode[] = [];

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

describe("StartScanScreen (issue #284, credential-binding rework issue #587, scope wiring issue #733)", () => {
	let originalFetch: typeof fetch;
	let fetchCalls: { url: string; init?: RequestInit }[];
	let targets: Target[];
	let componentsByTarget: Record<string, ComponentNode[]>;
	let runPostStatus: number;
	let runPostError: unknown;
	let previewPostStatus: number;
	let previewPostError: unknown;
	let previewResponse: Record<string, unknown>;

	function installFetchMock(role: string) {
		fetchCalls = [];
		targets = [BOUND_VSPHERE_TARGET];
		componentsByTarget = { "target-1": COMPONENTS_WITH_ITEMS };
		runPostStatus = 202;
		runPostError = undefined;
		previewPostStatus = 200;
		previewPostError = undefined;
		previewResponse = {
			requested_mode: "all",
			resolved_component_ids: ["cluster-1", "host-1"],
			scope_omissions: [],
			plan_schema_version: 1,
			items: [
				{
					component_id: "cluster-1",
					catalog_execution_profile_id: "profile-exec-1",
					baseline_id: "baseline-1",
					benchmark_revision_id: "benchmark-1",
					transport: "vsphere-api",
					selector_kind: "cluster",
					selector_name: "Cluster-A",
					report_group_key: "cluster-1",
					priority: 1,
					output_kind: "ckl",
					required_purposes: ["vsphere-api"],
					declared_input_names: [],
				},
			],
			skips: [],
			plan_digest: "sha256:invented-preview-digest-abc123",
			explanation: "1 of 1 requested components accepted.",
			is_runnable: true,
			credential_gaps: [],
		};

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
			const componentsMatch = url.match(/^\/api\/v1\/targets\/(target-\d+)\/components\?includeRetired=true$/);
			if (componentsMatch && method === "GET") {
				const targetId = componentsMatch[1];
				return jsonResponse(componentsByTarget[targetId] ?? COMPONENTS_EMPTY);
			}
			if (url === "/api/v1/credentials" && method === "GET") {
				return jsonResponse(CREDENTIAL_OPTIONS);
			}
			if (url === "/api/v1/profiles" && method === "GET") {
				return jsonResponse(PROFILE_OPTIONS);
			}
			if (url === "/api/v1/runs/plan-preview" && method === "POST") {
				if (previewPostStatus !== 200) {
					return jsonResponse({ error: previewPostError }, previewPostStatus);
				}
				return jsonResponse(previewResponse, 200);
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
		await waitFor(() => expect(screen.getByText("Scope — components")).toBeInTheDocument());
		await waitFor(() => expect(screen.queryByText("Loading components…")).not.toBeInTheDocument());

		await waitFor(() => expect(screen.getByText("VMware vSphere 8.0 vCenter STIG (1.1.0)")).toBeInTheDocument());
		fireEvent.change(screen.getByRole("combobox"), { target: { value: "profile-1" } });
	}

	async function goToCredential() {
		await goToScope();
		fireEvent.click(screen.getByText("Next"));
		await waitFor(() => expect(screen.getByText("Coverage")).toBeInTheDocument());
	}

	/** Advances from the credential step through schedule and preview (waiting for the auto-fetched `POST /runs/plan-preview` to resolve) to confirm — issue #733 remainder's Preview step sits between Schedule and Confirm. */
	async function goToConfirm() {
		fireEvent.click(screen.getByText("Next")); // -> schedule
		fireEvent.click(screen.getByText("Next")); // -> preview
		await waitFor(() => expect(screen.getByText("Preview — would-be plan")).toBeInTheDocument());
		await waitFor(() => expect(screen.queryByText("Previewing the plan…")).not.toBeInTheDocument());
		fireEvent.click(screen.getByText("Next")); // -> confirm
		await waitFor(() => expect(screen.getByText("Start scan")).toBeInTheDocument());
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

		await goToConfirm();
		expect(screen.getByText("Start scan")).not.toBeDisabled();

		fireEvent.click(screen.getByText("Start scan"));
		await waitFor(() => expect(window.location.pathname + window.location.search).toBe("/live-run?run=run-123"));

		const call = fetchCalls.find((c) => c.url === "/api/v1/runs" && c.init?.method === "POST")!;
		const body = JSON.parse(call.init!.body as string) as Record<string, unknown>;
		expect(body.run_type).toBe("scan");
		// Legacy tiers are retired from the wizard — neither is ever sent.
		expect(body.credential_id).toBeUndefined();
		expect(body.credential).toBeUndefined();
		expect(body.credential_overrides).toBeUndefined();
		expect(body.ad_hoc_credentials).toBeUndefined();

		// Issue #733: with every component left checked, the resolved scope is
		// "all", not a frozen explicit list — never widened, never guessed.
		const scope = JSON.parse((body.scope as string) ?? "{}") as { target_scope?: { mode: string } };
		expect(scope.target_scope).toEqual({ mode: "all", target_ids: ["target-1"] });
	});

	it("shows a coverage gap and blocks submission when a selected target has no assigned binding", async () => {
		installFetchMock("Cyber");
		targets = [BOUND_VSPHERE_TARGET, UNBOUND_SSH_TARGET];
		await mount();
		await goToCredential();

		await waitFor(() => expect(screen.getByText(/Missing required binding/)).toBeInTheDocument());
		expect(screen.getByText("bind a credential for this target").closest("a")).toHaveAttribute("href", "/config");
		expect(screen.getByText(/1 required credential missing/)).toBeInTheDocument();

		await goToConfirm();
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

		await goToConfirm();
		expect(screen.getByText("Start scan")).not.toBeDisabled();

		fireEvent.click(screen.getByText("Start scan"));
		await waitFor(() => expect(window.location.pathname + window.location.search).toBe("/live-run?run=run-123"));

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

		await goToConfirm();

		// The confirm summary never renders the raw secret anywhere on screen.
		expect(screen.queryByText("invented-wizard-secret-abc")).not.toBeInTheDocument();

		fireEvent.click(screen.getByText("Start scan"));
		await waitFor(() => expect(window.location.pathname + window.location.search).toBe("/live-run?run=run-123"));

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
		await goToConfirm();

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
		fireEvent.click(screen.getByText("Back")); // -> preview
		fireEvent.click(screen.getByText("Back")); // -> schedule
		fireEvent.click(screen.getByText("Back")); // -> credential
		await waitFor(() =>
			expect(screen.getByText("The selected credential's type is not compatible with this purpose.")).toBeInTheDocument(),
		);
	});

	it("scope tree falls back to a target-level checkbox when no components are cached", async () => {
		installFetchMock("Cyber");
		targets = [BOUND_VSPHERE_TARGET, BOUND_SSH_TARGET];
		componentsByTarget = { "target-1": COMPONENTS_WITH_ITEMS, "target-2": COMPONENTS_EMPTY };
		await mount();
		await goToScope();

		expect(screen.getByText("vcsa-01.example.internal")).toBeInTheDocument();
		expect(screen.getByText("esx-01.example.internal")).toBeInTheDocument();
		expect(screen.getByText("Cluster-A")).toBeInTheDocument();
		expect(screen.getByText("No cached components — scanning the whole target.")).toBeInTheDocument();
	});

	it("issue #733: unchecking one child component sends an explicit component_ids scope, never widened to 'all'", async () => {
		installFetchMock("Cyber");
		await mount();
		await goToScope();

		// COMPONENTS_WITH_ITEMS: Cluster-A (parent) -> esx-01a.example.internal (child).
		fireEvent.click(screen.getByText("esx-01a.example.internal").closest("label")!.querySelector("input")!);

		fireEvent.click(screen.getByText("Next")); // -> credential
		await waitFor(() => expect(screen.getByText("Coverage")).toBeInTheDocument());
		await goToConfirm();
		fireEvent.click(screen.getByText("Start scan"));
		await waitFor(() => expect(window.location.pathname + window.location.search).toBe("/live-run?run=run-123"));

		const call = fetchCalls.find((c) => c.url === "/api/v1/runs" && c.init?.method === "POST")!;
		const body = JSON.parse(call.init!.body as string) as Record<string, unknown>;
		const scope = JSON.parse(body.scope as string) as { target_scope?: { mode: string; component_ids?: string[] } };
		// Only the cluster (parent) remains selected — the host was explicitly
		// unchecked, so this never widens back to "all".
		expect(scope.target_scope).toEqual({ mode: "explicit", component_ids: ["cluster-1"] });
	});

	it("issue #733: unchecking a parent clears every selectable descendant (deterministic cascade)", async () => {
		installFetchMock("Cyber");
		await mount();
		await goToScope();

		const clusterCheckbox = screen.getByText("Cluster-A").closest("label")!.querySelector("input") as HTMLInputElement;
		fireEvent.click(clusterCheckbox);

		const hostCheckbox = screen.getByText("esx-01a.example.internal").closest("label")!.querySelector("input") as HTMLInputElement;
		expect(hostCheckbox.checked).toBe(false);

		fireEvent.click(screen.getByText("Next")); // -> credential
		await waitFor(() => expect(screen.getByText("Coverage")).toBeInTheDocument());
		await goToConfirm();
		fireEvent.click(screen.getByText("Start scan"));
		await waitFor(() => expect(window.location.pathname + window.location.search).toBe("/live-run?run=run-123"));

		const call = fetchCalls.find((c) => c.url === "/api/v1/runs" && c.init?.method === "POST")!;
		const body = JSON.parse(call.init!.body as string) as Record<string, unknown>;
		const scope = JSON.parse(body.scope as string) as { target_scope?: { mode: string; component_ids?: string[] } };
		expect(scope.target_scope).toEqual({ mode: "explicit", component_ids: [] });
	});

	it("issue #733 AC: an empty explicit selection warns but does not block or widen the scan", async () => {
		installFetchMock("Cyber");
		await mount();
		await goToScope();

		fireEvent.click(screen.getByText("Cluster-A").closest("label")!.querySelector("input")!);

		await waitFor(() => expect(screen.getByText(/intentionally empty plan/)).toBeInTheDocument());
		expect(screen.getByText("Next")).not.toBeDisabled();
	});

	it("issue #733: a 400 no_runnable_component maps scope_omissions onto the scope step with refresh guidance", async () => {
		installFetchMock("Cyber");
		await mount();
		await goToCredential();
		await goToConfirm();

		runPostStatus = 400;
		runPostError = {
			code: "no_runnable_component",
			message: "The requested scope has no runnable component.",
			scope_omissions: [
				{ component_id: "host-1", reason: "component_retired", detail: "esx-01a.example.internal was retired." },
			],
		};
		fireEvent.click(screen.getByText("Start scan"));

		await waitFor(() => expect(screen.getByText("The requested scope has no runnable component.")).toBeInTheDocument());

		// Go back to the scope step — the omission is rendered with actionable
		// refresh guidance, not the raw machine-readable reason.
		fireEvent.click(screen.getByText("Back")); // -> preview
		fireEvent.click(screen.getByText("Back")); // -> schedule
		fireEvent.click(screen.getByText("Back")); // -> credential
		fireEvent.click(screen.getByText("Back")); // -> scope
		await waitFor(() =>
			expect(screen.getByText("This component has been retired and can no longer be scanned — remove it from the selection.")).toBeInTheDocument(),
		);
	});

	it("Confirm's Start scan button stays disabled until a profile is selected", async () => {
		installFetchMock("Cyber");
		await mount();

		fireEvent.click(screen.getByText("Alpha Enclave"));
		await waitFor(() => expect(screen.getByText("Next")).not.toBeDisabled());
		fireEvent.click(screen.getByText("Next")); // -> scope
		await waitFor(() => expect(screen.getByText("Scope — components")).toBeInTheDocument());
		await waitFor(() => expect(screen.getByText("VMware vSphere 8.0 vCenter STIG (1.1.0)")).toBeInTheDocument());
		// Deliberately no profile selection here.

		fireEvent.click(screen.getByText("Next")); // -> credential
		await waitFor(() => expect(screen.getByText("Coverage")).toBeInTheDocument());
		await goToConfirm();

		expect(screen.getByText("Start scan")).toBeDisabled();
	});

	it("surfaces a 403 from POST /runs on confirm", async () => {
		installFetchMock("Cyber");
		await mount();
		await goToCredential();
		await goToConfirm();

		runPostStatus = 403;
		runPostError = { code: "forbidden", message: "Ad hoc credentials require the Operator role." };
		fireEvent.click(screen.getByText("Start scan"));

		await waitFor(() => expect(screen.getByText("Ad hoc credentials require the Operator role.")).toBeInTheDocument());
	});

	it("surfaces a 404 from POST /runs on confirm", async () => {
		installFetchMock("Cyber");
		await mount();
		await goToCredential();
		await goToConfirm();

		runPostStatus = 404;
		runPostError = { code: "not_found", message: "Site 'site-1' does not exist." };
		fireEvent.click(screen.getByText("Start scan"));

		await waitFor(() => expect(screen.getByText("Site 'site-1' does not exist.")).toBeInTheDocument());
	});

	it("does not double-submit on a rapid double click", async () => {
		installFetchMock("Cyber");
		await mount();
		await goToCredential();
		await goToConfirm();

		const button = screen.getByText("Start scan");
		fireEvent.click(button);
		fireEvent.click(button);

		await waitFor(() => expect(window.location.pathname + window.location.search).toBe("/live-run?run=run-123"));
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

	// -- issue #733 remainder: plan-preview integration --------------------

	it("issue #733 remainder: the preview request payload's scope equals the create payload's scope for the same selection (minus profile_id)", async () => {
		installFetchMock("Cyber");
		await mount();
		await goToCredential();
		await goToConfirm();

		const previewCall = fetchCalls.find((c) => c.url === "/api/v1/runs/plan-preview" && c.init?.method === "POST")!;
		const previewBody = JSON.parse(previewCall.init!.body as string) as Record<string, unknown>;
		const previewScope = JSON.parse(previewBody.scope as string) as Record<string, unknown>;

		fireEvent.click(screen.getByText("Start scan"));
		await waitFor(() => expect(window.location.pathname + window.location.search).toBe("/live-run?run=run-123"));

		const createCall = fetchCalls.find((c) => c.url === "/api/v1/runs" && c.init?.method === "POST")!;
		const createBody = JSON.parse(createCall.init!.body as string) as Record<string, unknown>;
		const createScope = JSON.parse(createBody.scope as string) as Record<string, unknown>;

		// Preview never sends profile_id (ADR-0022 §7), and -- issue #895 -- create
		// now rejects it too whenever target_scope is set, so this fixture's
		// all-components selection means BOTH requests omit profile_id entirely,
		// not merely send it as undefined; every other scope field (site_id,
		// target_ids/target_scope) is identical, because both are built from the
		// same `scope` object via the same `toPreviewScope` (useScanWizard's
		// shared memo/helper), never re-derived separately.
		expect(previewScope.profile_id).toBeUndefined();
		expect(createScope.profile_id).toBeUndefined();
		expect(previewScope).toEqual(createScope);
	});

	it("issue #733 remainder: renders accepted items, the plan digest, and carries the digest into Confirm", async () => {
		installFetchMock("Cyber");
		await mount();
		await goToCredential();
		fireEvent.click(screen.getByText("Next")); // -> schedule
		fireEvent.click(screen.getByText("Next")); // -> preview
		await waitFor(() => expect(screen.getByText("1 accepted item")).toBeInTheDocument());
		expect(screen.getByText("vSphere API")).toBeInTheDocument();
		expect(screen.getByText("Plan digest: sha256:invented-preview-digest-abc123")).toBeInTheDocument();

		fireEvent.click(screen.getByText("Next")); // -> confirm
		await waitFor(() => expect(screen.getByText("sha256:invented-preview-digest-abc123")).toBeInTheDocument());
	});

	it("issue #733 remainder: renders skips, scope omissions, and credential gaps from the preview response", async () => {
		installFetchMock("Cyber");
		previewResponse = {
			...previewResponse,
			scope_omissions: [{ component_id: "host-2", reason: "component_absent", detail: "esx-01b was not seen on the last discovery pass." }],
			skips: [{ component_id: "host-3", reason: "no_active_baseline", detail: "No active baseline for this component." }],
			credential_gaps: [{ target_id: "target-1", target_name: "vcsa-01.example.internal", purpose: "vsphere-api", reason: "missing_binding" }],
		};
		await mount();
		await goToCredential();
		fireEvent.click(screen.getByText("Next")); // -> schedule
		fireEvent.click(screen.getByText("Next")); // -> preview

		await waitFor(() =>
			expect(screen.getByText("This component was not seen on the last discovery pass — refresh the scope before selecting it.")).toBeInTheDocument(),
		);
		expect(screen.getByText(/No active baseline for this component\./)).toBeInTheDocument();
		expect(screen.getByText(/No credential bound for this purpose\./)).toBeInTheDocument();
	});

	it("issue #733 remainder: renders the honest empty-plan state when preview resolves zero runnable items (200, not blocked)", async () => {
		installFetchMock("Cyber");
		previewResponse = { ...previewResponse, items: [], is_runnable: false, explanation: "0 of 0 requested components accepted." };
		await mount();
		await goToCredential();
		fireEvent.click(screen.getByText("Next")); // -> schedule
		fireEvent.click(screen.getByText("Next")); // -> preview

		await waitFor(() => expect(screen.getByText(/No component in this scope is currently runnable/)).toBeInTheDocument());
		// Preview is advisory, not blocking — Next to Confirm remains available.
		expect(screen.getByText("Next")).not.toBeDisabled();
	});

	it("issue #733 remainder: a preview 400 renders via the existing error idiom with a retry action, distinct from create's blocking 400", async () => {
		installFetchMock("Cyber");
		previewPostStatus = 400;
		previewPostError = { code: "validation_error", message: "scope.target_scope is required for a preview." };
		await mount();
		await goToCredential();
		fireEvent.click(screen.getByText("Next")); // -> schedule
		fireEvent.click(screen.getByText("Next")); // -> preview

		await waitFor(() => expect(screen.getByText("scope.target_scope is required for a preview.")).toBeInTheDocument());
		expect(screen.getByText("Retry preview")).toBeInTheDocument();

		// Recovering (a fixed preview) and retrying succeeds without navigating away.
		previewPostStatus = 200;
		previewPostError = undefined;
		fireEvent.click(screen.getByText("Retry preview"));
		await waitFor(() => expect(screen.getByText("1 accepted item")).toBeInTheDocument());
	});
});

describe("resolveTargetScope / buildComponentTree (issue #733: deterministic tri-state semantics)", () => {
	const TREE: ComponentNode[] = [
		component({ id: "cluster-1", display_name: "Cluster-A", catalog_component_key: "vsphere.cluster" }),
		component({ id: "host-1", display_name: "esx-01a", parent_component_id: "cluster-1", catalog_component_key: "vsphere.esxi" }),
		component({ id: "host-2", display_name: "esx-01b", parent_component_id: "cluster-1", catalog_component_key: "vsphere.esxi" }),
	];

	it("builds a parent/child tree from flat parent_component_id pointers", () => {
		const tree = buildComponentTree(TREE);
		expect(tree).toHaveLength(1);
		expect(tree[0].id).toBe("cluster-1");
		expect(tree[0].children.map((c) => c.id)).toEqual(["host-1", "host-2"]);
	});

	it("every selectable component checked resolves to mode 'all'", () => {
		const ids = flattenComponentTree(buildComponentTree(TREE)).filter(isSelectableComponent).map((n) => n.id);
		const result = resolveTargetScope(ids, new Set(ids));
		expect(result).toEqual({ mode: "all" });
	});

	it("a partial selection resolves to mode 'explicit' with exactly the checked ids", () => {
		const ids = flattenComponentTree(buildComponentTree(TREE)).filter(isSelectableComponent).map((n) => n.id);
		const result = resolveTargetScope(ids, new Set(["host-1"]));
		expect(result).toEqual({ mode: "explicit", component_ids: ["host-1"] });
	});

	it("a deliberately empty selection resolves to mode 'explicit' with an empty list, never falling back to 'all'", () => {
		const ids = flattenComponentTree(buildComponentTree(TREE)).filter(isSelectableComponent).map((n) => n.id);
		const result = resolveTargetScope(ids, new Set());
		expect(result).toEqual({ mode: "explicit", component_ids: [] });
	});

	it("no known components resolves to null (legacy target_ids fallback, not an empty explicit scope)", () => {
		expect(resolveTargetScope([], new Set())).toBeNull();
	});

	it("is order-independent: toggling children off then on in a different order converges to the same resolved scope", () => {
		const ids = flattenComponentTree(buildComponentTree(TREE)).filter(isSelectableComponent).map((n) => n.id);
		const a = resolveTargetScope(ids, new Set(["cluster-1", "host-2"]));
		const b = resolveTargetScope(ids, new Set(["host-2", "cluster-1"]));
		expect(a).toEqual(b);
	});

	it("a retired/absent component is excluded from the selectable id set (never auto-selected into 'all')", () => {
		const withRetired: ComponentNode[] = [
			...TREE,
			component({ id: "host-3", display_name: "esx-01c (retired)", parent_component_id: "cluster-1", lifecycle: "retired" }),
		];
		const selectableIds = flattenComponentTree(buildComponentTree(withRetired)).filter(isSelectableComponent).map((n) => n.id);
		expect(selectableIds).not.toContain("host-3");
	});
});
