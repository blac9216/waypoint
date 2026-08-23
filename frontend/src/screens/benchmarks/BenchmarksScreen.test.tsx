/**
 * BenchmarksScreen — issue #559. Covers the acceptance criteria: profile
 * inventory listing + "no content installed" distinction, Global/Site/Target
 * layer tabs, version history + read-only prior-version inspect, effective
 * resolution (winning layer + source version, and the expired-attestation
 * "control remains Open" explanation), Admin-only create-new-version editing
 * with inline validation errors that never lose the draft, and Viewer
 * (read-only) vs Admin (writer) role gating.
 */
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { BenchmarksScreen } from "./BenchmarksScreen";

const SITES = [{ id: "site-1", name: "Alpha Site", description: null, stigman_override: null, created_at: "2026-01-01T00:00:00Z", updated_at: "2026-01-01T00:00:00Z" }];

const TARGETS = [
	{
		id: "target-1",
		site_id: "site-1",
		kind: "vsphere",
		name: "vcsa-01.example.internal",
		connection: '{"host":"vcsa-01.example.internal"}',
		credential_ref: null,
		discovery_status: "discovered",
		last_refreshed: null,
		created_at: "2026-01-01T00:00:00Z",
		updated_at: "2026-01-01T00:00:00Z",
	},
];

const PROFILES = [
	{
		id: "profile-uuid-1",
		profile_key: "vmware-vsphere-8.0-esxi-stig",
		name: "VMware vSphere 8.0 ESXi STIG",
		version: "V2R1",
		commit: "abcdef1234567890",
		state: "current",
		updated_at: "2026-08-01T00:00:00Z",
	},
];

const GLOBAL_INPUT_DOC = {
	id: "doc-1",
	kind: "input",
	profile: "vmware-vsphere-8.0-esxi-stig",
	layer: "global",
	version: 2,
	author: "j.moreno",
	body: "syslog_host: syslog.example.internal\n",
	created_at: "2026-07-01T00:00:00Z",
	updated_at: "2026-08-01T00:00:00Z",
};

const GLOBAL_INPUT_VERSIONS = [
	{ version: 1, author: "j.moreno", body: "syslog_host: old.example.internal\n", created_at: "2026-07-01T00:00:00Z" },
	{ version: 2, author: "j.moreno", body: "syslog_host: syslog.example.internal\n", created_at: "2026-08-01T00:00:00Z" },
];

function jsonResponse(body: unknown, status = 200): Response {
	return new Response(body === undefined ? null : JSON.stringify(body), {
		status,
		headers: { "Content-Type": "application/json" },
	});
}

interface MockOptions {
	profiles?: typeof PROFILES;
	configDocs?: unknown[];
	resolution?: unknown[];
	onSave?: (url: string, body: unknown) => Response | undefined;
}

function installFetchMock(options: MockOptions = {}) {
	const profiles = options.profiles ?? PROFILES;
	const configDocs = options.configDocs ?? [GLOBAL_INPUT_DOC];

	globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
		const url = typeof input === "string" ? input : input.toString();
		const method = init?.method ?? "GET";

		if (url === "/api/v1/profiles") {
			return jsonResponse(profiles);
		}
		if (url === "/api/v1/sites") {
			return jsonResponse(SITES);
		}
		if (url === "/api/v1/sites/site-1/targets") {
			return jsonResponse(TARGETS);
		}
		if (url.startsWith("/api/v1/config-docs/resolve")) {
			return jsonResponse(options.resolution ?? []);
		}
		if (url.startsWith("/api/v1/config-docs?")) {
			const params = new URLSearchParams(url.split("?")[1]);
			const kind = params.get("kind");
			return jsonResponse(configDocs.filter((d) => (d as { kind: string }).kind === kind));
		}
		if (url === "/api/v1/config-docs/doc-1/versions") {
			return jsonResponse(GLOBAL_INPUT_VERSIONS);
		}
		if (url.match(/^\/api\/v1\/config-docs\/doc-1\/versions\/\d+$/)) {
			const version = Number(url.split("/").pop());
			const found = GLOBAL_INPUT_VERSIONS.find((v) => v.version === version);
			return found ? jsonResponse(found) : new Response("not found", { status: 404 });
		}
		if (method === "PUT" && url.match(/^\/api\/v1\/config-docs\//)) {
			const body: unknown = init?.body ? JSON.parse(init.body as string) : undefined;
			const custom = options.onSave?.(url, body);
			if (custom) {
				return custom;
			}
			return jsonResponse({
				...GLOBAL_INPUT_DOC,
				body: (body as { body: string }).body,
				version: GLOBAL_INPUT_DOC.version + 1,
			});
		}
		if (url === "/api/v1/auth/me") {
			return jsonResponse({ username: "j.moreno", role: "Admin" });
		}
		throw new Error(`Unhandled fetch in test: ${method} ${url}`);
	}) as unknown as typeof fetch;
}

function renderWithAuth(role: "Viewer" | "Cyber" | "Operator" | "Admin" = "Admin") {
	sessionStorage.setItem(
		"waypoint.session",
		JSON.stringify({
			token: "tok",
			username: "j.moreno",
			role,
			expiresAt: new Date(Date.now() + 3600_000).toISOString(),
		}),
	);
	return render(
		<AuthProvider>
			<BenchmarksScreen />
		</AuthProvider>,
	);
}

describe("BenchmarksScreen", () => {
	beforeEach(() => {
		sessionStorage.clear();
		window.history.pushState(null, "", "/benchmarks");
	});

	afterEach(() => {
		vi.restoreAllMocks();
	});

	it("lists installed profiles with source/version metadata and lets an operator select one", async () => {
		installFetchMock();
		renderWithAuth();

		await waitFor(() => expect(screen.getAllByText("VMware vSphere 8.0 ESXi STIG")[0]).toBeInTheDocument());
		expect(screen.getByText(/V2R1/)).toBeInTheDocument();
		expect(screen.getByText(/abcdef12/)).toBeInTheDocument();
	});

	it("distinguishes no content installed from an empty benchmark", async () => {
		installFetchMock({ profiles: [] });
		renderWithAuth();

		await waitFor(() => expect(screen.getByText(/No compliance content installed/i)).toBeInTheDocument());
	});

	it("filters profiles by search term", async () => {
		installFetchMock();
		renderWithAuth();

		await waitFor(() => expect(screen.getAllByText("VMware vSphere 8.0 ESXi STIG")[0]).toBeInTheDocument());
		fireEvent.change(screen.getByPlaceholderText("Search profiles…"), { target: { value: "nsx" } });
		await waitFor(() => expect(screen.getByText(/No profiles match/)).toBeInTheDocument());
	});

	it("shows the Global layer's stored YAML and lets a writer inspect a prior version read-only", async () => {
		installFetchMock();
		renderWithAuth();

		await waitFor(() => expect(screen.getAllByText("VMware vSphere 8.0 ESXi STIG")[0]).toBeInTheDocument());
		fireEvent.click(screen.getAllByText("VMware vSphere 8.0 ESXi STIG")[0]);

		await waitFor(() => expect(screen.getByText(/syslog\.example\.internal/)).toBeInTheDocument());
		expect(screen.getByText(/v2 · j\.moreno/)).toBeInTheDocument();

		await waitFor(() => expect(screen.getByText("v1")).toBeInTheDocument());
		const v1Row = screen.getByText("v1").closest(".bench-layer__history-row") as HTMLElement;
		fireEvent.click(within(v1Row).getByText("Inspect"));
		await waitFor(() => expect(screen.getByText(/old\.example\.internal/)).toBeInTheDocument());
		// The read-only inspect view is clearly labeled and does not replace the
		// current version's own display above it.
		expect(screen.getByText(/Read-only — v1/)).toBeInTheDocument();
	});

	it("disables Site/Target layer tabs until a site/target is picked, and enables them once selected", async () => {
		installFetchMock();
		renderWithAuth();

		await waitFor(() => expect(screen.getAllByText("VMware vSphere 8.0 ESXi STIG")[0]).toBeInTheDocument());
		fireEvent.click(screen.getAllByText("VMware vSphere 8.0 ESXi STIG")[0]);
		await waitFor(() => expect(screen.getByText("Global")).toBeInTheDocument());

		const siteTab = screen.getByRole("button", { name: "Site" });
		expect(siteTab).toBeDisabled();

		fireEvent.change(screen.getByLabelText("Site"), { target: { value: "site-1" } });
		await waitFor(() => expect(screen.getByRole("button", { name: "Site — Alpha Site" })).not.toBeDisabled());
	});

	it("effective resolution shows which layer wins and the source version for a selected target", async () => {
		installFetchMock({
			resolution: [
				{
					kind: "input",
					profile: "vmware-vsphere-8.0-esxi-stig",
					layer: "global",
					body: "syslog_host: syslog.example.internal\n",
					doc_id: "doc-1",
					version: 2,
					author: "j.moreno",
					updated_at: "2026-08-01T00:00:00Z",
					attestation_expired: false,
					attestation_expires_at: null,
				},
				{ kind: "attestation", profile: "vmware-vsphere-8.0-esxi-stig", layer: null, body: null, doc_id: null, version: null, author: null, updated_at: null, attestation_expired: false, attestation_expires_at: null },
				{ kind: "remediation-input", profile: "vmware-vsphere-8.0-esxi-stig", layer: null, body: null, doc_id: null, version: null, author: null, updated_at: null, attestation_expired: false, attestation_expires_at: null },
			],
		});
		renderWithAuth();

		await waitFor(() => expect(screen.getAllByText("VMware vSphere 8.0 ESXi STIG")[0]).toBeInTheDocument());
		fireEvent.click(screen.getAllByText("VMware vSphere 8.0 ESXi STIG")[0]);
		fireEvent.change(await screen.findByLabelText("Site"), { target: { value: "site-1" } });
		fireEvent.change(await screen.findByLabelText("Target"), { target: { value: "target-1" } });

		const effective = await screen.findByText("EFFECTIVE");
		const panel = effective.closest(".bench-effective") as HTMLElement;
		await waitFor(() => expect(within(panel).getByText("Global")).toBeInTheDocument());
		expect(within(panel).getByText(/v2 · j\.moreno/)).toBeInTheDocument();
		expect(within(panel).getAllByText(/Nothing defined at any layer/).length).toBe(2);
	});

	it("shows expired attestations as visibly inactive with the control-remains-Open explanation", async () => {
		installFetchMock({
			resolution: [
				{ kind: "input", profile: "vmware-vsphere-8.0-esxi-stig", layer: null, body: null, doc_id: null, version: null, author: null, updated_at: null, attestation_expired: false, attestation_expires_at: null },
				{
					kind: "attestation",
					profile: "vmware-vsphere-8.0-esxi-stig",
					layer: "site:site-1",
					body: "controls: []\n",
					doc_id: "doc-2",
					version: 1,
					author: "j.moreno",
					updated_at: "2026-01-01T00:00:00Z",
					attestation_expired: true,
					attestation_expires_at: "2026-06-01T00:00:00Z",
				},
				{ kind: "remediation-input", profile: "vmware-vsphere-8.0-esxi-stig", layer: null, body: null, doc_id: null, version: null, author: null, updated_at: null, attestation_expired: false, attestation_expires_at: null },
			],
		});
		renderWithAuth();

		await waitFor(() => expect(screen.getAllByText("VMware vSphere 8.0 ESXi STIG")[0]).toBeInTheDocument());
		fireEvent.click(screen.getAllByText("VMware vSphere 8.0 ESXi STIG")[0]);
		fireEvent.change(await screen.findByLabelText("Site"), { target: { value: "site-1" } });
		fireEvent.change(await screen.findByLabelText("Target"), { target: { value: "target-1" } });

		await waitFor(() => expect(screen.getByText("EXPIRED — NOT APPLIED")).toBeInTheDocument());
		expect(screen.getByText(/control reports Open/)).toBeInTheDocument();
	});

	it("creates a new version through the API and reflects the updated body/version", async () => {
		installFetchMock();
		renderWithAuth();

		await waitFor(() => expect(screen.getAllByText("VMware vSphere 8.0 ESXi STIG")[0]).toBeInTheDocument());
		fireEvent.click(screen.getAllByText("VMware vSphere 8.0 ESXi STIG")[0]);
		await waitFor(() => expect(screen.getByText(/syslog\.example\.internal/)).toBeInTheDocument());

		fireEvent.click(screen.getByText("Create new version"));
		const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
		fireEvent.change(textarea, { target: { value: "syslog_host: new-syslog.example.internal\n" } });
		fireEvent.click(screen.getByText("Save new version"));

		await waitFor(() => expect(screen.getByText(/v3 · j\.moreno/)).toBeInTheDocument());
		expect(screen.getByText(/new-syslog\.example\.internal/)).toBeInTheDocument();
	});

	it("shows an inline validation error and never loses the operator's draft on save failure", async () => {
		installFetchMock({
			onSave: () =>
				jsonResponse({ error: { code: "invalid_yaml", message: "Document is not valid YAML: found character that cannot start any token." } }, 400),
		});
		renderWithAuth();

		await waitFor(() => expect(screen.getAllByText("VMware vSphere 8.0 ESXi STIG")[0]).toBeInTheDocument());
		fireEvent.click(screen.getAllByText("VMware vSphere 8.0 ESXi STIG")[0]);
		await waitFor(() => expect(screen.getByText(/syslog\.example\.internal/)).toBeInTheDocument());

		fireEvent.click(screen.getByText("Create new version"));
		const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
		const badDraft = "syslog_host: [unterminated";
		fireEvent.change(textarea, { target: { value: badDraft } });
		fireEvent.click(screen.getByText("Save new version"));

		await waitFor(() => expect(screen.getByText(/not valid YAML/)).toBeInTheDocument());
		// The save-failure region announces to screen readers (issue #557 convention,
		// mirrors SiteTargetsPanel.test.tsx's aria-live status assertion).
		expect(screen.getByText(/not valid YAML/)).toHaveAttribute("aria-live", "polite");
		// The draft is untouched — still in the editor, not reverted or cleared.
		expect((screen.getByRole("textbox") as HTMLTextAreaElement).value).toBe(badDraft);
	});

	it("gates writes for a Viewer: no create/edit buttons are enabled", async () => {
		installFetchMock();
		renderWithAuth("Viewer");

		await waitFor(() => expect(screen.getAllByText("VMware vSphere 8.0 ESXi STIG")[0]).toBeInTheDocument());
		fireEvent.click(screen.getAllByText("VMware vSphere 8.0 ESXi STIG")[0]);
		await waitFor(() => expect(screen.getByText(/syslog\.example\.internal/)).toBeInTheDocument());

		const createVersionBtn = screen.getByText("Create new version");
		expect(createVersionBtn).toBeDisabled();
		expect(createVersionBtn.title).toMatch(/Requires Admin/);
		expect(screen.getByText(/Viewer role: Viewer — read-only/)).toBeInTheDocument();
	});

	it("shows the writer role note for Admin", async () => {
		installFetchMock();
		renderWithAuth("Admin");

		await waitFor(() => expect(screen.getByText("Writer (Admin)")).toBeInTheDocument());
	});

	it("opens directly to a deep-linked profile from Results' Open in Benchmarks (issue #559)", async () => {
		window.history.pushState(null, "", "/benchmarks?profile=vmware-vsphere-8.0-esxi-stig&target=target-1");
		installFetchMock();
		renderWithAuth();

		await waitFor(() => expect(screen.getAllByText("VMware vSphere 8.0 ESXi STIG")[0]).toBeInTheDocument());
		// Deep-linked profile is auto-selected: its detail panel (with the
		// profile_key) renders without any further click.
		await waitFor(() => expect(screen.getByText("vmware-vsphere-8.0-esxi-stig")).toBeInTheDocument());
	});
});
