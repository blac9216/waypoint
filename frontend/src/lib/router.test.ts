import { describe, expect, it } from "vitest";
import { evaluateRouteAccess, ROUTES, routeForKey } from "./router";

describe("evaluateRouteAccess", () => {
	it("allows a Viewer onto read-only screens", () => {
		expect(evaluateRouteAccess(routeForKey("dashboard"), "Viewer", "connected")).toBe("allowed");
		expect(evaluateRouteAccess(routeForKey("results"), "Viewer", "connected")).toBe("allowed");
	});

	it("blocks a Viewer from a screen that requires a higher role", () => {
		expect(evaluateRouteAccess(routeForKey("start-scan"), "Viewer", "connected")).toBe("denied");
		expect(evaluateRouteAccess(routeForKey("configuration"), "Viewer", "connected")).toBe("denied");
	});

	it("allows Cyber onto Start a Scan but not Configuration", () => {
		expect(evaluateRouteAccess(routeForKey("start-scan"), "Cyber", "connected")).toBe("allowed");
		expect(evaluateRouteAccess(routeForKey("configuration"), "Cyber", "connected")).toBe("denied");
	});

	it("only Admin reaches Configuration", () => {
		expect(evaluateRouteAccess(routeForKey("configuration"), "Operator", "connected")).toBe("denied");
		expect(evaluateRouteAccess(routeForKey("configuration"), "Admin", "connected")).toBe("allowed");
	});

	it("mode-gates the Download Catalog route regardless of role: denied outright, not just disabled", () => {
		// Even Admin cannot access it when the appliance is air-gapped — this
		// is the README's "mode-gating genuinely removes the nav item, because
		// the feature does not exist" distinction from role-gating.
		expect(evaluateRouteAccess(routeForKey("catalog"), "Admin", "disconnected")).toBe("denied");
		expect(evaluateRouteAccess(routeForKey("catalog"), "Operator", "connected")).toBe("allowed");
	});

	it("still enforces role on the catalog route even when connected", () => {
		expect(evaluateRouteAccess(routeForKey("catalog"), "Cyber", "connected")).toBe("denied");
	});

	/**
	 * The issue #82 regression coverage: `mode: "unknown"` must produce
	 * `"pending"` for a connectedOnly route — never `"allowed"` (would let a
	 * disconnected appliance briefly serve a connected-only screen) and never
	 * `"denied"` (the original bug: a route mode will turn out to allow gets
	 * bounced before the answer is known).
	 */
	describe("mode: \"unknown\" (tri-state, issue #82)", () => {
		it("returns \"pending\", not \"denied\", for a connectedOnly route while mode is unknown", () => {
			expect(evaluateRouteAccess(routeForKey("catalog"), "Admin", "unknown")).toBe("pending");
			expect(evaluateRouteAccess(routeForKey("catalog"), "Operator", "unknown")).toBe("pending");
		});

		it("still denies outright — never pending — when role alone is already insufficient", () => {
			// Role is checked first and needs no async state: an unprivileged
			// role is settled immediately, even before mode is known.
			expect(evaluateRouteAccess(routeForKey("catalog"), "Cyber", "unknown")).toBe("denied");
			expect(evaluateRouteAccess(routeForKey("catalog"), "Viewer", "unknown")).toBe("denied");
		});

		it("does not affect a route that isn't connectedOnly — mode is irrelevant to it", () => {
			expect(evaluateRouteAccess(routeForKey("dashboard"), "Viewer", "unknown")).toBe("allowed");
			expect(evaluateRouteAccess(routeForKey("configuration"), "Admin", "unknown")).toBe("allowed");
		});
	});

	it("every route is reachable by Admin when connected (no route left permanently unreachable)", () => {
		for (const route of ROUTES) {
			expect(evaluateRouteAccess(route, "Admin", "connected")).toBe("allowed");
		}
	});
});
