import { useState, type FormEvent } from "react";
import { useAuth } from "../../lib/auth-context";
import { BrandMark } from "../chrome/icons";
import "./LoginScreen.css";

/**
 * Sign-in screen (issue #534). Production path is the "Sign in with
 * Keycloak" button — `startOidcLogin()` redirects the whole page to
 * Keycloak's `/authorize` endpoint (PKCE, `lib/oidc.ts`), so there is
 * nothing else to render for it beyond a button and an error line; the rest
 * of the flow happens off-screen (redirect away, redirect back to
 * `/oidc/callback`, `AuthProvider` takes it from there).
 *
 * The username/password form below it is the dev-flag local-auth path
 * (`LocalAuth:Enabled`, ADR-0004 rollout note) — feature-detected via
 * `localAuthAvailable` (`GET /auth/config`, fetched once by `AuthProvider`)
 * rather than hardcoded, so a production build with the flag off never
 * shows a form that would just 404. `localAuthAvailable === null` is the
 * config-still-loading state: the form is withheld (not shown-then-hidden)
 * until the answer is known, so it never flashes.
 */
export function LoginScreen() {
	const { login, startOidcLogin, localAuthAvailable, status, error } = useAuth();
	const [username, setUsername] = useState("");
	const [password, setPassword] = useState("");
	const [oidcError, setOidcError] = useState<string | null>(null);
	const submitting = status === "signing-in";

	async function onSubmit(event: FormEvent) {
		event.preventDefault();
		try {
			await login(username, password);
		} catch {
			// error surfaced via auth context state; nothing further to do here.
		}
	}

	async function onOidcClick() {
		setOidcError(null);
		try {
			// Does not resolve on the happy path — the page navigates away to
			// Keycloak. A caught rejection here means the redirect itself could
			// not be started (e.g. GET /auth/config or discovery failed), not a
			// rejected sign-in (that surfaces later, on the callback).
			await startOidcLogin();
		} catch (err) {
			setOidcError(err instanceof Error ? err.message : "Could not reach Keycloak.");
		}
	}

	return (
		<div className="login-screen">
			<div className="login-card">
				<div className="login-card__brand">
					<BrandMark size={28} />
					<div>
						<div className="login-card__wordmark">WAYPOINT</div>
						<div className="login-card__qualifier">DoD VCF Toolkit</div>
					</div>
				</div>

				{oidcError && (
					<div className="login-error" role="alert">
						{oidcError}
					</div>
				)}

				<button type="button" className="login-submit" onClick={onOidcClick} disabled={submitting}>
					Sign in with Keycloak
				</button>

				{localAuthAvailable && (
					<>
						<div className="login-hint">— or, local authentication (dev-grade for milestone M1, ADR-0004) —</div>
						<form className="login-card" onSubmit={onSubmit}>
							<label className="login-field">
								<span>Username</span>
								<input
									type="text"
									name="username"
									autoComplete="username"
									value={username}
									onChange={(e) => setUsername(e.target.value)}
									required
								/>
							</label>

							<label className="login-field">
								<span>Password</span>
								<input
									type="password"
									name="password"
									autoComplete="current-password"
									value={password}
									onChange={(e) => setPassword(e.target.value)}
									required
								/>
							</label>

							{error && (
								<div className="login-error" role="alert">
									{error}
								</div>
							)}

							<button type="submit" className="login-submit" disabled={submitting}>
								{submitting ? "Signing in…" : "Sign in (local)"}
							</button>
						</form>
					</>
				)}
			</div>
		</div>
	);
}
