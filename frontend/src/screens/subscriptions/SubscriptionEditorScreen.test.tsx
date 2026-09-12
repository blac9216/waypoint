/**
 * SubscriptionEditorScreen — issue #1473. Proves the editor renders against
 * #1450's documented `SubscriptionsController` response shape
 * (`SubscriptionResponse`, snake_case on the wire) and covers each
 * acceptance criterion: the line picker lists indexed catalog metadata and
 * supports add/remove, the projected-size display updates as the selection
 * changes, create and edit both save through the subscription API and a
 * saved subscription round-trips into edit mode, and adopting a preset
 * pre-fills the editor.
 */
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import { RouterProvider } from "../../lib/router";
import { SubscriptionEditorScreen } from "./SubscriptionEditorScreen";

const ARTIFACTS = [
	{ id: "a1", name: "vcsa-8.0.3.rpm", sha256: "s1", product: "VCENTER", version: "8.0.3.9100", size_bytes: 1_000_000, status: "indexed" },
	{ id: "a2", name: "vcsa-8.0.3b.rpm", sha256: "s2", product: "VCENTER", version: "8.0.3.9200", size_bytes: 2_000_000, status: "indexed" },
	{ id: "a3", name: "vcsa-8.0.2.rpm", sha256: "s3", product: "VCENTER", version: "8.0.2.9100", size_bytes: 4_000_000, status: "indexed" },
];

const SUBSCRIPTION = {
	id: "sub-1",
	product: "VCENTER",
	lane: "depot",
	line_granularity: "subminor",
	anchor_version: "8.0.3.9100",
	preset_id: null,
	refresh_window_days: null,
	retention_override_days: null,
	is_enabled: true,
	created_at: "2026-09-01T00:00:00Z",
	updated_at: "2026-09-01T00:00:00Z",
};

const PRESET = {
	id: "preset-1",
	stack: "VCF",
	generation: "8.0",
	name: "VCF 8.0 baseline",
	line_granularity: "minor",
	anchor_version: "8.0",
	is_custom: false,
	source_preset_id: null,
	created_at: "2026-09-01T00:00:00Z",
	updated_at: "2026-09-01T00:00:00Z",
};

function jsonResponse(body: unknown, status = 200, headers?: Record<string, string>): Response {
	return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ...headers } });
}

interface MockOptions {
	subscription?: unknown;
	preset?: unknown;
	postStatus?: number;
	postBody?: unknown;
}

function installFetchMock(options?: MockOptions) {
	globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
		const url = typeof input === "string" ? input : input.toString();
		const method = init?.method ?? "GET";

		if (url.startsWith("/api/v1/catalog/artifacts")) {
			return jsonResponse(ARTIFACTS, 200, { "X-Total-Count": String(ARTIFACTS.length) });
		}
		if (url === "/api/v1/subscriptions/sub-1" && method === "GET") {
			return jsonResponse(options?.subscription ?? SUBSCRIPTION);
		}
		if (url === "/api/v1/presets/preset-1" && method === "GET") {
			return jsonResponse(options?.preset ?? PRESET);
		}
		if (url === "/api/v1/subscriptions" && method === "POST") {
			return jsonResponse(options?.postBody ?? { ...SUBSCRIPTION, id: "sub-new" }, options?.postStatus ?? 201);
		}
		if (url === "/api/v1/subscriptions/sub-1" && method === "PUT") {
			return jsonResponse(options?.postBody ?? { ...SUBSCRIPTION, is_enabled: false });
		}
		throw new Error(`Unhandled fetch in test: ${method} ${url}`);
	}) as unknown as typeof fetch;
}

function renderWithProviders(role: "Viewer" | "Cyber" | "Operator" | "Admin" = "Admin") {
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
			<RouterProvider>
				<SubscriptionEditorScreen />
			</RouterProvider>
		</AuthProvider>,
	);
}

describe("SubscriptionEditorScreen", () => {
	beforeEach(() => {
		sessionStorage.clear();
	});

	afterEach(() => {
		vi.restoreAllMocks();
	});

	it("blank create: lists indexed catalog products, picks a version, and projected size updates on selection change", async () => {
		window.history.pushState(null, "", "/subscriptions/new");
		installFetchMock();
		renderWithProviders();

		await waitFor(() => expect(screen.getByLabelText("Product")).toBeInTheDocument());

		fireEvent.change(screen.getByLabelText("Product"), { target: { value: "VCENTER" } });
		expect(screen.getByText("Projected size: 0 B")).toBeInTheDocument();

		fireEvent.change(screen.getByLabelText("Anchor version"), { target: { value: "8.0.3.9100" } });
		// subminor granularity (default form state is "minor" — set it to
		// subminor so only the two 8.0.3.* artifacts are in-line, not the
		// 8.0.2.* one too).
		fireEvent.change(screen.getByLabelText("Line granularity"), { target: { value: "subminor" } });

		await waitFor(() => expect(screen.getByTestId("projected-size")).toHaveTextContent("2.9 MiB"));

		fireEvent.change(screen.getByLabelText("Anchor version"), { target: { value: "8.0.2.9100" } });
		await waitFor(() => expect(screen.getByTestId("projected-size")).toHaveTextContent("3.8 MiB"));
	});

	it("remove line clears the current product/version pick", async () => {
		window.history.pushState(null, "", "/subscriptions/new");
		installFetchMock();
		renderWithProviders();

		await waitFor(() => expect(screen.getByLabelText("Product")).toBeInTheDocument());
		fireEvent.change(screen.getByLabelText("Product"), { target: { value: "VCENTER" } });
		fireEvent.change(screen.getByLabelText("Anchor version"), { target: { value: "8.0.3.9100" } });

		fireEvent.click(screen.getByText("Remove line"));

		expect((screen.getByLabelText("Product") as HTMLInputElement).value).toBe("");
		expect((screen.getByLabelText("Anchor version") as HTMLInputElement).value).toBe("");
	});

	it("create mode saves via POST /subscriptions and shows Saved", async () => {
		window.history.pushState(null, "", "/subscriptions/new");
		installFetchMock({ postBody: { ...SUBSCRIPTION, id: "sub-new" } });
		renderWithProviders();

		await waitFor(() => expect(screen.getByLabelText("Product")).toBeInTheDocument());
		fireEvent.change(screen.getByLabelText("Product"), { target: { value: "VCENTER" } });
		fireEvent.change(screen.getByLabelText("Anchor version"), { target: { value: "8.0.3.9100" } });

		const postSpy = vi.spyOn(globalThis, "fetch");
		fireEvent.click(screen.getByText("Save"));

		await waitFor(() =>
			expect(postSpy).toHaveBeenCalledWith("/api/v1/subscriptions", expect.objectContaining({ method: "POST" })),
		);
		await waitFor(() => expect(screen.getByText("Saved.")).toBeInTheDocument());
	});

	it("edit mode loads GET /subscriptions/{id} and its prior selection round-trips into the form", async () => {
		window.history.pushState(null, "", "/subscriptions/new?id=sub-1");
		installFetchMock();
		renderWithProviders();

		await waitFor(() => expect((screen.getByLabelText("Product") as HTMLInputElement).value).toBe("VCENTER"));
		expect((screen.getByLabelText("Anchor version") as HTMLInputElement).value).toBe("8.0.3.9100");
		expect((screen.getByLabelText("Line granularity") as HTMLSelectElement).value).toBe("subminor");

		const putSpy = vi.spyOn(globalThis, "fetch");
		fireEvent.click(screen.getByText("Save"));

		await waitFor(() =>
			expect(putSpy).toHaveBeenCalledWith("/api/v1/subscriptions/sub-1", expect.objectContaining({ method: "PUT" })),
		);
	});

	it("adopt-a-preset (?preset=) pre-fills line granularity/anchor version from the preset and disables them", async () => {
		window.history.pushState(null, "", "/subscriptions/new?preset=preset-1");
		installFetchMock();
		renderWithProviders();

		await waitFor(() => expect((screen.getByLabelText("Anchor version") as HTMLInputElement).value).toBe("8.0"));
		expect((screen.getByLabelText("Line granularity") as HTMLSelectElement).value).toBe("minor");
		expect((screen.getByLabelText("Anchor version") as HTMLInputElement).disabled).toBe(true);
		expect((screen.getByLabelText("Line granularity") as HTMLSelectElement).disabled).toBe(true);

		const postSpy = vi.spyOn(globalThis, "fetch");
		fireEvent.change(screen.getByLabelText("Product"), { target: { value: "VCENTER" } });
		fireEvent.click(screen.getByText("Save"));

		await waitFor(() =>
			expect(postSpy).toHaveBeenCalledWith(
				"/api/v1/subscriptions",
				expect.objectContaining({
					method: "POST",
					body: expect.stringContaining(`"preset_id":"preset-1"`),
				}),
			),
		);
	});

	it("disables Save for a Viewer, with a reason", async () => {
		window.history.pushState(null, "", "/subscriptions/new");
		installFetchMock();
		renderWithProviders("Viewer");

		await waitFor(() => expect(screen.getByLabelText("Product")).toBeInTheDocument());
		const saveButton = screen.getByText("Save") as HTMLButtonElement;
		expect(saveButton.disabled).toBe(true);
		expect(saveButton.title).toMatch(/Admin/);
	});
});
