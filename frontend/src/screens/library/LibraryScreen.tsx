/**
 * Library — Repository tab (issue #36, docs/ui/prototype screen 7): mode-aware
 * presence over the depot catalog, a product-family rail, and the air-gapped
 * "Export request manifest" action. Connected-mode-only affordances (queue
 * missing in the download catalog) degrade to hidden/disabled per mode rather
 * than being a separate screen — the tab itself is Viewer-readable in either
 * mode (`routes.ts`'s `library` route is not `connectedOnly`).
 *
 * The "Content Library" sub-tab from the prototype (OVF/ISO upload/import,
 * `/content-library/items`) is a separate surface this issue does not build —
 * already tracked as issue #37 ("frontend+backend: content library management").
 */
import { useCallback, useEffect, useMemo, useState } from "react";
import { useAuth } from "../../lib/auth-context";
import { ApiError } from "../../lib/api";
import { roleAtLeast, roleGateProps } from "../../lib/roles";
import { useRouter } from "../../lib/router-context";
import {
	fetchLibraryItems,
	fetchLibraryRequestManifest,
	formatBytes,
	matchesPresenceFilter,
	requestManifestToBlob,
	PRESENCE_LABELS,
	type LibraryFamily,
	type LibraryItem,
	type LibraryItemsResponse,
	type LibraryPresenceFilter,
} from "./library";
import "./LibraryScreen.css";

function downloadManifest(blob: Blob, filename: string) {
	const url = URL.createObjectURL(blob);
	const link = document.createElement("a");
	link.href = url;
	link.download = filename;
	document.body.appendChild(link);
	link.click();
	document.body.removeChild(link);
	URL.revokeObjectURL(url);
}

export function LibraryScreen() {
	const { user } = useAuth();
	const { navigate } = useRouter();

	const [data, setData] = useState<LibraryItemsResponse | null>(null);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);

	const [search, setSearch] = useState("");
	const [presenceFilter, setPresenceFilter] = useState<LibraryPresenceFilter>("all");
	const [selectedFamily, setSelectedFamily] = useState<string | null>(null);

	const [exporting, setExporting] = useState(false);
	const [exportError, setExportError] = useState<string | null>(null);

	const load = useCallback(() => {
		setLoading(true);
		setLoadError(null);
		fetchLibraryItems()
			.then((res) => setData(res))
			.catch((err: unknown) => {
				setLoadError(err instanceof ApiError ? err.message : "Could not load the library.");
			})
			.finally(() => setLoading(false));
	}, []);

	useEffect(() => {
		load();
	}, [load]);

	const connected = data?.mode === "connected";
	const items = data?.items ?? [];
	const families = data?.families ?? [];

	const filteredItems = useMemo(() => {
		const term = search.trim().toLowerCase();
		return items.filter((item) => {
			if (!matchesPresenceFilter(item, presenceFilter)) return false;
			if (selectedFamily && item.product !== selectedFamily) return false;
			if (!term) return true;
			return (
				item.external_id.toLowerCase().includes(term) ||
				(item.product ?? "").toLowerCase().includes(term) ||
				(item.version ?? "").toLowerCase().includes(term)
			);
		});
		// `items` is a fresh `[]` fallback each render when `data` is null; `data` is the actual stable dependency.
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, [data, presenceFilter, selectedFamily, search]);

	const missingCount = items.filter((i) => i.presence === "in_depot" || i.presence === "missing").length;
	const presentCount = items.length - missingCount;

	const canQueue = user ? roleAtLeast(user.role, "Operator") : false;
	const queueGate = user
		? roleGateProps(user.role, "Operator", `Requires Operator or Admin — not available to ${user.role}`)
		: { disabled: true };

	const doExport = useCallback(async () => {
		setExporting(true);
		setExportError(null);
		try {
			const manifest = await fetchLibraryRequestManifest();
			const stamp = manifest.generated_at.slice(0, 10);
			downloadManifest(requestManifestToBlob(manifest), `waypoint-library-request-manifest-${stamp}.json`);
		} catch (err) {
			setExportError(err instanceof ApiError ? err.message : "Could not export the request manifest.");
		} finally {
			setExporting(false);
		}
	}, []);

	const primaryAction = connected
		? {
				label: "Queue missing in catalog",
				onClick: () => navigate("/catalog"),
				gate: queueGate,
				disabled: queueGate.disabled || !canQueue,
			}
		: {
				label: exporting ? "Exporting…" : "Export request manifest",
				onClick: doExport,
				gate: {},
				disabled: exporting,
			};

	return (
		<div className="library-screen">
			<aside className="library-rail">
				<div className="library-rail__title">PRODUCT FAMILIES</div>
				<ul className="library-family-list">
					<li>
						<button
							type="button"
							className={`library-family ${selectedFamily === null ? "is-selected" : ""}`}
							onClick={() => setSelectedFamily(null)}
						>
							<span className="library-family__name">All items</span>
							<span className="mono library-family__count">{items.length}</span>
						</button>
					</li>
					{families.map((family) => (
						<FamilyRow
							key={family.name}
							family={family}
							connected={connected}
							selected={selectedFamily === family.name}
							onSelect={() => setSelectedFamily(family.name)}
						/>
					))}
				</ul>
			</aside>

			<div className="library-main">
				<div className="library-filterbar">
					<input
						type="search"
						className="library-filterbar__search"
						placeholder="search library…"
						value={search}
						onChange={(e) => setSearch(e.target.value)}
						aria-label="Search library"
					/>
					<select
						value={presenceFilter}
						onChange={(e) => setPresenceFilter(e.target.value as LibraryPresenceFilter)}
						aria-label="Filter by presence"
					>
						<option value="all">All items</option>
						<option value="present">Present only</option>
						<option value="missing">Missing only</option>
					</select>
					<div className="library-filterbar__spacer" />
					<div className="mono library-filterbar__summary">
						{loading ? "loading…" : `${presentCount} present · ${missingCount} ${connected ? "available in depot" : "missing"}`}
					</div>
				</div>

				<div className={`library-note library-note--${connected ? "info" : "warn"}`}>
					<span className="library-note__dot" />
					<span>
						{connected
							? "Library reflects what is on this appliance. Items marked “in depot” are entitled and indexed but not yet downloaded — queue them from the Download Catalog."
							: `Air-gapped. Presence is evaluated against the local catalog. ${missingCount} referenced artifacts are not present locally — export a request manifest for the connected appliance to fulfil.`}
					</span>
				</div>

				{loadError && <div className="library-screen__error">{loadError}</div>}

				<div className="library-table-wrap">
					<table className="library-table">
						<thead>
							<tr>
								<th className="library-col-item">ITEM</th>
								<th>VERSION</th>
								<th>SIZE</th>
								<th>PRESENCE</th>
								<th className="library-col-source">PROVENANCE</th>
							</tr>
						</thead>
						<tbody>
							{filteredItems.map((item) => (
								<LibraryRow key={item.id} item={item} />
							))}
							{!loading && filteredItems.length === 0 && (
								<tr>
									<td colSpan={5} className="library-table__empty">
										No items match the current filters.
									</td>
								</tr>
							)}
						</tbody>
					</table>
				</div>

				<div className="library-footer">
					<div className="library-footer__summary">
						{connected
							? "Content library sync and downloads are queued from the Download Catalog."
							: "Missing items can be written to a request file for the connected appliance to fulfil."}
					</div>
					<div className="library-footer__spacer" />
					{exportError && <div className="library-footer__error">{exportError}</div>}
					<button
						type="button"
						className="library-footer__action"
						onClick={primaryAction.onClick}
						disabled={primaryAction.disabled}
						{...("title" in primaryAction.gate ? { title: primaryAction.gate.title } : {})}
					>
						{primaryAction.label}
					</button>
				</div>
			</div>
		</div>
	);
}

function FamilyRow({
	family,
	connected,
	selected,
	onSelect,
}: {
	family: LibraryFamily;
	connected: boolean;
	selected: boolean;
	onSelect: () => void;
}) {
	const hasMissing = family.missing_count > 0 && !connected;
	return (
		<li>
			<button type="button" className={`library-family ${selected ? "is-selected" : ""}`} onClick={onSelect}>
				<span className="library-family__name">{family.name}</span>
				<span className="mono library-family__count">{family.present_count}</span>
				{hasMissing && <span className="mono library-family__missing">−{family.missing_count}</span>}
			</button>
		</li>
	);
}

function LibraryRow({ item }: { item: LibraryItem }) {
	return (
		<tr>
			<td className="library-col-item">
				<div className="mono library-item__name">{item.external_id}</div>
				{item.product && <div className="library-item__product">{item.product}</div>}
			</td>
			<td className="mono">{item.version ?? "—"}</td>
			<td className="mono">{formatBytes(item.size_bytes)}</td>
			<td>
				<span className={`library-badge library-badge--${item.presence}`}>{PRESENCE_LABELS[item.presence]}</span>
			</td>
			<td className="mono library-col-source">{item.provenance}</td>
		</tr>
	);
}
