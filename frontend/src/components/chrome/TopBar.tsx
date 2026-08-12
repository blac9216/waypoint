import { useAuth } from "../../lib/auth-context";
import { useSystem, type SystemRunnerStatus } from "../../lib/system-context";
import { useTheme } from "../../lib/theme-context";
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

			{/* Issue #443: the API process no longer executes anything itself
			    (ADR-0013), so "is a job actually able to run" is a separate fact
			    from "is the API up" — this reads GET /system's `runners` list
			    (each entry a compliance-runner or download-runner heartbeat)
			    rather than backend health. Reuses the STIG Manager indicator's
			    exact dot+label shape; only shown once `ready`, since an empty
			    list before the first fetch settles would misleadingly read as
			    "no runners". */}
			{ready && <RunnersIndicator runners={system?.runners ?? []} />}

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

/**
 * Issue #443: a `runners` list of length 0 is a real, distinct state from
 * "unavailable" — no worker has ever heartbeated (e.g. a fresh deployment
 * before either runner container has started), not "a worker is down". Both
 * read as "not fully ready" but say different things to an operator, so the
 * dot color and tooltip cover three states, matching the STIG Manager and
 * mode indicators' own two/three-state pattern rather than collapsing to a
 * plain boolean.
 *
 * Issue #489: `starved_job_types` is layered on top of, not folded into,
 * `available` — a runner reports itself available while still denying
 * admission to specific job types. A permanent starvation (profile alone
 * exceeds the runner's total effective budget — misconfiguration, never
 * self-resolves) escalates the dot past "degraded" the same way "some
 * runner unavailable" does, since it needs the same "go look at this" signal
 * as an outage; transient starvation (contention that clears on its own)
 * stays at the existing degraded/warn treatment so it doesn't cry wolf.
 */
function RunnersIndicator({ runners }: { runners: SystemRunnerStatus[] }) {
	const availableCount = runners.filter((runner) => runner.available).length;
	const starvedRunners = runners.filter((runner) => runner.starved_job_types.length > 0);
	const hasPermanentStarvation = starvedRunners.some((runner) => runner.starved_job_types.some((starved) => starved.permanent));

	const state: "none" | "degraded" | "all-ok" =
		runners.length === 0
			? "none"
			: availableCount < runners.length || starvedRunners.length > 0
				? "degraded"
				: "all-ok";

	const label = runners.length === 0 ? "Runners" : `Runners ${availableCount}/${runners.length}`;
	const tooltipLines =
		runners.length === 0
			? ["Runners: none reporting — no compliance-runner or download-runner has heartbeated yet."]
			: runners.map((runner) => {
					const base = `${runner.worker_id} (${runner.job_types.join(", ")}): ${runner.available ? "available" : "unavailable"}`;
					if (runner.starved_job_types.length === 0) {
						return base;
					}
					const starved = runner.starved_job_types
						.map((s) => `${s.job_type}${s.permanent ? " (permanent — misconfiguration, will not self-resolve)" : " (transient)"}`)
						.join(", ");
					return `${base}\n  starved: ${starved}`;
				});

	const dotClass =
		state === "all-ok" ? "is-ok" : state === "none" ? "is-off" : hasPermanentStarvation ? "is-bad" : "is-degraded";

	return (
		<div className="top-bar__stigman" title={tooltipLines.join("\n")}>
			<span className={`top-bar__stigman-dot ${dotClass}`} />
			<span>{label}</span>
			{starvedRunners.length > 0 && (
				<span className={`top-bar__runners-starved ${hasPermanentStarvation ? "is-bad" : "is-degraded"}`}>
					{hasPermanentStarvation ? "⚠ starved (permanent)" : "⚠ starved"}
				</span>
			)}
		</div>
	);
}
