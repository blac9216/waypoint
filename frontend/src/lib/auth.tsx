import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { ApiError, apiFetch, setTokenGetter, setUnauthorizedHandler } from "./api";
import type { Role } from "./roles";

/**
 * ASSUMPTION — flagged, not silent (see the PR description's "Open questions"
 * / the deferred docs issue this links to): docs/api-contract.md documents
 * the *shape* of auth ("OIDC bearer token ... local-auth issuer in M1 —
 * ADR-0004") but not a concrete login endpoint, and the real backend
 * (issue #3) doesn't exist yet either. This client targets the smallest
 * contract-consistent surface — `POST /api/v1/auth/login` returning a
 * bearer token + the caller's identity, snake_case per Conventions — mocked
 * locally by the dev-only Vite plugin `frontend/vite-plugins/mock-backend.ts`
 * (`apply: "serve"`, so it never ships) until #3 lands. This file is the one
 * place to update if the real shape differs.
 */
interface LoginResponseWire {
	token: string;
	user: { username: string; role: Role };
}

export interface AuthUser {
	username: string;
	role: Role;
}

interface AuthContextValue {
	user: AuthUser | null;
	token: string | null;
	status: "restoring" | "signed-out" | "signing-in" | "signed-in";
	error: string | null;
	login: (username: string, password: string) => Promise<void>;
	logout: () => void;
}

const STORAGE_KEY = "waypoint.session";

interface StoredSession {
	token: string;
	user: AuthUser;
}

function readStoredSession(): StoredSession | null {
	try {
		// sessionStorage, not localStorage: this is a dev-grade "session token"
		// (issue #3's own wording) — it should not outlive the browser/PWA
		// session. Real SSO sessions (Keycloak, M3) will replace this outright.
		const raw = window.sessionStorage.getItem(STORAGE_KEY);
		if (!raw) {
			return null;
		}
		const parsed = JSON.parse(raw) as StoredSession;
		if (parsed && typeof parsed.token === "string" && parsed.user) {
			return parsed;
		}
		return null;
	} catch {
		return null;
	}
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
	const [session, setSession] = useState<StoredSession | null>(null);
	const [status, setStatus] = useState<AuthContextValue["status"]>("restoring");
	const [error, setError] = useState<string | null>(null);
	const sessionRef = useRef<StoredSession | null>(null);
	sessionRef.current = session;

	useEffect(() => {
		setTokenGetter(() => sessionRef.current?.token ?? null);
		setUnauthorizedHandler(() => {
			setSession(null);
			setStatus("signed-out");
			try {
				window.sessionStorage.removeItem(STORAGE_KEY);
			} catch {
				// best-effort
			}
		});
	}, []);

	useEffect(() => {
		const restored = readStoredSession();
		setSession(restored);
		setStatus(restored ? "signed-in" : "signed-out");
	}, []);

	const login = useCallback(async (username: string, password: string) => {
		setStatus("signing-in");
		setError(null);
		try {
			const response = await apiFetch<LoginResponseWire>("/auth/login", {
				method: "POST",
				body: { username, password },
				unauthenticated: true,
			});
			const next: StoredSession = { token: response.token, user: response.user };
			setSession(next);
			setStatus("signed-in");
			try {
				window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify(next));
			} catch {
				// best-effort
			}
		} catch (err) {
			setStatus("signed-out");
			if (err instanceof ApiError) {
				setError(err.message);
			} else {
				setError("Could not reach the Waypoint API.");
			}
			throw err;
		}
	}, []);

	const logout = useCallback(() => {
		setSession(null);
		setStatus("signed-out");
		try {
			window.sessionStorage.removeItem(STORAGE_KEY);
		} catch {
			// best-effort
		}
	}, []);

	const value = useMemo<AuthContextValue>(
		() => ({
			user: session?.user ?? null,
			token: session?.token ?? null,
			status,
			error,
			login,
			logout,
		}),
		[session, status, error, login, logout],
	);

	return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
	const ctx = useContext(AuthContext);
	if (!ctx) {
		throw new Error("useAuth must be used within an AuthProvider");
	}
	return ctx;
}
