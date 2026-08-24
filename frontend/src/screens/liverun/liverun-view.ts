/**
 * Non-component view helpers shared by LiveRunLayouts.tsx's layout
 * components. Split out from LiveRunLayouts.tsx (#480) so that file's only
 * exports are components, clearing the `react/only-export-components` warn
 * (`.oxlintrc.json`) that PR #477's extraction surfaced.
 */
const STATE_COLOR: Record<string, string> = {
	queued: "var(--txt3)",
	blocked: "var(--txt3)",
	running: "var(--acc)",
	attesting: "var(--acc)",
	converting: "var(--acc)",
	uploaded: "var(--ok)",
	done: "var(--ok)",
	failed: "var(--bad)",
	"auth-failed": "var(--bad)",
};

export function stateColor(state: string): string {
	return STATE_COLOR[state] ?? "var(--txt3)";
}
