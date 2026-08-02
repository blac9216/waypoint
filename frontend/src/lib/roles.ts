/**
 * Role model — domain-model.md "Roles". Four roles, strictly increasing:
 * Viewer < Cyber < Operator < Admin. The server enforces every guard for
 * real (api-contract.md Conventions: "the API enforces role guards
 * server-side — the UI's disabled-with-reason treatment is presentation
 * only"). This module exists purely for that presentation layer.
 */

export type Role = "Viewer" | "Cyber" | "Operator" | "Admin";

export const ROLE_ORDER: Role[] = ["Viewer", "Cyber", "Operator", "Admin"];

export function roleRank(role: Role): number {
	return ROLE_ORDER.indexOf(role);
}

/** True if `role` meets or exceeds `required` in the strictly-increasing order. */
export function roleAtLeast(role: Role, required: Role): boolean {
	return roleRank(role) >= roleRank(required);
}

/**
 * README "Roles & Permissions" treatment: an action a role cannot take stays
 * visible but disabled, at opacity 0.42, with a title explaining why — never
 * silently hidden. Returns the props to spread onto the disabled control.
 */
export function roleGateProps(
	role: Role,
	required: Role,
	reason?: string,
): { disabled: boolean; style?: { opacity: number }; title?: string } {
	const allowed = roleAtLeast(role, required);
	if (allowed) {
		return { disabled: false };
	}
	return {
		disabled: true,
		style: { opacity: 0.42 },
		title: reason ?? `Requires ${required} — this action is not available to ${role}`,
	};
}
