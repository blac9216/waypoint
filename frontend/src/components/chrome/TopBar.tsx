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
	const { system, stigman } = useSystem();
	const { theme, toggleTheme } = useTheme();

	const mode = system?.mode ?? null;

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

			<div
				className="top-bar__stigman"
				title={
					stigman?.connected
						? `STIG Manager: ${stigman.endpoint ?? "unknown endpoint"} — connected, collection ${stigman.collection ?? "?"}`
						: "STIG Manager: not reachable"
				}
			>
				<span className={`top-bar__stigman-dot ${stigman?.connected ? "is-ok" : "is-off"}`} />
				<span>STIG Manager</span>
			</div>

			{/* Read-only by design: deployment mode is fixed at deploy time (README
			    "Global Chrome" / "Interactions"), not a runtime toggle — this is
			    deliberately NOT the prototype's clickable demo badge. */}
			<div
				className={`top-bar__mode top-bar__mode--${mode ?? "unknown"}`}
				role="status"
				title={mode ? MODE_TOOLTIP[mode] : "Deployment mode unavailable — could not reach the Waypoint API."}
			>
				<span className="top-bar__mode-dot" />
				{mode ? MODE_LABEL[mode] : "MODE · UNKNOWN"}
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
