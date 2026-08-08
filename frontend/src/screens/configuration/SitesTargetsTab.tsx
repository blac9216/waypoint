/**
 * Config → Sites & Targets tab (issue #237, epic #13) — docs/ui/prototype
 * README "Sites & Targets" panel, against the #19 backend (`/sites`,
 * `/sites/{id}/targets`, `/targets/{id}`, PR #238).
 *
 * #237 was originally one PR (#255), closed on review for size (~1450 net
 * LOC) and split into #256 (data layer, merged), #257 (tab shell + Sites
 * CRUD, merged), and #258 (this issue: the targets table + Targets CRUD,
 * replacing #257's main-pane placeholder with the real `SiteTargetsPanel`).
 */
import { useCallback, useEffect, useState } from "react";
import { ApiError } from "../../lib/api";
import { createSite, deleteSite, fetchCredentialOptions, fetchSites, fetchTargets, updateSite, type CredentialOption, type Site } from "./sites";
import { SitesSidebar } from "./SitesSidebar";
import { SiteTargetsPanel } from "./SiteTargetsPanel";
import "./ConfigurationScreen.css";

export function SitesTargetsTab() {
	const [sites, setSites] = useState<Site[]>([]);
	// The credential picker in SiteTargetsPanel's target forms reads this
	// listing; it's fetched here (once) rather than in the panel because this
	// level already loads it for the sidebar's count-refresh path.
	const [credentials, setCredentials] = useState<CredentialOption[]>([]);
	// The `/sites` resource has no target-count field (docs/api-contract.md
	// "Site: name, description, stigman_override?" — no count), so the
	// sidebar's per-site count (docs/ui/prototype/README.md "Sidebar lists
	// sites with target counts") is derived client-side: one
	// `/sites/{id}/targets` fetch per site, in parallel, keyed by site id.
	const [targetCounts, setTargetCounts] = useState<Map<string, number>>(new Map());
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);
	const [selectedId, setSelectedId] = useState<string | null>(null);
	const [saving, setSaving] = useState(false);
	const [formError, setFormError] = useState<string | null>(null);

	// Best-effort per-site target counts (see `targetCounts` note above). A
	// single site's count failing to load shows "—" for that one site rather
	// than blocking; extracted so SiteTargetsPanel can trigger a refresh after
	// a create/delete keeps the sidebar count in sync with the table.
	const refreshCounts = useCallback(async (siteList: Site[]) => {
		const counts = await Promise.all(
			siteList.map(async (site) => {
				try {
					const targets = await fetchTargets(site.id);
					return [site.id, targets.length] as const;
				} catch {
					return [site.id, undefined] as const;
				}
			}),
		);
		setTargetCounts(new Map(counts.filter((c): c is [string, number] => c[1] !== undefined)));
	}, []);

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
				void refreshCounts(siteList);
			})
			.catch((err: unknown) => {
				setLoadError(err instanceof ApiError ? err.message : "Could not load sites & targets.");
			})
			.finally(() => setLoading(false));
	}, [refreshCounts]);

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
					<SiteTargetsPanel
						siteId={selectedSite.id}
						siteName={selectedSite.name}
						credentials={credentials}
						onTargetsChanged={() => void refreshCounts(sites)}
					/>
				) : (
					<div className="config-panel">
						<div className="config-panel__empty">No site selected yet — add a site to get started.</div>
					</div>
				)}
				<SitesSidebar
					sites={sites}
					targetCounts={targetCounts}
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
