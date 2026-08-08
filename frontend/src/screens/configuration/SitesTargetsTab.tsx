/**
 * Config → Sites & Targets tab (issue #237, epic #13) — docs/ui/prototype
 * README "Sites & Targets" panel, against the #19 backend (`/sites`,
 * `/sites/{id}/targets`, `/targets/{id}`, PR #238).
 *
 * This is the #257 slice: site selection + full sites CRUD (sidebar), with
 * a placeholder in the main pane where the targets table lands. #237 was
 * originally implemented as one PR (#255) but that PR was closed on review
 * for size (~1450 net LOC) and split into #256 (data layer, merged) / #257
 * (this tab shell + sites CRUD) / #258 (targets table + targets CRUD, which
 * replaces the placeholder below with the real table).
 */
import { useCallback, useEffect, useState } from "react";
import { ApiError } from "../../lib/api";
import { createSite, deleteSite, fetchCredentialOptions, fetchSites, fetchTargets, updateSite, type CredentialOption, type Site } from "./sites";
import { SitesSidebar } from "./SitesSidebar";
import "./ConfigurationScreen.css";

export function SitesTargetsTab() {
	const [sites, setSites] = useState<Site[]>([]);
	// Fetched here (not by the #258 targets panel) because the credential
	// picker for target forms needs it too, and #258 wires into this
	// component's main pane — keeping the fetch at this level avoids two
	// screens independently re-fetching the same /credentials listing.
	const [, setCredentials] = useState<CredentialOption[]>([]);
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

	const load = useCallback(() => {
		setLoading(true);
		setLoadError(null);
		Promise.all([fetchSites(), fetchCredentialOptions()])
			.then(async ([siteList, credentialList]) => {
				setSites(siteList);
				setCredentials(credentialList);
				setSelectedId((prev) => {
					if (prev && siteList.some((s) => s.id === prev)) {
						return prev;
					}
					return siteList[0]?.id ?? null;
				});
				// Best-effort: a single site's count failing to load should not
				// block the rest of the screen — the sidebar just shows "—" for
				// that one site (see SitesSidebar's rendering of a missing count).
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
					<div className="config-panel">
						<div className="config-panel__header">
							<div className="config-panel__title">{selectedSite.name.toUpperCase()} · TARGETS</div>
						</div>
						<div className="config-panel__empty">
							Targets table lands in #258 (frontend: Sites & Targets tab — targets table + Targets CRUD).
						</div>
					</div>
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
