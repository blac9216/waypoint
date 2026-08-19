import { createContext, useContext } from "react";
import type { Role } from "./roles";

export interface AuthUser {
	username: string;
	role: Role;
}

export interface AuthContextValue {
	user: AuthUser | null;
	token: string | null;
	status: "restoring" | "signed-out" | "signing-in" | "signed-in";
	error: string | null;
	/** Dev-flag local-auth path (`LocalAuth:Enabled`) — username/password against `POST /auth/login`. */
	login: (username: string, password: string) => Promise<void>;
	/** Whether the dev-flag local-auth form should be offered at all — `null` while `GET /auth/config` is still loading. */
	localAuthAvailable: boolean | null;
	/** Production sign-in: redirects the browser to Keycloak's authorization endpoint (PKCE). Does not return on the happy path — the page navigates away. */
	startOidcLogin: () => Promise<void>;
	/**
	 * Step-up re-authentication (issue #521/#534): redirects through the
	 * authorization endpoint with `prompt=login` so Keycloak re-issues a
	 * fresh `auth_time`, then returns to `returnTo` (default: current path)
	 * once the callback completes. Distinct from `startOidcLogin` only in
	 * that intent — same redirect + PKCE mechanism.
	 */
	stepUpOidcLogin: (returnTo?: string) => Promise<void>;
	logout: () => void;
}

export const AuthContext = createContext<AuthContextValue | null>(null);

export function useAuth(): AuthContextValue {
	const ctx = useContext(AuthContext);
	if (!ctx) {
		throw new Error("useAuth must be used within an AuthProvider");
	}
	return ctx;
}
