/**
 * Configuration screen shell — docs/ui/prototype/README.md "9. Configuration
 * — Six tabs." Issue #237 implements only the first tab (Sites & Targets);
 * the remaining five stay PlaceholderScreen-style stubs so this shell can be
 * built additively without guessing at unbuilt tabs' shapes. Issue #247
 * (Credentials tab, not yet dispatched) and future tabs slot in by adding one
 * entry to `TABS` and one branch below — no change to this file's structure.
 */
import { useState } from "react";
import { SitesTargetsTab } from "./SitesTargetsTab";
import "./ConfigurationScreen.css";

type ConfigTabKey = "sites" | "credentials" | "depot" | "content" | "users" | "stigman";

const TABS: { key: ConfigTabKey; label: string }[] = [
	{ key: "sites", label: "Sites & Targets" },
	{ key: "credentials", label: "Credentials" },
	{ key: "depot", label: "Depot & Tokens" },
	{ key: "content", label: "Compliance Content" },
	{ key: "users", label: "Users & Roles" },
	{ key: "stigman", label: "STIG Manager" },
];

export function ConfigurationScreen() {
	const [tab, setTab] = useState<ConfigTabKey>("sites");

	return (
		<div className="config-screen">
			<div className="config-screen__tabbar">
				{TABS.map((t) => (
					<button
						key={t.key}
						type="button"
						className={`config-screen__tab ${tab === t.key ? "is-active" : ""}`}
						onClick={() => setTab(t.key)}
					>
						{t.label}
					</button>
				))}
			</div>
			<div className="config-screen__content">
				{tab === "sites" && <SitesTargetsTab />}
				{tab !== "sites" && (
					<div className="config-tab__status">
						{TABS.find((t) => t.key === tab)?.label} lands in a future PR (docs/ui/prototype/README.md
						"Configuration").
					</div>
				)}
			</div>
		</div>
	);
}
