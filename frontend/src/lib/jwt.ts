/**
 * Minimal JWT payload decode (issue #534) — base64url-decode the middle
 * segment and JSON-parse it. Deliberately does **not** verify the
 * signature: the backend is the only party that needs to trust this token
 * (every request re-validates it against Keycloak's JWKS, `Oidc:Authority`),
 * so a client-side "verification" would be theater — it can't check
 * anything the network didn't already hand the client in cleartext. This
 * exists purely to read the `role`/`sub`/`auth_time` claims for display and
 * for deciding *when* to redirect for step-up, not to authorize anything.
 */
export function decodeJwtPayload(token: string): Record<string, unknown> | null {
	const parts = token.split(".");
	if (parts.length !== 3) {
		return null;
	}
	try {
		const base64 = parts[1].replace(/-/g, "+").replace(/_/g, "/");
		const padded = base64.padEnd(base64.length + ((4 - (base64.length % 4)) % 4), "=");
		const json = decodeURIComponent(
			atob(padded)
				.split("")
				.map((c) => "%" + c.charCodeAt(0).toString(16).padStart(2, "0"))
				.join(""),
		);
		const parsed = JSON.parse(json) as unknown;
		return parsed && typeof parsed === "object" ? (parsed as Record<string, unknown>) : null;
	} catch {
		return null;
	}
}
