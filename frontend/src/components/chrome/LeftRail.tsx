import type { ComponentType } from "react";
import { useAuth } from "../../lib/auth-context";
import { Link } from "../../lib/router";
import { useRouter } from "../../lib/router-context";
import { ROUTES, type ScreenKey } from "../../lib/routes";
import { roleAtLeast } from "../../lib/roles";
import { useSystem } from "../../lib/system-context";
import {
	AuditIcon,
	BenchmarksIcon,
	CatalogIcon,
	ChevronIcon,
	ConfigurationIcon,
	DashboardIcon,
	JobsIcon,
	LibraryIcon,
	LiveRunIcon,
	ResultsIcon,
	ScanIcon,
	TransferIcon,
} from "./icons";
import "./LeftRail.css";

// Live Run sits in the COMPLIANCE group (issue #711, epic #706's last open
// child): PR #709 restored the /live-run console but left it unreachable
// from nav (a #590/ADR-0019-era decision that predated the console's
// restoration). Jobs (formerly "Live Jobs", renamed by issue #708 once it
// gained a History mode — title-only, same route key/path) remains the
// separate top-level "what's happening now across every domain" entry.
const ICONS: Record<ScreenKey, ComponentType> = {
	dashboard: DashboardIcon,
	"live-jobs": JobsIcon,
	"live-run": LiveRunIcon,
	"start-scan": ScanIcon,
	results: ResultsIcon,
	benchmarks: BenchmarksIcon,
	catalog: CatalogIcon,
	library: LibraryIcon,
	transfer: TransferIcon,
	configuration: ConfigurationIcon,
	audit: AuditIcon,
};

interface NavGroup {
	label: string | null;
	items: ScreenKey[];
}

const NAV_GROUPS: NavGroup[] = [
	// Jobs sits with Dashboard, above every domain group — ADR-0019 decision 1:
	// "Live Jobs is top-level and cross-domain," not owned by Compliance the
	// way the Live Run console is. Renamed from "Live Jobs" by issue #708
	// once it gained a History mode alongside active work.
	{ label: null, items: ["dashboard", "live-jobs"] },
	{ label: "COMPLIANCE", items: ["live-run", "start-scan", "results", "benchmarks"] },
	{ label: "CONTENT", items: ["catalog", "library", "transfer"] },
	{ label: "CONFIGURE", items: ["configuration", "audit"] },
];

export interface LeftRailProps {
	open: boolean;
	onToggle: () => void;
	/** Nav-badge counts, keyed by screen — deliberately optional/sparse: this
	 * issue owns the chrome, not the run/download/library data feeds that
	 * would populate these, so an un-populated badge renders nothing rather
	 * than a fabricated number. Each screen's own issue wires its count. */
	badges?: Partial<Record<ScreenKey, { text: string; tone: "warn" | "acc" }>>;
}

export function LeftRail({ open, onToggle, badges }: LeftRailProps) {
	const { user } = useAuth();
	const { route: activeRoute } = useRouter();
	const { system, mode } = useSystem();

	if (!user) {
		return null;
	}

	return (
		<nav className={`left-rail ${open ? "is-open" : "is-collapsed"}`} aria-label="Primary">
			{NAV_GROUPS.map((group, groupIndex) => {
				const visibleItems = group.items
					.map((key) => ({ key, route: ROUTES.find((r) => r.key === key)! }))
					.filter(({ route }) => !(route.connectedOnly && mode !== "connected"));
				if (visibleItems.length === 0) {
					return null;
				}
				return (
					<div key={group.label ?? `group-${groupIndex}`}>
						{groupIndex > 0 && <div className="left-rail__separator" />}
						{group.label &&
							(open ? (
								<div className="left-rail__group-label">{group.label}</div>
							) : (
								<div className="left-rail__group-spacer" />
							))}
						{visibleItems.map(({ key, route: r }) => {
							const NavIcon = ICONS[key];
							const active = activeRoute?.key === r.key;
							const allowed = roleAtLeast(user.role, r.requiredRole);
							const badge = badges?.[r.key];
							return (
								<Link
									key={r.key}
									to={r.path}
									className={`left-rail__item ${active ? "is-active" : ""} ${!allowed ? "is-disabled" : ""}`}
									title={allowed ? r.title : `Requires ${r.requiredRole} — not available to ${user.role}`}
									aria-disabled={!allowed}
									aria-current={active ? "page" : undefined}
									onClick={(event) => {
										if (!allowed) {
											event.preventDefault();
										}
									}}
								>
									<NavIcon />
									{open && <span className="left-rail__label">{r.title}</span>}
									{open && badge && (
										<span className={`left-rail__badge left-rail__badge--${badge.tone}`}>{badge.text}</span>
									)}
								</Link>
							);
						})}
					</div>
				);
			})}

			<div className="left-rail__fill" />

			{open && system && (
				<div className="left-rail__footer">
					<div className="left-rail__version mono">
						v{system.version} · build {system.build}
					</div>
					{system.update_available && (
						<div className="left-rail__update mono">{system.update_available} available</div>
					)}
				</div>
			)}

			<button type="button" className="left-rail__collapse" onClick={onToggle} title="Collapse / expand navigation">
				<ChevronIcon style={{ transform: `rotate(${open ? 0 : 180}deg)` }} />
				{open && <span>Collapse</span>}
			</button>
		</nav>
	);
}
