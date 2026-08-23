/**
 * Benchmarks screen (issue #559, epic #558) — replaces the `/benchmarks`
 * placeholder. Profile/control browser (source/version metadata, search)
 * plus the Global/Site/Target config-document editor for `input`/
 * `attestation`/`remediation-input` documents, immutable version history,
 * and an explicit effective-resolution view.
 *
 * "No content installed" vs. "empty benchmark": `GET /profiles`
 * (`ProfilesController`, #40/#566) only ever lists profiles the compliance-
 * content pull actually installed (`Profile.State` is `current` |
 * `update_pending` | `pinned` — there is no "known but absent" row). An
 * empty list here means no content installed at all, distinct from a
 * selected profile that simply has no config-docs at any layer yet (that
 * profile still appears in the list; `LayerDocPanel` reports "No <kind>
 * document defined at this layer" per kind/layer, not the whole screen).
 *
 * There is no `/profiles/{id}/controls` endpoint (see benchmarks.ts's module
 * doc) — this screen browses profiles and their config-documents, never
 * individual controls parsed out of benchmark content.
 */
import { useEffect, useMemo, useState } from "react";
import { ApiError } from "../../lib/api";
import { useAuth } from "../../lib/auth-context";
import { roleAtLeast } from "../../lib/roles";
import { fetchSites, fetchTargets, type Site, type Target } from "../configuration/sites";
import { CONFIG_DOC_KINDS, fetchProfiles, formatTimestamp, type ConfigDocKind, type ProfileSummary } from "./benchmarks";
import { EffectiveResolutionPanel } from "./EffectiveResolutionPanel";
import { LayerDocPanel } from "./LayerDocPanel";
import "./BenchmarksScreen.css";

/** Parses `?profile=<key>&target=<id>` — the Results "Open in Benchmarks"
 * deep link's query shape (see `AttestationsPanel.tsx`). Mirrors
 * `useRunIdFromQuery`'s pattern (lib/router.tsx's documented convention:
 * params ride the query string, not the hand-rolled router's path table). */
function useBenchmarksDeepLink(): { profile: string | null; target: string | null } {
	const [params, setParams] = useState(() => new URLSearchParams(window.location.search));
	useEffect(() => {
		const sync = () => setParams(new URLSearchParams(window.location.search));
		window.addEventListener("popstate", sync);
		return () => window.removeEventListener("popstate", sync);
	}, []);
	return { profile: params.get("profile"), target: params.get("target") };
}

export function BenchmarksScreen() {
	const { user } = useAuth();
	const role = user?.role ?? "Viewer";
	const deepLink = useBenchmarksDeepLink();

	const [profiles, setProfiles] = useState<ProfileSummary[]>([]);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);
	const [search, setSearch] = useState("");
	const [selectedProfileKey, setSelectedProfileKey] = useState<string | null>(null);
	const [kind, setKind] = useState<ConfigDocKind>("input");

	const [sites, setSites] = useState<Site[]>([]);
	const [targets, setTargets] = useState<Target[]>([]);
	const [selectedSiteId, setSelectedSiteId] = useState<string | null>(null);
	const [selectedTargetId, setSelectedTargetId] = useState<string | null>(null);

	useEffect(() => {
		setLoading(true);
		setLoadError(null);
		fetchProfiles()
			.then((list) => {
				setProfiles(list);
				setSelectedProfileKey((prev) => {
					if (deepLink.profile && list.some((p) => p.profile_key === deepLink.profile)) {
						return deepLink.profile;
					}
					if (prev && list.some((p) => p.profile_key === prev)) {
						return prev;
					}
					return list[0]?.profile_key ?? null;
				});
			})
			.catch((err: unknown) => {
				setLoadError(err instanceof ApiError ? err.message : "Could not load the profile inventory.");
			})
			.finally(() => setLoading(false));
		// deepLink.profile is only consulted on first load's initial selection,
		// deliberately not a dependency — re-running this effect on every
		// popstate would refetch the whole profile list for no reason.
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, []);

	useEffect(() => {
		fetchSites()
			.then(setSites)
			.catch(() => setSites([]));
	}, []);

	useEffect(() => {
		if (!selectedSiteId) {
			setTargets([]);
			return;
		}
		fetchTargets(selectedSiteId)
			.then(setTargets)
			.catch(() => setTargets([]));
	}, [selectedSiteId]);

	// Deep link with only a target id known (Results carries target ids, not
	// site ids) — resolve the target's site once the target's site becomes
	// reachable via the sites/targets fetched above.
	useEffect(() => {
		if (!deepLink.target || selectedTargetId) {
			return;
		}
		Promise.all(sites.map((s) => fetchTargets(s.id).then((ts) => ({ site: s, ts })))).then((groups) => {
			for (const { site, ts } of groups) {
				if (ts.some((t) => t.id === deepLink.target)) {
					setSelectedSiteId(site.id);
					setSelectedTargetId(deepLink.target);
					return;
				}
			}
		});
		// Runs once sites are available; re-running per sites/targets identity
		// change would refetch every site's targets repeatedly.
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, [sites, deepLink.target]);

	const filteredProfiles = useMemo(() => {
		const q = search.trim().toLowerCase();
		if (!q) {
			return profiles;
		}
		return profiles.filter((p) => p.name.toLowerCase().includes(q) || p.profile_key.toLowerCase().includes(q) || (p.version ?? "").toLowerCase().includes(q));
	}, [profiles, search]);

	const selectedProfile = profiles.find((p) => p.profile_key === selectedProfileKey) ?? null;
	const siteNameById = useMemo(() => new Map(sites.map((s) => [s.id, s.name])), [sites]);
	const targetNameById = useMemo(() => new Map(targets.map((t) => [t.id, t.name])), [targets]);

	const canWrite = roleAtLeast(role, "Admin");

	if (loading && profiles.length === 0) {
		return <div className="bench-screen__status">Loading profiles…</div>;
	}

	return (
		<div className="bench-screen">
			{loadError && <div className="bench-layer__error bench-screen__load-error">{loadError}</div>}

			<div className="bench-screen__grid">
				<div className="bench-panel bench-panel--profiles">
					<div className="bench-panel__header">
						<div className="bench-panel__title">PROFILES</div>
						<div className="bench-panel__spacer" />
						<div className="bench-role-note">{canWrite ? "Writer (Admin)" : `Viewer role: ${role} — read-only`}</div>
					</div>
					<input
						className="bench-search"
						type="search"
						placeholder="Search profiles…"
						value={search}
						onChange={(event) => setSearch(event.target.value)}
					/>
					{profiles.length === 0 && !loading && (
						<div className="bench-panel__empty">
							No compliance content installed. Install content from Configuration → Compliance Content before profiles appear here.
						</div>
					)}
					{profiles.length > 0 && filteredProfiles.length === 0 && <div className="bench-panel__empty">No profiles match "{search}".</div>}
					<div className="bench-profile-list">
						{filteredProfiles.map((p) => (
							<button
								key={p.id}
								type="button"
								className={`bench-profile-list__item ${p.profile_key === selectedProfileKey ? "is-selected" : ""}`}
								onClick={() => setSelectedProfileKey(p.profile_key)}
							>
								<div className="bench-profile-list__name">{p.name}</div>
								<div className="bench-profile-list__meta">
									{p.version ?? "unversioned"} · {p.commit.slice(0, 8)} · {p.state.replace("_", " ")}
								</div>
								<div className="bench-profile-list__meta">updated {formatTimestamp(p.updated_at)}</div>
							</button>
						))}
					</div>
				</div>

				<div className="bench-panel bench-panel--detail">
					{!selectedProfile && <div className="bench-panel__empty">Select a profile to inspect its controls and configuration.</div>}
					{selectedProfile && (
						<>
							<div className="bench-panel__header">
								<div className="bench-panel__title">{selectedProfile.name}</div>
								<div className="bench-panel__spacer" />
								<span className="mono bench-profile-list__meta">{selectedProfile.profile_key}</span>
							</div>
							<div className="bench-scope-picker">
								<label>
									Site
									<select
										value={selectedSiteId ?? ""}
										onChange={(event) => {
											setSelectedSiteId(event.target.value || null);
											setSelectedTargetId(null);
										}}
									>
										<option value="">— none —</option>
										{sites.map((s) => (
											<option key={s.id} value={s.id}>
												{s.name}
											</option>
										))}
									</select>
								</label>
								<label>
									Target
									<select
										value={selectedTargetId ?? ""}
										onChange={(event) => setSelectedTargetId(event.target.value || null)}
										disabled={!selectedSiteId}
									>
										<option value="">— none —</option>
										{targets.map((t) => (
											<option key={t.id} value={t.id}>
												{t.name}
											</option>
										))}
									</select>
								</label>
							</div>

							<div className="bench-kind-tabs">
								{CONFIG_DOC_KINDS.map((k) => (
									<button
										key={k.value}
										type="button"
										className={`bench-kind-tabs__tab ${kind === k.value ? "is-active" : ""}`}
										onClick={() => setKind(k.value)}
									>
										{k.label}
									</button>
								))}
							</div>

							<LayerDocPanel
								profileKey={selectedProfile.profile_key}
								kind={kind}
								role={role}
								siteId={selectedSiteId}
								siteName={selectedSiteId ? (siteNameById.get(selectedSiteId) ?? null) : null}
								targetId={selectedTargetId}
								targetName={selectedTargetId ? (targetNameById.get(selectedTargetId) ?? null) : null}
							/>

							<EffectiveResolutionPanel
								profileKey={selectedProfile.profile_key}
								targetId={selectedTargetId}
								siteNameById={siteNameById}
								targetNameById={targetNameById}
							/>
						</>
					)}
				</div>
			</div>
		</div>
	);
}
