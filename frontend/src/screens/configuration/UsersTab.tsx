/**
 * Config → Users & Roles tab (issue #32, epic #14) — docs/ui/prototype
 * README "9. Configuration": "user, role pill, site scope, auth method
 * (PIV/CAC, LDAP), last seen, plus a one-line restatement of what each role
 * can do", against the #529 backend (`UsersController`).
 *
 * Role is rendered read-only with an explanatory hint (see users.ts's doc
 * comment) — there is deliberately no role `<select>` anywhere on this tab,
 * matching the backend's `PUT` which has no field to accept one. Only
 * site scope is editable, Admin-gated with the standard roleGateProps
 * visible-but-disabled treatment (mirrors CredentialsTab/StigManagerTab).
 */
import { useCallback, useEffect, useState } from "react";
import { useAuth } from "../../lib/auth-context";
import { ApiError } from "../../lib/api";
import { roleGateProps } from "../../lib/roles";
import { formatTimestamp } from "./credentials";
import { describeRole, fetchUsers, formatSiteScope, updateUserSiteScope, type WaypointUser } from "./users";
import "./ConfigurationScreen.css";
import "./UsersTab.css";

export function UsersTab() {
	const { user } = useAuth();
	const writeGate = user
		? roleGateProps(user.role, "Admin", `Requires Admin — this action is not available to ${user.role}`)
		: { disabled: true };

	const [users, setUsers] = useState<WaypointUser[]>([]);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);

	const [editingId, setEditingId] = useState<string | null>(null);
	const [scopeForm, setScopeForm] = useState("");
	const [saving, setSaving] = useState(false);
	const [formError, setFormError] = useState<string | null>(null);

	const load = useCallback(() => {
		setLoading(true);
		setLoadError(null);
		fetchUsers()
			.then(setUsers)
			.catch((err: unknown) => setLoadError(err instanceof ApiError ? err.message : "Could not load users."))
			.finally(() => setLoading(false));
	}, []);

	useEffect(() => {
		load();
	}, [load]);

	const startEdit = (u: WaypointUser) => {
		setEditingId(u.id);
		setScopeForm(u.site_scope || "[]");
		setFormError(null);
	};

	const cancelEdit = () => {
		setEditingId(null);
		setFormError(null);
	};

	const submitEdit = async (id: string) => {
		let parsed: unknown;
		try {
			parsed = JSON.parse(scopeForm);
		} catch {
			setFormError("Site scope must be valid JSON (e.g. [] or [\"site-1\"]).");
			return;
		}
		if (!Array.isArray(parsed)) {
			setFormError("Site scope must be a JSON array.");
			return;
		}

		setSaving(true);
		setFormError(null);
		try {
			const updated = await updateUserSiteScope(id, scopeForm);
			setUsers((prev) => prev.map((u) => (u.id === id ? updated : u)));
			setEditingId(null);
		} catch (err) {
			setFormError(err instanceof ApiError ? err.message : "Could not save site scope.");
		} finally {
			setSaving(false);
		}
	};

	return (
		<div className="config-tab config-tab--users">
			<div className="config-panel">
				<div className="config-panel__header">
					<div className="config-panel__title">USERS & ROLES</div>
				</div>

				<div className="users-tab__hint">
					Role reflects Keycloak group membership and takes effect on the user&apos;s next sign-in — change it in the
					identity provider, not here. Site scope is the only field editable in this table.
				</div>

				{loadError && <div className="config-panel__error">{loadError}</div>}
				{formError && <div className="config-panel__error">{formError}</div>}

				<table className="config-table">
					<colgroup>
						<col style={{ width: "16%" }} />
						<col style={{ width: "10%" }} />
						<col style={{ width: "16%" }} />
						<col style={{ width: "11%" }} />
						<col style={{ width: "13%" }} />
						<col style={{ width: "24%" }} />
						<col style={{ width: "10%" }} />
					</colgroup>
					<thead>
						<tr>
							<th>USER</th>
							<th>ROLE</th>
							<th>SITE SCOPE</th>
							<th>AUTH METHOD</th>
							<th>LAST SEEN</th>
							<th>ROLE CAN</th>
							<th>ACTIONS</th>
						</tr>
					</thead>
					<tbody>
						{loading && (
							<tr>
								<td colSpan={7} className="config-table__empty">
									Loading users…
								</td>
							</tr>
						)}
						{!loading && users.length === 0 && (
							<tr>
								<td colSpan={7} className="config-table__empty">
									No users yet.
								</td>
							</tr>
						)}
						{!loading &&
							users.map((u) => (
								<tr key={u.id}>
									<td className="config-table__truncate">{u.username}</td>
									<td>
										<span className="users-tab__role-pill" title="Role is IdP-derived — edit it in Keycloak.">
											{u.role}
										</span>
									</td>
									<td className="config-table__truncate">
										{editingId === u.id ? (
											<input
												className="users-tab__scope-input mono"
												value={scopeForm}
												onChange={(e) => setScopeForm(e.target.value)}
												placeholder='[] or ["site-id"]'
											/>
										) : (
											formatSiteScope(u.site_scope)
										)}
									</td>
									<td>{u.auth_method}</td>
									<td className="mono">{formatTimestamp(u.last_seen_at)}</td>
									<td className="users-tab__role-capability">{describeRole(u.role)}</td>
									<td>
										{editingId === u.id ? (
											<div className="users-tab__actions">
												<button type="button" onClick={() => submitEdit(u.id)} disabled={saving}>
													{saving ? "Saving…" : "Save"}
												</button>
												<button type="button" onClick={cancelEdit} disabled={saving}>
													Cancel
												</button>
											</div>
										) : (
											<button type="button" {...writeGate} onClick={() => startEdit(u)}>
												Edit scope
											</button>
										)}
									</td>
								</tr>
							))}
					</tbody>
				</table>
			</div>
		</div>
	);
}
