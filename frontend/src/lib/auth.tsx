import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { ApiError, apiFetch, setTokenGetter, setUnauthorizedHandler } from "./api";
import { ROLE_ORDER, type Role } from "./roles";

/**
 * Local-auth login/session client — the confirmed contract (issue #64,
 * settled against the real backend, `docs/api-contract.md`'s Auth
 * section): `POST /api/v1/auth/login` returns `{token, role, expires_at}`
 * — there is **no `user` object** on the login response, `role` is a flat
 * PascalCase string (`Role.ToString()` server-side; see `./roles`'s closed
 * set), and `expires_at` is an ISO-8601 UTC instant. Identity (`username`)
 * comes from a separate call, `GET /api/v1/auth/me`, once a token exists.
 * This module is still the one place to update if the contract moves — the
 * previous `{token, user}` assumption this file made ahead of the real
 * backend landing was wrong; see the issue for the integration break it
 * caused (`AuthContext.user` staying `null`, dropping all chrome + the
 * persisted session on refresh).
 */
interface LoginResponseWire {
	token: string;
	role: Role;
	expires_at: string;
}

interface CurrentUserResponseWire {
	username: string;
	role: Role;
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

/** Everything needed to restore a session without another round trip: the
 * bearer token, the identity fetched from `/auth/me` at login time, and the
 * server-issued expiry so a stale/expired stored session is rejected on
 * restore instead of handed back to the app as valid. */
interface StoredSession {
	token: string;
	username: string;
	role: Role;
	expiresAt: string;
}

function isRole(value: unknown): value is Role {
	return typeof value === "string" && (ROLE_ORDER as string[]).includes(value);
}

function readStoredSession(): StoredSession | null {
	try {
		// sessionStorage, not localStorage: this is a dev-grade "session token"
		// (ADR-0004's rollout note) — it should not outlive the browser/PWA
		// session. Real SSO sessions (Keycloak, #29) will replace this outright.
		const raw = window.sessionStorage.getItem(STORAGE_KEY);
		if (!raw) {
			return null;
		}
		const parsed = JSON.parse(raw) as Partial<StoredSession> | null;
		if (
			!parsed ||
			typeof parsed.token !== "string" ||
			typeof parsed.username !== "string" ||
			typeof parsed.expiresAt !== "string" ||
			!isRole(parsed.role)
		) {
			return null;
		}
		if (Number.isNaN(Date.parse(parsed.expiresAt)) || Date.parse(parsed.expiresAt) <= Date.now()) {
			return null;
		}
		return { token: parsed.token, username: parsed.username, role: parsed.role, expiresAt: parsed.expiresAt };
	} catch {
		return null;
	}
}

function persistSession(session: StoredSession): void {
	try {
		window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify(session));
	} catch {
		// best-effort
	}
}

function clearStoredSession(): void {
	try {
		window.sessionStorage.removeItem(STORAGE_KEY);
	} catch {
		// best-effort
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
			clearStoredSession();
		});
	}, []);

	useEffect(() => {
		const restored = readStoredSession();
		if (!restored) {
			// A stored session that failed to parse/validate (including one that
			// has simply expired) is not left behind for the next mount to trip
			// over again.
			clearStoredSession();
		}
		setSession(restored);
		setStatus(restored ? "signed-in" : "signed-out");
	}, []);

	const login = useCallback(async (username: string, password: string) => {
		setStatus("signing-in");
		setError(null);
		try {
			const loginResponse = await apiFetch<LoginResponseWire>("/auth/login", {
				method: "POST",
				body: { username, password },
				unauthenticated: true,
			});
			// The login response carries no identity, only a token + role — fetch
			// `/auth/me` with the just-issued token (not yet reflected by
			// `getToken()`'s state closure, so it's attached explicitly here)
			// to get the username the rest of the app needs.
			const me = await apiFetch<CurrentUserResponseWire>("/auth/me", {
				method: "GET",
				unauthenticated: true,
				headers: { Authorization: `Bearer ${loginResponse.token}` },
			});
			const next: StoredSession = {
				token: loginResponse.token,
				username: me.username,
				role: loginResponse.role,
				expiresAt: loginResponse.expires_at,
			};
			setSession(next);
			setStatus("signed-in");
			persistSession(next);
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
		clearStoredSession();
	}, []);

	const value = useMemo<AuthContextValue>(
		() => ({
			user: session ? { username: session.username, role: session.role } : null,
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
