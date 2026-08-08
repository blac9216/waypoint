/**
 * Config → Sites & Targets tab (issue #237, epic #13) — docs/ui/prototype
 * README "Sites & Targets" panel, against the #19 backend (`/sites`,
 * `/sites/{id}/targets`, `/targets/{id}`, PR #238).
 *
 * Layout mirrors the prototype: a targets table for the selected site (main
 * pane) beside a SITES sidebar (site list + counts). The prototype only
 * mocks reads; this component additionally owns full CRUD for both sites and
 * targets per #237's acceptance criteria, Admin-gated (`roleGateProps`),
 * visible-but-disabled for lower roles per domain-model.md's convention.
 */
import { useCallback, useEffect, useState } from "react";
import { ApiError } from "../../lib/api";
import { createSite, deleteSite, fetchCredentialOptions, fetchSites, updateSite, type CredentialOption, type Site } from "./sites";
import { SitesSidebar } from "./SitesSidebar";
import { SiteTargetsPanel } from "./SiteTargetsPanel";
import "./ConfigurationScreen.css";

export function SitesTargetsTab() {
	const [sites, setSites] = useState<Site[]>([]);
	const [credentials, setCredentials] = useState<CredentialOption[]>([]);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);
	const [selectedId, setSelectedId] = useState<string | null>(null);
	const [saving, setSaving] = useState(false);
	const [formError, setFormError] = useState<string | null>(null);

	const load = useCallback(() => {
		setLoading(true);
		setLoadError(null);
		Promise.all([fetchSites(), fetchCredentialOptions()])
			.then(([siteList, credentialList]) => {
				setSites(siteList);
				setCredentials(credentialList);
				setSelectedId((prev) => {
					if (prev && siteList.some((s) => s.id === prev)) {
						return prev;
					}
					return siteList[0]?.id ?? null;
				});
			})
			.catch((err: unknown) => {
				setLoadError(err instanceof ApiError ? err.message : "Could not load sites & targets.");
			})
			.finally(() => setLoading(false));
	}, []);

	useEffect(() => {
		load();
	}, [load]);

	const handleCreate = useCallback(
		async (input: { name: string; description: string }) => {
			setSaving(true);
			setFormError(null);
			try {
				const created = await createSite({ name: input.name, description: input.description || null });
				setSelectedId(created.id);
				load();
			} catch (err) {
				setFormError(err instanceof ApiError ? err.message : "Could not create the site.");
			} finally {
				setSaving(false);
			}
		},
		[load],
	);

	const handleUpdate = useCallback(
		async (id: string, input: { name: string; description: string }) => {
			setSaving(true);
			setFormError(null);
			try {
				await updateSite(id, { name: input.name, description: input.description || null });
				load();
			} catch (err) {
				setFormError(err instanceof ApiError ? err.message : "Could not update the site.");
			} finally {
				setSaving(false);
			}
		},
		[load],
	);

	const handleDelete = useCallback(
		async (id: string, name: string) => {
			if (!window.confirm(`Delete site "${name}"? Its targets must be removed first.`)) {
				return;
			}
			setFormError(null);
			try {
				await deleteSite(id);
				load();
			} catch (err) {
				setFormError(err instanceof ApiError ? err.message : "Could not delete the site.");
			}
		},
		[load],
	);

	if (loading && sites.length === 0) {
		return <div className="config-tab__status">Loading sites & targets…</div>;
	}

	const selectedSite = sites.find((s) => s.id === selectedId) ?? null;

	return (
		<div className="config-tab config-tab--sites">
			{loadError && <div className="config-panel__error">{loadError}</div>}
			<div className="config-tab__grid">
				{selectedSite ? (
					<SiteTargetsPanel siteId={selectedSite.id} siteName={selectedSite.name} credentials={credentials} />
				) : (
					<div className="config-panel">
						<div className="config-panel__empty">No site selected yet — add a site to get started.</div>
					</div>
				)}
				<SitesSidebar
					sites={sites}
					selectedId={selectedId}
					onSelect={setSelectedId}
					onCreate={handleCreate}
					onUpdate={handleUpdate}
					onDelete={handleDelete}
					saving={saving}
					formError={formError}
				/>
			</div>
		</div>
	);
}
