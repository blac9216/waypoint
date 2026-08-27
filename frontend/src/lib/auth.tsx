import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { ApiError, apiFetch, setTokenGetter, setUnauthorizedHandler } from "./api";
import { decodeJwtPayload } from "./jwt";
import {
	completeLogin,
	consumeReturnTo,
	discoverOidc,
	endSessionUrl,
	startLogin,
} from "./oidc";
import { ROLE_ORDER, roleRank, type Role } from "./roles";
import { AuthContext, type AuthContextValue } from "./auth-context";

/**
 * `GET /auth/config` (issue #534, anonymous) — how the SPA learns, without
 * hardcoding, (a) whether the dev-flag local-auth form should be offered at
 * all (`LocalAuth:Enabled`) and (b) the browser-facing OIDC authority +
 * public client id it needs for the real Keycloak redirect. See
 * `Waypoint.Api.Contracts.AuthConfigResponse` / `OidcAuthOptions` for the
 * server side of this contract.
 */
interface AuthConfigWire {
	local_auth_enabled: unknown;
	oidc_authority: unknown;
	oidc_client_id: unknown;
}

/** The SPA's own JWT-derived session view — deliberately narrower than a full OIDC client's token set: only what `AuthContext` needs to render (role, username, expiry). */
interface OidcSessionClaims {
	role: unknown;
	preferred_username: unknown;
	sub: unknown;
	exp: unknown;
}

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

const STORAGE_KEY = "waypoint.session";

interface AuthConfig {
	localAuthEnabled: boolean;
	oidcAuthority: string;
	oidcClientId: string;
}

/**
 * Fetched once per page load and cached module-level (not component
 * state): `GET /auth/config` never changes mid-session — it's server
 * deployment configuration, not per-user data — and every caller in this
 * file (the local-auth-availability probe, `startOidcLogin`,
 * `stepUpOidcLogin`, `logout`'s end-session redirect, the OIDC callback
 * handler) needs the same answer. A module-level promise (not just a
 * cached value) also collapses concurrent callers into one in-flight
 * fetch instead of firing one each.
 */
let authConfigPromise: Promise<AuthConfig> | null = null;

function fetchAuthConfig(): Promise<AuthConfig> {
	authConfigPromise ??= apiFetch<AuthConfigWire>("/auth/config", { unauthenticated: true })
		.then((wire) => ({
			localAuthEnabled: wire.local_auth_enabled === true,
			oidcAuthority: toWireText(wire.oidc_authority, "oidc_authority", "GET /auth/config"),
			oidcClientId: toWireText(wire.oidc_client_id, "oidc_client_id", "GET /auth/config"),
		}))
		.catch((err: unknown) => {
			// A failed fetch must not poison the module-level cache forever — the
			// next caller (or the same page, once the backend is reachable
			// again) gets to try again rather than replaying the same rejection
			// indefinitely.
			authConfigPromise = null;
			throw err;
		});
	return authConfigPromise;
}

/** Test-only escape hatch: `authConfigPromise` is deliberately module-level (see above), so a test suite that mocks `GET /auth/config` differently per test must clear it between tests or every test after the first reuses the first test's cached answer. Not exported from any public entry point — import directly from this module in tests. */
export function __resetAuthConfigCacheForTests(): void {
	authConfigPromise = null;
}

/** Everything needed to restore a session without another round trip: the
 * bearer token, the identity fetched from `/auth/me` at login time, and the
 * server-issued expiry so a stale/expired stored session is rejected on
 * restore instead of handed back to the app as valid. */
interface StoredSession {
	token: string;
	username: string;
	role: Role;
	expiresAt: string;
	/** `"local"` (dev-flag `POST /auth/login`) or `"oidc"` (Keycloak) — logout behaves differently per kind (see `logout()`): an OIDC session ends with an end-session redirect, a local session just clears storage. Defaults to `"local"` on restore of a session persisted before this field existed (issue #534) so an in-flight dev session isn't dropped by the upgrade. */
	kind?: "local" | "oidc";
}

function isRole(value: unknown): value is Role {
	return typeof value === "string" && (ROLE_ORDER as string[]).includes(value);
}

/**
 * Strict ISO-8601 instant shape, matching what a .NET `DateTimeOffset`
 * actually serializes to (`System.Text.Json`'s default `DateTimeOffset`
 * converter, which every wire producer in this repo uses — see
 * `Waypoint.Api/Contracts/AuthContracts.cs`'s `LoginResponse.ExpiresAt`):
 * `YYYY-MM-DDTHH:mm:ss[.fff...](Z|±HH:mm)`. This is deliberately narrower
 * than anything `Date.parse` accepts — `Date.parse` is implementation-defined
 * outside the ISO subset and happily parses `"December 31, 2099"`, a shape
 * the backend can never emit. A stored session's `expiresAt` restoring
 * "successfully" off a value like that is exactly the half-accepted state
 * `readStoredSession()` exists to refuse (issue #98).
 */
const ISO_INSTANT_RE = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$/;

function isIsoInstant(value: unknown): value is string {
	return typeof value === "string" && ISO_INSTANT_RE.test(value) && Number.isFinite(Date.parse(value));
}

/** Non-empty after trimming — the bar `toWireText()` already holds the wire
 * path to (see its doc comment). `readStoredSession()` used to only check
 * `typeof === "string"`, which let `""` and whitespace-only values restore
 * as a "signed in" session that carries no usable token/identity (issue
 * #98). */
function isNonBlankString(value: unknown): value is string {
	return typeof value === "string" && value.trim() !== "";
}

/**
 * Recognized `Role` values found in a wire `role` claim, in whatever shape it
 * arrived. Local-auth (`POST /auth/login`, `GET /auth/me`) sends a flat
 * string. Keycloak's `waypoint-role` mapper is an
 * `oidc-group-membership-mapper` (`deploy/keycloak/realm/waypoint-realm.json`),
 * which **always** emits a JSON array of group names — `["Admin"]` even for a
 * single group — never a scalar (issue #872). Both shapes are legitimate wire
 * contracts from different issuers, not one right shape and one malformed
 * one, so both are accepted here; unrecognized elements (a group name that
 * isn't one of the four `Role`s) are silently dropped rather than treated as
 * a parse failure, since the mapper may surface groups Waypoint doesn't map
 * to a role at all.
 */
function recognizedRoles(value: unknown): Role[] {
	if (typeof value === "string") {
		return isRole(value) ? [value] : [];
	}
	if (Array.isArray(value)) {
		return value.filter(isRole);
	}
	return [];
}

/**
 * Narrow a `role` that arrived over the network to the closed `Role` set, or
 * refuse the sign-in. Accepts both wire shapes `recognizedRoles()` knows
 * about — a bare string (local-auth) or a string array (Keycloak's
 * group-membership mapper, issue #872).
 *
 * When more than one recognized role comes back (a user in more than one
 * mapped group), the **most privileged** one wins. This is a presentation
 * choice only — `roles.ts`'s header note applies here too: the server
 * enforces every guard for real, the SPA's role only drives disabled/hidden
 * styling. Picking the highest of several roles the server has actually
 * granted the user shows them the UI that matches what they can do; picking
 * the lowest would hide controls the server would honor if they clicked
 * them, which is a worse failure mode than the reverse (a disabled-with-reason
 * control is always the fallback the server-side check still guards).
 *
 * **Fails closed** when nothing recognized comes back, consistent with how
 * the rest of the role model already behaves: every comparison routes
 * through `roleAtLeast` → `roleRank` → `ROLE_ORDER.indexOf`, an unrecognized
 * role yields `-1`, and `-1 >= 0` is false, so it is denied everywhere.
 * Signing in *anyway* on an out-of-domain role is the worst of the options —
 * it produces exactly the half-accepted state issue #64 exists to close:
 * `login()` reports success, every guard denies (blank nav, Configuration
 * silently redirecting to Dashboard), and the next page refresh signs the
 * user out because `readStoredSession()` correctly rejects what `login()`
 * just wrote. An empty array (group membership mapper with no mapped groups)
 * fails closed the same way as a missing claim or an unrecognized string.
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
	const recognized = recognizedRoles(value);
	if (recognized.length === 0) {
		throw new ApiError(
			200,
			"invalid_role",
			`${source} returned a role Waypoint does not recognize, so the sign-in was refused.`,
			{ source, received: value, expected: ROLE_ORDER },
		);
	}
	return recognized.reduce((best, role) => (roleRank(role) > roleRank(best) ? role : best));
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
			!isNonBlankString(parsed.token) ||
			!isNonBlankString(parsed.username) ||
			!isIsoInstant(parsed.expiresAt) ||
			!isRole(parsed.role)
		) {
			return null;
		}
		if (Date.parse(parsed.expiresAt) <= Date.now()) {
			return null;
		}
		return {
			token: parsed.token,
			username: parsed.username,
			role: parsed.role,
			expiresAt: parsed.expiresAt,
			// Pre-#534 stored sessions have no `kind` at all — treat them as
			// "local" (the only kind that existed before), not as a parse
			// failure that would sign the user out on the very upgrade that
			// added this field.
			kind: parsed.kind === "oidc" ? "oidc" : "local",
		};
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

export function AuthProvider({ children }: { children: ReactNode }) {
	const [session, setSession] = useState<StoredSession | null>(null);
	const [status, setStatus] = useState<AuthContextValue["status"]>("restoring");
	const [error, setError] = useState<string | null>(null);
	const sessionRef = useRef<StoredSession | null>(null);
	sessionRef.current = session;

	// The one teardown path for "this session is over, for any reason" — an
	// explicit logout(), a 401 from the server (setUnauthorizedHandler below),
	// or the client-side expiry enforcement (issue #97). All three funnel
	// through here so there is exactly one way a session ends, not a parallel
	// one invented per caller.
	const dropSession = useCallback(() => {
		setSession(null);
		setStatus("signed-out");
		clearStoredSession();
	}, []);

	useEffect(() => {
		setTokenGetter(() => sessionRef.current?.token ?? null);
		setUnauthorizedHandler(dropSession);
	}, [dropSession]);

	useEffect(() => {
		// The OIDC callback landing (`/oidc/callback`, see oidc.ts's
		// `redirectUri()`) is handled entirely here, before any route/role
		// gating runs — App.tsx never learns this path exists. A stored
		// session (if any) is irrelevant on this exact load: the browser just
		// came back from Keycloak specifically to mint a new one.
		if (window.location.pathname === "/oidc/callback") {
			completeOidcCallback();
			return;
		}
		const restored = readStoredSession();
		if (!restored) {
			// A stored session that failed to parse/validate (including one that
			// has simply expired) is not left behind for the next mount to trip
			// over again.
			clearStoredSession();
		}
		setSession(restored);
		setStatus(restored ? "signed-in" : "signed-out");
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, []);

	/**
	 * Exchanges the authorization code on the current URL for tokens
	 * (`oidc.ts`'s `completeLogin`), builds a `StoredSession` from the access
	 * token's own claims (no `/auth/me` round trip needed — the token already
	 * carries `role`/`preferred_username`/`exp`, mapped by the realm's
	 * protocol mappers, `deploy/keycloak/realm/waypoint-realm.json`), and
	 * restores the browser to the path `startLogin`/`stepUpOidcLogin` was
	 * called from (`consumeReturnTo`) via `history.replaceState` — a plain
	 * navigation, not `router.navigate`, because this runs before
	 * `RouterProvider` exists in the tree (`AuthProvider` wraps it, see
	 * `App.tsx`) and because the code/state query params must not survive
	 * into browser history (replayable, and ugly in the address bar).
	 */
	async function completeOidcCallback(): Promise<void> {
		setStatus("signing-in");
		setError(null);
		try {
			const config = await fetchAuthConfig();
			const discovery = await discoverOidc(config.oidcAuthority);
			const tokens = await completeLogin(discovery, config.oidcClientId, window.location.href);
			const claims = decodeJwtPayload(tokens.accessToken) as unknown as OidcSessionClaims | null;
			if (!claims) {
				throw new Error("Could not read claims from the issued access token.");
			}
			const role = toRole(claims.role, "Keycloak access token");
			const username = toWireText(claims.preferred_username ?? claims.sub, "preferred_username/sub", "Keycloak access token");
			const next: StoredSession = { token: tokens.accessToken, username, role, expiresAt: tokens.expiresAt, kind: "oidc" };
			setSession(next);
			setStatus("signed-in");
			persistSession(next);
		} catch (err) {
			setStatus("signed-out");
			setError(err instanceof Error ? err.message : "OIDC sign-in failed.");
		} finally {
			const returnTo = consumeReturnTo();
			window.history.replaceState(null, "", returnTo);
		}
	}

	/**
	 * Client-side expiry enforcement (issue #97). Without this, an expired
	 * `expiresAt` was only noticed on the *next* mount (readStoredSession) or
	 * whenever some authenticated request happened to 401 — until then the
	 * app kept rendering full chrome on a token the server had already
	 * invalidated. There is no refresh endpoint in M1
	 * (docs/api-contract.md's Auth section): expiry means sign out, not renew.
	 *
	 * Two mechanisms, because neither alone is sufficient:
	 *
	 * - A `setTimeout` scheduled to the exact `expiresAt` catches the common
	 *   case (tab stays open and foregrounded) promptly, without polling.
	 *   Re-scheduled whenever `session` changes (login, restore, logout) and
	 *   always cleared on cleanup — an uncleared timer here would fire
	 *   `dropSession()` against a since-unmounted/replaced provider, the exact
	 *   leaked-timer shape issue #324 was about.
	 * - A `visibilitychange`/`focus` re-check catches the case the timer
	 *   can't: a backgrounded tab's timers are throttled/clamped by the
	 *   browser, so a tab that was backgrounded before expiry and returns to
	 *   the foreground after it must re-evaluate immediately rather than wait
	 *   for a delayed timer to eventually fire.
	 *
	 * Both paths call the same `dropSession()` used by `logout()` and the 401
	 * handler — no parallel sign-out logic.
	 */
	useEffect(() => {
		if (!session) {
			return;
		}
		const msRemaining = Date.parse(session.expiresAt) - Date.now();
		if (msRemaining <= 0) {
			dropSession();
			return;
		}
		const timer = window.setTimeout(dropSession, msRemaining);

		const recheck = () => {
			if (document.visibilityState !== "visible") {
				return;
			}
			if (Date.parse(sessionRef.current?.expiresAt ?? "") <= Date.now()) {
				dropSession();
			}
		};
		document.addEventListener("visibilitychange", recheck);
		window.addEventListener("focus", recheck);

		return () => {
			window.clearTimeout(timer);
			document.removeEventListener("visibilitychange", recheck);
			window.removeEventListener("focus", recheck);
		};
	}, [session, dropSession]);

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
				kind: "local",
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

	/**
	 * OIDC sessions end with Keycloak's own RP-Initiated Logout redirect
	 * (kills the browser's Keycloak SSO cookie too — without it, the very
	 * next `startOidcLogin` would silently re-authenticate the same user via
	 * the still-live Keycloak session, which is not what a user who clicked
	 * "sign out" asked for). Local-auth sessions have no IdP session to end,
	 * so they fall back to the plain `dropSession()` every prior version of
	 * this app used. Either way `dropSession()` runs first so the app's own
	 * state is clean even if the discovery fetch below fails (offline
	 * Keycloak should never trap a user mid-logout).
	 */
	const logout = useCallback(() => {
		const kind = sessionRef.current?.kind;
		dropSession();
		if (kind !== "oidc") {
			return;
		}
		fetchAuthConfig()
			.then((config) => discoverOidc(config.oidcAuthority).then((discovery) => ({ config, discovery })))
			.then(({ config, discovery }) => {
				const url = endSessionUrl(discovery, config.oidcClientId);
				if (url) {
					window.location.assign(url);
				}
			})
			.catch(() => {
				// Best-effort: the local session is already dropped either way
				// (see above) — a Keycloak-side session surviving is a lesser
				// failure than trapping the user on a logout that can't complete.
			});
	}, [dropSession]);

	const [localAuthAvailable, setLocalAuthAvailable] = useState<boolean | null>(null);

	useEffect(() => {
		fetchAuthConfig()
			.then((config) => setLocalAuthAvailable(config.localAuthEnabled))
			.catch(() => setLocalAuthAvailable(false));
	}, []);

	const startOidcLogin = useCallback(async () => {
		const config = await fetchAuthConfig();
		const discovery = await discoverOidc(config.oidcAuthority);
		await startLogin(discovery, config.oidcClientId);
	}, []);

	/** Step-up re-auth (issue #521/#534): same redirect mechanism as `startOidcLogin`, `prompt=login` so Keycloak re-issues a fresh `auth_time` even with an active SSO session (docs/security.md "Step-up re-authentication"). */
	const stepUpOidcLogin = useCallback(async (returnTo?: string) => {
		const config = await fetchAuthConfig();
		const discovery = await discoverOidc(config.oidcAuthority);
		await startLogin(discovery, config.oidcClientId, { prompt: "login", returnTo });
	}, []);

	const value = useMemo<AuthContextValue>(
		() => ({
			user: session ? { username: session.username, role: session.role } : null,
			token: session?.token ?? null,
			status,
			error,
			login,
			localAuthAvailable,
			startOidcLogin,
			stepUpOidcLogin,
			logout,
		}),
		[session, status, error, login, localAuthAvailable, startOidcLogin, stepUpOidcLogin, logout],
	);

	return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
