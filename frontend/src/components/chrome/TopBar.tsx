import { useAuth } from "../../lib/auth";
import { useSystem } from "../../lib/system";
import { useTheme } from "../../lib/theme";
import { BrandMark, ThemeIcon } from "./icons";
import "./TopBar.css";

const MODE_LABEL: Record<"connected" | "disconnected", string> = {
	connected: "MODE · INTERNET-ENABLED",
	disconnected: "MODE · AIR-GAPPED",
};

const MODE_TOOLTIP: Record<"connected" | "disconnected", string> = {
	connected: "Internet-enabled: reaches the Broadcom depot and GitHub; all features; builds signed export bundles.",
	disconnected: "Air-gapped: no external network; consumes imported bundles; download/catalog features are hidden.",
};

export function TopBar({ screenTitle }: { screenTitle: string }) {
	const { user } = useAuth();
	const { system, stigman, mode, ready } = useSystem();
	const { theme, toggleTheme } = useTheme();

	// Three cases, not two (issue #94): `mode === "unknown"` covers both
	// "still loading" (`!ready`) and, in principle, any other not-yet-settled
	// state — only `!ready` actually occurs today, since `mode` folds to
	// `"disconnected"` the instant a fetch settles (SystemProvider). Wording
	// them the same was the bug: a request that is still in flight is not an
	// outage, and telling an operator "could not reach the Waypoint API"
	// while it is merely loading is a false alarm.
	const modeLabel = !ready ? "MODE · CHECKING…" : MODE_LABEL[mode === "connected" ? "connected" : "disconnected"];
	const modeTooltip = !ready
		? "Checking deployment mode…"
		: mode === "connected"
			? MODE_TOOLTIP.connected
			: system === null
				? "Deployment mode unavailable — could not reach the Waypoint API."
				: MODE_TOOLTIP.disconnected;

	return (
		<header className="top-bar">
			<div className="top-bar__brand">
				<BrandMark />
				<div className="top-bar__wordmark">WAYPOINT</div>
				<div className="top-bar__qualifier">DoD VCF Toolkit</div>
			</div>

			<div className="top-bar__divider" aria-hidden="true" />
			<div className="top-bar__screen-title">{screenTitle}</div>

			<div className="top-bar__spacer" />

			{/* `GET /stigman` reports configuration, not reachability (issue #316) —
			    `stigman !== null` means a connection is configured, not that it is
			    reachable right now. Live reachability is `POST /stigman/test`
			    (Admin-only, side-effecting), surfaced by the Config → STIG Manager
			    tab's "Test" button, not fired unprompted from this always-on
			    chrome. */}
			<div
				className="top-bar__stigman"
				title={
					stigman
						? `STIG Manager: ${stigman.endpoint}, collection ${stigman.collection} — configured`
						: "STIG Manager: not configured"
				}
			>
				<span className={`top-bar__stigman-dot ${stigman ? "is-ok" : "is-off"}`} />
				<span>STIG Manager</span>
			</div>

			{/* Read-only by design: deployment mode is fixed at deploy time (README
			    "Global Chrome" / "Interactions"), not a runtime toggle — this is
			    deliberately NOT the prototype's clickable demo badge. */}
			<div className={`top-bar__mode top-bar__mode--${mode}`} role="status" title={modeTooltip}>
				<span className="top-bar__mode-dot" />
				{modeLabel}
			</div>

			<div className="top-bar__user" title={`Signed in as ${user?.username ?? "—"}`}>
				{user ? `${user.username} · ${user.role}` : "—"}
			</div>

			<button
				type="button"
				className="top-bar__theme-toggle"
				onClick={toggleTheme}
				title="Toggle light / dark theme"
				aria-label={`Switch to ${theme === "dark" ? "light" : "dark"} theme`}
			>
				<ThemeIcon />
			</button>
		</header>
	);
}
