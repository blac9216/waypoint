import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { apiGet } from "./api";
import { useAuth } from "./auth";

/** `GET /api/v1/system` (docs/api-contract.md "System, users, audit"):
 * "Version/build, mode, uptime, disk usage by store, depot sync, update
 * availability." Only the chrome-relevant subset is modeled here — screens
 * that need disk usage / depot sync own their own fetch. */
export interface SystemInfo {
	version: string;
	build: string;
	mode: "connected" | "disconnected";
	update_available: string | null;
}

interface StigmanStatus {
	connected: boolean;
	endpoint: string | null;
	collection: string | null;
}

interface SystemContextValue {
	system: SystemInfo | null;
	stigman: StigmanStatus | null;
	/** True once the first fetch attempt (success or failure) has resolved. */
	ready: boolean;
}

const SystemContext = createContext<SystemContextValue | null>(null);

export function SystemProvider({ children }: { children: ReactNode }) {
	const { status } = useAuth();
	const [system, setSystem] = useState<SystemInfo | null>(null);
	const [stigman, setStigman] = useState<StigmanStatus | null>(null);
	const [ready, setReady] = useState(false);

	useEffect(() => {
		if (status !== "signed-in") {
			return;
		}
		let cancelled = false;

		async function load() {
			// `/system` and `/stigman` are independent per the contract; a
			// STIG Manager outage should never block the mode/version chrome.
			const [systemResult, stigmanResult] = await Promise.allSettled([
				apiGet<SystemInfo>("/system"),
				apiGet<StigmanStatus>("/stigman"),
			]);
			if (cancelled) {
				return;
			}
			// Deployment mode is a deploy-time fact (README "Layout Rules" /
			// "Interactions"), not something the UI can toggle. When the API is
			// unreachable we deliberately do NOT guess "connected" — an unknown
			// mode hides mode-gated nav (Download Catalog) rather than risk
			// showing a feature the appliance cannot actually serve.
			setSystem(systemResult.status === "fulfilled" ? systemResult.value : null);
			setStigman(stigmanResult.status === "fulfilled" ? stigmanResult.value : null);
			setReady(true);
		}

		void load();
		return () => {
			cancelled = true;
		};
	}, [status]);

	const value = useMemo(() => ({ system, stigman, ready }), [system, stigman, ready]);

	return <SystemContext.Provider value={value}>{children}</SystemContext.Provider>;
}

export function useSystem(): SystemContextValue {
	const ctx = useContext(SystemContext);
	if (!ctx) {
		throw new Error("useSystem must be used within a SystemProvider");
	}
	return ctx;
}
