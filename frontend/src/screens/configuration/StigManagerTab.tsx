/**
 * Config → STIG Manager tab (issue #312, third slice of the #25 split —
 * #310/PR #314 merged the connection model + test endpoint this tab
 * consumes; #311, the CKL upload backend, is a parallel lane this tab does
 * not depend on). docs/ui/prototype/README.md "STIG Manager — global default
 * endpoint, OIDC client, default collection, reachability with API version";
 * prototype panel pairs a GLOBAL DEFAULT form with a PER-SITE OVERRIDES list
 * (docs/ui/prototype/vcf-ops-console.dc.html `cfgStigman`).
 *
 * The prototype's per-site overrides list is read-only (name + resolved
 * value); this tab additionally lets Admin *edit* an override, since #312's
 * AC calls for override display/edit, not just display — the same
 * inherit/override concept the Sites sidebar already renders for
 * `stigman_override` presence (PR #263, `SitesSidebar.tsx`).
 *
 * No inline secret entry anywhere on this tab: the connection carries only a
 * `credential_id` reference into the existing (#247) credential store — a
 * `token`-type credential holds the OIDC client secret. This matches the
 * merged backend (`StigManagerConnectionResponse` has no secret field) and
 * ADR-0011 ("no new secret path"); the prototype's static mock also never
 * showed a secret input for this panel, so there was nothing to reconcile.
 */
import { useCallback, useEffect, useState } from "react";
import { useAuth } from "../../lib/auth";
import { ApiError } from "../../lib/api";
import { roleAtLeast, roleGateProps } from "../../lib/roles";
import { fetchCredentialOptions, fetchSites, type CredentialOption, type Site } from "./sites";
import {
	fetchGlobalStigManager,
	fetchSiteStigManager,
	formatStigManagerOutcome,
	putGlobalStigManager,
	putSiteStigManager,
	testStigManager,
	type ResolvedStigManagerConnection,
	type StigManagerConnection,
	type StigManagerTestResult,
	type StigManagerWriteInput,
} from "./stigman";
import "./ConfigurationScreen.css";
import "./StigManagerTab.css";

const EMPTY_FORM: StigManagerWriteInput = {
	endpoint: "",
	authority: "",
	collection: "",
	client_id: "",
	scope: "",
	credential_id: null,
};

function toFormState(connection: StigManagerConnection | ResolvedStigManagerConnection): StigManagerWriteInput {
	return {
		endpoint: connection.endpoint,
		authority: connection.authority,
		collection: connection.collection,
		client_id: connection.client_id,
		scope: connection.scope,
		credential_id: connection.credential_id,
	};
}

export function StigManagerTab() {
	const { user } = useAuth();
	const canWrite = user ? roleAtLeast(user.role, "Admin") : false;
	const writeGate = user ? roleGateProps(user.role, "Admin", `Requires Admin — this action is not available to ${user.role}`) : { disabled: true };

	const [global, setGlobal] = useState<StigManagerConnection | null>(null);
	const [globalMissing, setGlobalMissing] = useState(false);
	const [credentials, setCredentials] = useState<CredentialOption[]>([]);
	const [sites, setSites] = useState<Site[]>([]);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);

	const [globalForm, setGlobalForm] = useState<StigManagerWriteInput>(EMPTY_FORM);
	const [editingGlobal, setEditingGlobal] = useState(false);
	const [saving, setSaving] = useState(false);
	const [formError, setFormError] = useState<string | null>(null);

	const [testing, setTesting] = useState<"global" | string | null>(null);
	const [testResult, setTestResult] = useState<{ scope: "global" | string; result: StigManagerTestResult } | null>(null);

	const [overrideSiteId, setOverrideSiteId] = useState<string | null>(null);
	const [overrides, setOverrides] = useState<Map<string, ResolvedStigManagerConnection>>(new Map());
	const [overrideForm, setOverrideForm] = useState<StigManagerWriteInput>(EMPTY_FORM);

	const load = useCallback(() => {
		setLoading(true);
		setLoadError(null);
		setGlobalMissing(false);
		Promise.all([fetchCredentialOptions(), fetchSites()])
			.then(([credentialList, siteList]) => {
				setCredentials(credentialList);
				setSites(siteList);
				return fetchGlobalStigManager()
					.then((connection) => {
						setGlobal(connection);
						setGlobalForm(toFormState(connection));
					})
					.catch((err: unknown) => {
						if (err instanceof ApiError && err.status === 404) {
							setGlobal(null);
							setGlobalMissing(true);
							return;
						}
						throw err;
					});
			})
			.then(() => Promise.all(sitesFromState()))
			.catch((err: unknown) => setLoadError(err instanceof ApiError ? err.message : "Could not load the STIG Manager connection."))
			.finally(() => setLoading(false));

		// Per-site resolved connections are fetched lazily (see `loadOverride`
		// below), not up front — #312's AC only needs to *display which sites
		// override*, which `Site.stigman_override` (already on the wire, PR
		// #238) answers without an extra round trip per site.
		function sitesFromState(): Promise<void>[] {
			return [];
		}
	}, []);

	useEffect(() => {
		load();
	}, [load]);

	const submitGlobal = useCallback(async () => {
		setSaving(true);
		setFormError(null);
		try {
			const saved = await putGlobalStigManager(globalForm);
			setGlobal(saved);
			setGlobalMissing(false);
			setEditingGlobal(false);
		} catch (err) {
			setFormError(err instanceof ApiError ? err.message : "Could not save the global STIG Manager connection.");
		} finally {
			setSaving(false);
		}
	}, [globalForm]);

	const doTestGlobal = useCallback(async () => {
		setTesting("global");
		setTestResult(null);
		try {
			const result = await testStigManager();
			setTestResult({ scope: "global", result });
		} catch (err) {
			setTestResult({
				scope: "global",
				result: { reachable: false, auth_ok: false, outcome: "unreachable", message: err instanceof ApiError ? err.message : "Test failed.", api_version: null },
			});
		} finally {
			setTesting(null);
		}
	}, []);

	const doTestSite = useCallback(async (siteId: string) => {
		setTesting(siteId);
		setTestResult(null);
		try {
			const result = await testStigManager(siteId);
			setTestResult({ scope: siteId, result });
		} catch (err) {
			setTestResult({
				scope: siteId,
				result: { reachable: false, auth_ok: false, outcome: "unreachable", message: err instanceof ApiError ? err.message : "Test failed.", api_version: null },
			});
		} finally {
			setTesting(null);
		}
	}, []);

	const openOverride = useCallback(
		(siteId: string) => {
			setFormError(null);
			setOverrideSiteId((current) => (current === siteId ? null : siteId));
			const cached = overrides.get(siteId);
			if (cached) {
				setOverrideForm(toFormState(cached));
				return;
			}
			fetchSiteStigManager(siteId)
				.then((resolved) => {
					setOverrides((prev) => new Map(prev).set(siteId, resolved));
					setOverrideForm(toFormState(resolved));
				})
				.catch((err: unknown) => setFormError(err instanceof ApiError ? err.message : "Could not load this site's STIG Manager connection."));
		},
		[overrides],
	);

	const submitOverride = useCallback(
		async (siteId: string) => {
			setSaving(true);
			setFormError(null);
			try {
				const resolved = await putSiteStigManager(siteId, overrideForm);
				setOverrides((prev) => new Map(prev).set(siteId, resolved));
				setSites((prev) => prev.map((s) => (s.id === siteId ? { ...s, stigman_override: JSON.stringify(overrideForm) } : s)));
				setOverrideSiteId(null);
			} catch (err) {
				setFormError(err instanceof ApiError ? err.message : "Could not save this site's STIG Manager override.");
			} finally {
				setSaving(false);
			}
		},
		[overrideForm],
	);

	return (
		<div className="config-tab config-tab--stigman">
			<div className="config-tab__grid">
				<div className="config-panel">
					<div className="config-panel__header">
						<div className="config-panel__title">GLOBAL DEFAULT</div>
						<div className="config-panel__spacer" />
						{!loading && global && !editingGlobal && (
							<button
								type="button"
								{...writeGate}
								onClick={() => {
									setEditingGlobal(true);
									setGlobalForm(toFormState(global));
									setFormError(null);
								}}
							>
								Edit
							</button>
						)}
					</div>

					{loadError && <div className="config-panel__error">{loadError}</div>}
					{formError && <div className="config-panel__error">{formError}</div>}

					{loading && <div className="config-panel__empty">Loading…</div>}

					{!loading && globalMissing && !editingGlobal && (
						<div className="config-panel__empty">
							No global STIG Manager connection is configured.{" "}
							<button
								type="button"
								{...writeGate}
								onClick={() => {
									setEditingGlobal(true);
									setGlobalForm(EMPTY_FORM);
									setFormError(null);
								}}
							>
								Configure
							</button>
						</div>
					)}

					{!loading && global && !editingGlobal && (
						<StigManagerSummary
							connection={global}
							credentials={credentials}
							testing={testing === "global"}
							testResult={testResult && testResult.scope === "global" ? testResult.result : null}
							testGate={writeGate}
							onTest={doTestGlobal}
						/>
					)}

					{!loading && editingGlobal && canWrite && (
						<StigManagerForm
							form={globalForm}
							setForm={setGlobalForm}
							credentials={credentials}
							saving={saving}
							onCancel={() => {
								setEditingGlobal(false);
								setFormError(null);
								if (global) {
									setGlobalForm(toFormState(global));
								}
							}}
							onSubmit={submitGlobal}
						/>
					)}
				</div>

				<div className="config-panel">
					<div className="config-panel__header">
						<div className="config-panel__title">PER-SITE OVERRIDES</div>
					</div>

					{!loading && sites.length === 0 && <div className="config-panel__empty">No sites yet.</div>}

					{!loading &&
						sites.map((site) => {
							const hasOverride = Boolean(site.stigman_override);
							const isOpen = overrideSiteId === site.id;
							const resolved = overrides.get(site.id);
							return (
								<div key={site.id} className="stigman-tab__site">
									<div className="stigman-tab__site-row">
										<div className="stigman-tab__site-name">{site.name}</div>
										<span className={`stigman-tab__badge ${hasOverride ? "is-override" : "is-inherit"}`}>
											{hasOverride ? "override" : "inherit"}
										</span>
										<div className="config-panel__spacer" />
										<button type="button" onClick={() => openOverride(site.id)}>
											{isOpen ? "Close" : hasOverride ? "View / edit" : "Add override"}
										</button>
									</div>

									{isOpen && (
										<div className="stigman-tab__site-detail">
											{resolved && !canWrite && (
												<StigManagerSummary
													connection={resolved}
													credentials={credentials}
													testing={testing === site.id}
													testResult={testResult && testResult.scope === site.id ? testResult.result : null}
													testGate={writeGate}
													onTest={() => doTestSite(site.id)}
												/>
											)}
											{resolved && canWrite && (
												<>
													<div className="stigman-tab__source">
														Currently resolves from: <strong>{resolved.source === "site_override" ? "this site's override" : "the global default"}</strong>
													</div>
													<StigManagerForm
														form={overrideForm}
														setForm={setOverrideForm}
														credentials={credentials}
														saving={saving}
														onCancel={() => setOverrideSiteId(null)}
														onSubmit={() => submitOverride(site.id)}
													/>
													<div className="stigman-tab__test-row">
														<button type="button" {...writeGate} onClick={() => doTestSite(site.id)} disabled={testing === site.id}>
															{testing === site.id ? "Testing…" : "Test"}
														</button>
														{testResult && testResult.scope === site.id && (
															<TestOutcomeBadge result={testResult.result} />
														)}
													</div>
												</>
											)}
											{!resolved && <div className="config-panel__empty">Loading…</div>}
										</div>
									)}
								</div>
							);
						})}
				</div>
			</div>
		</div>
	);
}

function StigManagerSummary({
	connection,
	credentials,
	testing,
	testResult,
	testGate,
	onTest,
}: {
	connection: StigManagerConnection | ResolvedStigManagerConnection;
	credentials: CredentialOption[];
	testing: boolean;
	testResult: StigManagerTestResult | null;
	testGate: { disabled: boolean; style?: { opacity: number }; title?: string };
	onTest: () => void;
}) {
	const credentialName = connection.credential_id ? credentials.find((c) => c.id === connection.credential_id)?.name ?? connection.credential_id : "none";
	return (
		<div className="stigman-tab__summary">
			<div className="stigman-tab__field">
				<span className="stigman-tab__label">Endpoint</span>
				<span className="mono">{connection.endpoint}</span>
			</div>
			<div className="stigman-tab__field">
				<span className="stigman-tab__label">Authority</span>
				<span className="mono">{connection.authority}</span>
			</div>
			<div className="stigman-tab__field">
				<span className="stigman-tab__label">Collection</span>
				<span className="mono">{connection.collection}</span>
			</div>
			<div className="stigman-tab__field">
				<span className="stigman-tab__label">Client ID</span>
				<span className="mono">{connection.client_id}</span>
			</div>
			<div className="stigman-tab__field">
				<span className="stigman-tab__label">Scope</span>
				<span className="mono">{connection.scope}</span>
			</div>
			<div className="stigman-tab__field">
				<span className="stigman-tab__label">Credential</span>
				<span className="mono">{credentialName}</span>
			</div>
			<div className="stigman-tab__test-row">
				<button type="button" {...testGate} onClick={onTest} disabled={testGate.disabled || testing}>
					{testing ? "Testing…" : "Test"}
				</button>
				{testResult && <TestOutcomeBadge result={testResult} />}
			</div>
		</div>
	);
}

function TestOutcomeBadge({ result }: { result: StigManagerTestResult }) {
	const cls =
		result.outcome === "ok"
			? "stigman-tab__outcome--ok"
			: result.outcome === "not_configured"
				? "stigman-tab__outcome--neutral"
				: "stigman-tab__outcome--bad";
	return (
		<div className={`stigman-tab__outcome ${cls}`}>
			<span className="stigman-tab__outcome-label">{formatStigManagerOutcome(result.outcome)}</span>
			{result.api_version && <span className="mono"> · API v{result.api_version}</span>}
			<div className="stigman-tab__outcome-message">{result.message}</div>
		</div>
	);
}

function StigManagerForm({
	form,
	setForm,
	credentials,
	saving,
	onCancel,
	onSubmit,
}: {
	form: StigManagerWriteInput;
	setForm: (f: StigManagerWriteInput) => void;
	credentials: CredentialOption[];
	saving: boolean;
	onCancel: () => void;
	onSubmit: () => void;
}) {
	const canSubmit = form.endpoint.trim().length > 0 && form.authority.trim().length > 0 && form.collection.trim().length > 0 && form.client_id.trim().length > 0;

	return (
		<form
			className="config-form"
			onSubmit={(e) => {
				e.preventDefault();
				if (canSubmit) onSubmit();
			}}
		>
			<div className="config-form__grid">
				<label className="config-form__field">
					<span>Endpoint</span>
					<input
						value={form.endpoint}
						onChange={(e) => setForm({ ...form, endpoint: e.target.value })}
						placeholder="stigman.example.internal"
						required
					/>
				</label>
				<label className="config-form__field">
					<span>Authority (OIDC issuer)</span>
					<input
						value={form.authority}
						onChange={(e) => setForm({ ...form, authority: e.target.value })}
						placeholder="keycloak.example.internal/realms/waypoint"
						required
					/>
				</label>
				<label className="config-form__field">
					<span>Collection</span>
					<input
						value={form.collection}
						onChange={(e) => setForm({ ...form, collection: e.target.value })}
						placeholder="e.g. 17"
						required
					/>
				</label>
				<label className="config-form__field">
					<span>Client ID</span>
					<input
						value={form.client_id}
						onChange={(e) => setForm({ ...form, client_id: e.target.value })}
						placeholder="waypoint-appliance"
						required
					/>
				</label>
				<label className="config-form__field">
					<span>Scope</span>
					<input
						value={form.scope}
						onChange={(e) => setForm({ ...form, scope: e.target.value })}
						placeholder="openid stig-manager:stig:read"
					/>
				</label>
				<label className="config-form__field">
					<span>Credential</span>
					<select value={form.credential_id ?? ""} onChange={(e) => setForm({ ...form, credential_id: e.target.value || null })}>
						<option value="">No credential</option>
						{credentials.map((c) => (
							<option key={c.id} value={c.id}>
								{c.name}
							</option>
						))}
					</select>
				</label>
			</div>
			<div className="config-form__actions">
				<button type="button" onClick={onCancel} disabled={saving}>
					Cancel
				</button>
				<button type="submit" className="config-form__submit" disabled={saving || !canSubmit}>
					{saving ? "Saving…" : "Save"}
				</button>
			</div>
		</form>
	);
}
