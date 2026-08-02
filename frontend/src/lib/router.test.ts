import { describe, expect, it } from "vitest";
import { canAccessRoute, ROUTES, routeForKey } from "./router";

describe("canAccessRoute", () => {
	it("allows a Viewer onto read-only screens", () => {
		expect(canAccessRoute(routeForKey("dashboard"), "Viewer", "connected")).toBe(true);
		expect(canAccessRoute(routeForKey("results"), "Viewer", "connected")).toBe(true);
	});

	it("blocks a Viewer from a screen that requires a higher role", () => {
		expect(canAccessRoute(routeForKey("start-scan"), "Viewer", "connected")).toBe(false);
		expect(canAccessRoute(routeForKey("configuration"), "Viewer", "connected")).toBe(false);
	});

	it("allows Cyber onto Start a Scan but not Configuration", () => {
		expect(canAccessRoute(routeForKey("start-scan"), "Cyber", "connected")).toBe(true);
		expect(canAccessRoute(routeForKey("configuration"), "Cyber", "connected")).toBe(false);
	});

	it("only Admin reaches Configuration", () => {
		expect(canAccessRoute(routeForKey("configuration"), "Operator", "connected")).toBe(false);
		expect(canAccessRoute(routeForKey("configuration"), "Admin", "connected")).toBe(true);
	});

	it("mode-gates the Download Catalog route regardless of role: hidden, not just disabled", () => {
		// Even Admin cannot access it when the appliance is air-gapped — this
		// is the README's "mode-gating genuinely removes the nav item, because
		// the feature does not exist" distinction from role-gating.
		expect(canAccessRoute(routeForKey("catalog"), "Admin", "disconnected")).toBe(false);
		expect(canAccessRoute(routeForKey("catalog"), "Admin", null)).toBe(false);
		expect(canAccessRoute(routeForKey("catalog"), "Operator", "connected")).toBe(true);
	});

	it("still enforces role on the catalog route even when connected", () => {
		expect(canAccessRoute(routeForKey("catalog"), "Cyber", "connected")).toBe(false);
	});

	it("every route is reachable by Admin when connected (no route left permanently unreachable)", () => {
		for (const route of ROUTES) {
			expect(canAccessRoute(route, "Admin", "connected")).toBe(true);
		}
	});
});
