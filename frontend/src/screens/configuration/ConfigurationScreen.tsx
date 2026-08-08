/**
 * Configuration screen shell — docs/ui/prototype/README.md "9. Configuration
 * — Six tabs." Issue #237 (split into #256/#257/#258 after PR #255 was too
 * large to review) implemented the first tab (Sites & Targets); issue #247
 * added the second (Credentials); issue #312 adds the third (STIG Manager).
 * The remaining three stay stub text so this shell can be built additively
 * without guessing at unbuilt tabs' shapes — each slots in by adding one
 * entry to `TABS` and one branch below, no change to this file's structure.
 */
import { useState } from "react";
import { CredentialsTab } from "./CredentialsTab";
import { SitesTargetsTab } from "./SitesTargetsTab";
import { StigManagerTab } from "./StigManagerTab";
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
				{tab === "credentials" && <CredentialsTab />}
				{tab === "stigman" && <StigManagerTab />}
				{tab !== "sites" && tab !== "credentials" && tab !== "stigman" && (
					<div className="config-tab__status">
						{TABS.find((t) => t.key === tab)?.label} lands in a future PR (docs/ui/prototype/README.md
						"Configuration").
					</div>
				)}
			</div>
		</div>
	);
}
