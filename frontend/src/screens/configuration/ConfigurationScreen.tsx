/**
 * Configuration screen shell — docs/ui/prototype/README.md "9. Configuration
 * — Six tabs." Issue #237 (split into #256/#257/#258 after PR #255 was too
 * large to review) implemented the first tab (Sites & Targets); issue #247
 * added the second (Credentials); issue #40 adds the fourth (Compliance
 * Content); issue #312 adds the fifth (STIG Manager); issue #32 adds the
 * sixth (Users & Roles); issue #571 adds the third (Depot & Tokens),
 * completing #560's frontend half. Every tab now has a real component — the
 * stub-text fallback branch is kept only as a defensive default for an
 * unrecognized `tab` value, which should be unreachable given `TABS` is the
 * only source of tab keys.
 */
import { useState } from "react";
import { ComplianceContentTab } from "./ComplianceContentTab";
import { CredentialsTab } from "./CredentialsTab";
import { DepotTokensTab } from "./DepotTokensTab";
import { SitesTargetsTab } from "./SitesTargetsTab";
import { StigManagerTab } from "./StigManagerTab";
import { UsersTab } from "./UsersTab";
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
				{tab === "depot" && <DepotTokensTab />}
				{tab === "content" && <ComplianceContentTab />}
				{tab === "stigman" && <StigManagerTab />}
				{tab === "users" && <UsersTab />}
			</div>
		</div>
	);
}
