/**
 * LOCAL STORES usage widget — docs/ui/prototype/README.md screen 6 right
 * rail: "LOCAL STORES (depot mirror / content library / photon repo with
 * usage bars)". Bound to `GET /system`'s disk-usage-by-store fields
 * (api-contract.md "System, users, audit"). Issue #226 is landing those
 * fields on the backend concurrently with this screen; `SystemInfo` in
 * lib/system.tsx does not model them yet (deliberately — that provider only
 * carries the chrome-relevant subset), so this widget fetches `/system`
 * itself and renders a graceful loading/empty state rather than crashing or
 * fabricating numbers when the fields are absent.
 */
import { useEffect, useState } from "react";
import { apiGet } from "../../lib/api";
import { formatBytes } from "./catalog";
import type { SystemDiskUsage, StoreUsage } from "./catalog";

export function StoresUsagePanel() {
	const [usage, setUsage] = useState<SystemDiskUsage | null>(null);
	const [loading, setLoading] = useState(true);

	useEffect(() => {
		let cancelled = false;
		apiGet<SystemDiskUsage>("/system")
			.then((res) => {
				if (!cancelled) {
					setUsage(res);
				}
			})
			.catch(() => {
				// Best-effort — the widget just stays in its empty state.
			})
			.finally(() => {
				if (!cancelled) {
					setLoading(false);
				}
			});
		return () => {
			cancelled = true;
		};
	}, []);

	const stores = usage?.stores ?? [];

	return (
		<div className="catalog-panel">
			<div className="catalog-panel__title">LOCAL STORES</div>
			{loading && <div className="catalog-panel__empty">Loading…</div>}
			{!loading && stores.length === 0 && (
				<div className="catalog-panel__empty">Disk usage is not reported by this appliance yet.</div>
			)}
			{!loading && stores.length > 0 && (
				<ul className="catalog-stores-list">
					{stores.map((store) => (
						<StoreBar key={store.name} store={store} />
					))}
				</ul>
			)}
		</div>
	);
}

function StoreBar({ store }: { store: StoreUsage }) {
	const pct = store.total_bytes > 0 ? Math.min(100, Math.round((store.used_bytes / store.total_bytes) * 100)) : 0;
	return (
		<li className="catalog-store">
			<div className="catalog-store__label mono">{store.name}</div>
			<div className="catalog-store__bar">
				<div className="catalog-store__bar-fill" style={{ width: `${pct}%` }} />
			</div>
			<div className="catalog-store__meta mono">
				{formatBytes(store.used_bytes)} of {formatBytes(store.total_bytes)}
			</div>
		</li>
	);
}
