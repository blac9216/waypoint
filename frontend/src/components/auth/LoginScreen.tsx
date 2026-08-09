import { useState, type FormEvent } from "react";
import { useAuth } from "../../lib/auth-context";
import { BrandMark } from "../chrome/icons";
import "./LoginScreen.css";

/**
 * Local auth login (issue #3, not yet built — see lib/auth.tsx's header
 * comment for the endpoint assumption this screen posts against). Keycloak
 * (OIDC, CAC/PIV) replaces this outright at M3 (ADR-0004) behind the same
 * AuthProvider contract, so this component should not need to change then.
 */
export function LoginScreen() {
	const { login, status, error } = useAuth();
	const [username, setUsername] = useState("");
	const [password, setPassword] = useState("");
	const submitting = status === "signing-in";

	async function onSubmit(event: FormEvent) {
		event.preventDefault();
		try {
			await login(username, password);
		} catch {
			// error surfaced via auth context state; nothing further to do here.
		}
	}

	return (
		<div className="login-screen">
			<form className="login-card" onSubmit={onSubmit}>
				<div className="login-card__brand">
					<BrandMark size={28} />
					<div>
						<div className="login-card__wordmark">WAYPOINT</div>
						<div className="login-card__qualifier">DoD VCF Toolkit</div>
					</div>
				</div>

				<label className="login-field">
					<span>Username</span>
					<input
						type="text"
						name="username"
						autoComplete="username"
						value={username}
						onChange={(e) => setUsername(e.target.value)}
						autoFocus
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
					{submitting ? "Signing in…" : "Sign in"}
				</button>

				<div className="login-hint">Local authentication — dev-grade for milestone M1 (ADR-0004).</div>
			</form>
		</div>
	);
}
