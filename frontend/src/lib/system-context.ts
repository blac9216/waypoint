import { createContext, useContext } from "react";
import type { ModeState } from "./routes";

/**
 * One runner process's last-known liveness (issue #443,
 * `backend/Waypoint.Api/Contracts/SystemContracts.cs` `SystemRunnerStatusResponse`).
 * `available` is the derived verdict to key an indicator on — already folds in
 * both "did this worker report itself ready" and "has its heartbeat gone
 * stale"; nothing on the frontend needs to re-derive staleness from
 * `last_seen_at`.
 */
export interface SystemRunnerStatus {
	worker_id: string;
	job_types: string[];
	available: boolean;
	last_seen_at: string;
}

/** `GET /api/v1/system` (docs/api-contract.md "System, users, audit"):
 * "Version/build, mode, uptime, disk usage by store, depot sync, update
 * availability." Only the chrome-relevant subset is modeled here — screens
 * that need disk usage / depot sync own their own fetch. `runners` (issue
 * #443) is the exception: the control plane no longer executes anything
 * itself, so per-domain runner availability is chrome-relevant the same way
 * `mode` is — it answers "can work I submit actually run", which belongs in
 * the always-on top bar next to the STIG Manager and deployment-mode
 * indicators, not buried in a screen that only some roles/routes visit. */
export interface SystemInfo {
	version: string;
	build: string;
	mode: "connected" | "disconnected";
	update_available: string | null;
	runners: SystemRunnerStatus[];
}

/**
 * The chrome-relevant subset of `GET /api/v1/stigman`'s
 * `StigManagerConnectionResponse` (`backend/Waypoint.Api/Contracts/StigManagerContracts.cs`,
 * PR #314) — a *stored configuration*, not a reachability signal. There is no
 * `connected` field on the wire (issue #316): the endpoint being configured
 * says nothing about whether STIG Manager is actually up right now. Live
 * reachability is a separate, side-effecting probe (`POST /stigman/test`,
 * Admin-only) that the always-on top-bar chrome must not fire on every page
 * load — both because a Viewer would 403 on it and because it is a real
 * network call, not something to run unprompted on startup. The Config →
 * STIG Manager tab (#312/#317) owns that live check via its own "Test"
 * button; this type only ever answers "is a connection configured".
 */
export interface StigmanStatus {
	endpoint: string;
	collection: string;
}

export interface SystemContextValue {
	system: SystemInfo | null;
	stigman: StigmanStatus | null;
	/** True once the first fetch attempt (success or failure) has resolved. */
	ready: boolean;
	/**
	 * Tri-state deployment mode (issue #82): `"unknown"` until `ready`,
	 * `"connected"`/`"disconnected"` from the resolved `SystemInfo`
	 * afterwards (a failed fetch settles to `"disconnected"`, matching the
	 * existing "never guess connected" fail-safe below). Anything that
	 * decides access or visibility for a `connectedOnly` route
	 * (`evaluateRouteAccess` in `./router`) must read THIS field, not
	 * `system?.mode ?? null` — that pattern conflated "known disconnected"
	 * with "not yet known" and was the root cause of #82 (a connected-only
	 * deep link denied before `GET /api/v1/system` could ever answer).
	 */
	mode: ModeState;
}

export const SystemContext = createContext<SystemContextValue | null>(null);

export function useSystem(): SystemContextValue {
	const ctx = useContext(SystemContext);
	if (!ctx) {
		throw new Error("useSystem must be used within a SystemProvider");
	}
	return ctx;
}
