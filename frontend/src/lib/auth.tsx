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
 *
 * The two interfaces below are those responses exactly as the server sends
 * them, and **every field is typed `unknown`** — deliberately, not for want
 * of better types. `apiFetch<T>` casts parsed JSON straight to `T` without
 * inspecting it, so `token: string` or `role: Role` would not be facts the
 * compiler checked; they would be assertions about values the network
 * controls, and TypeScript would then cheerfully let them flow into the app
 * unvalidated. `unknown` makes the compiler *require* the narrowing helpers
 * below, so the wire path cannot drift back to being laxer than the
 * `sessionStorage` restore path — which is the asymmetry this file used to
 * have: strict where the network could not reach, absent where it could.
 *
 * It is all-or-nothing on purpose. A mixed interface — some fields narrowed,
 * some asserted — leaves the next reader no way to tell which is which, and
 * that ambiguity is what produced this bug in the first place. If a field is
 * listed here, assume nothing about it until it has been through a narrowing
 * helper.
 */
interface LoginResponseWire {
	token: unknown;
	role: unknown;
	expires_at: unknown;
}

interface CurrentUserResponseWire {
	username: unknown;
	role: unknown;
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

/**
 * Narrow a `role` that arrived over the network to the closed `Role` set, or
 * refuse the sign-in. The same `isRole()` predicate that guards the
 * `sessionStorage` restore path — applied to the path that actually faces
 * the network.
 *
 * **Fails closed**, consistent with how the rest of the role model already
 * behaves: every comparison routes through `roleAtLeast` → `roleRank` →
 * `ROLE_ORDER.indexOf`, an unrecognized role yields `-1`, and `-1 >= 0` is
 * false, so it is denied everywhere. Signing in *anyway* on an out-of-domain
 * role is the worst of the options — it produces exactly the half-accepted
 * state issue #64 exists to close: `login()` reports success, every guard
 * denies (blank nav, Configuration silently redirecting to Dashboard), and
 * the next page refresh signs the user out because `readStoredSession()`
 * correctly rejects what `login()` just wrote.
 *
 * The offending value goes in `detail`, never in `message`: `message` is
 * rendered on the login screen, and this is server-controlled text of
 * unbounded length.
 *
 * `status` is 200 on purpose — the transport succeeded and the server
 * answered; it is the *body* that violates the contract. Reporting a
 * synthetic 5xx here would misdescribe what happened on the wire.
 */
function toRole(value: unknown, source: string): Role {
	if (!isRole(value)) {
		throw new ApiError(
			200,
			"invalid_role",
			`${source} returned a role Waypoint does not recognize, so the sign-in was refused.`,
			{ source, received: value, expected: ROLE_ORDER },
		);
	}
	return value;
}

/**
 * Narrow a wire field to a usable non-empty string, or refuse the sign-in.
 *
 * "Usable" is the bar, not merely "a string": a `token` of `""` produces a
 * session that looks valid, attaches no usable credential, and only unwinds
 * when some later request happens to 401 — a half-accepted state, which is
 * the thing this module is meant to stop producing. Whitespace-only is
 * rejected for the same reason it would be for a token: it is not a
 * credential, and for `username` it renders as a blank name in the top bar.
 *
 * As with `toRole()`, the rejected value rides in `detail` rather than the
 * user-facing `message` (server-controlled text, unbounded length).
 *
 * Note this deliberately does **not** touch `readStoredSession()`. The
 * restore path's acceptance of empty strings is tracked separately in #98
 * and stays there; this is the wire path only.
 */
function toWireText(value: unknown, field: string, source: string): string {
	if (typeof value !== "string" || value.trim() === "") {
		throw new ApiError(
			200,
			"invalid_field",
			`${source} returned an unusable ${field} (missing or empty), so the sign-in was refused.`,
			{ source, field, received: value },
		);
	}
	return value;
}

/**
 * Narrow a wire timestamp to a string that actually denotes an instant.
 *
 * The bar is "parses to a finite time", nothing stricter. In particular this
 * does **not** require the expiry to be in the future: the server is
 * authoritative for session lifetime, and the client's clock may legitimately
 * be skewed, so rejecting a freshly-issued token because the local clock runs
 * fast would break login for a problem the client cannot diagnose. An expiry
 * already in the past is handled where it belongs — `readStoredSession()`
 * declines to restore it.
 *
 * What this does stop is an `expires_at` that is not a parseable instant at
 * all. Left unchecked, `Date.parse` yields `NaN`, every `NaN <= now`
 * comparison is false, so the session is persisted as though it were valid
 * and is then rejected on the *next* restore — the login succeeds and the
 * refresh signs the user out. Same failure shape as an unrecognized role,
 * one field over.
 */
function toWireInstant(value: unknown, field: string, source: string): string {
	const text = toWireText(value, field, source);
	if (!Number.isFinite(Date.parse(text))) {
		throw new ApiError(
			200,
			"invalid_field",
			`${source} returned an unusable ${field} (not a valid timestamp), so the sign-in was refused.`,
			{ source, field, received: value },
		);
	}
	return text;
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
			// Narrow the login response before any of it is used — in particular
			// before `token` is presented as a credential. Sending a bearer
			// header built from an unvalidated value would spend a round trip to
			// learn something already knowable here.
			const token = toWireText(loginResponse.token, "token", "POST /auth/login");
			const expiresAt = toWireInstant(loginResponse.expires_at, "expires_at", "POST /auth/login");
			// The login response carries no identity, only a token + role — fetch
			// `/auth/me` with the just-issued token (not yet reflected by
			// `getToken()`'s state closure, so it's attached explicitly here)
			// to get the username the rest of the app needs.
			const me = await apiFetch<CurrentUserResponseWire>("/auth/me", {
				method: "GET",
				unauthenticated: true,
				headers: { Authorization: `Bearer ${token}` },
			});
			// Named `meUsername`, not `username`: this block already has a
			// `username` parameter, and a same-block `const username` would put
			// the earlier `body: { username, password }` into the temporal dead
			// zone — a ReferenceError on every login.
			const meUsername = toWireText(me.username, "username", "GET /auth/me");
			// Both wire roles are narrowed before either is trusted. `/auth/me`
			// returns a role as well, and it used to be fetched and silently
			// discarded — which meant the server's two views of this one session
			// could disagree and nothing would ever notice.
			//
			// Neither endpoint "wins" a disagreement, because there is no honest
			// way to pick: `/auth/login` is authoritative for the *session*
			// (token, expiry) and `/auth/me` for *identity* (username), but
			// `role` is the single field both assert, for the same token, from
			// the same server, milliseconds apart. A divergence there is a
			// contract violation — not a race worth papering over — so the
			// sign-in is refused rather than resolved. That is the fail-closed
			// reading, it matches an unrecognized role being denied everywhere,
			// and the remedy is one retry. (Today divergence is unreachable: M1
			// local auth serves a single config-fixed role. If #29's issuer ever
			// makes it legitimate, `/auth/me` is the value that should win — it
			// survives the Keycloak swap and can be re-queried — but that must
			// be decided there, deliberately, not defaulted to here.)
			const loginRole = toRole(loginResponse.role, "POST /auth/login");
			const meRole = toRole(me.role, "GET /auth/me");
			if (loginRole !== meRole) {
				throw new ApiError(
					200,
					"role_mismatch",
					"The Waypoint API reported two different roles for this session, so the sign-in was refused.",
					{ login: loginRole, me: meRole },
				);
			}
			// Every field is a narrowed local, not a wire property — so this
			// object cannot be assembled at all unless the whole response
			// validated.
			const next: StoredSession = {
				token,
				username: meUsername,
				role: loginRole,
				expiresAt,
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
