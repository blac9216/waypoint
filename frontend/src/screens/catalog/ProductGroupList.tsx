/**
 * Grouped rendering for the Download Catalog (issue #796): one collapsible
 * section per product (`ArtifactTable` reused inside each), core
 * infrastructure ordered first, Kubernetes-stack products collapsed by
 * default so the real 1,088-entry catalog's 433 VKR rows don't bury the
 * ~40 products an operator is actually looking for. A user's manual
 * expand/collapse of a group overrides that default for the life of this
 * mount.
 */
import { useState } from "react";
import type { CatalogArtifact, DownloadQueueItem, ProductGroup } from "./catalog";
import { ArtifactTable } from "./ArtifactTable";
import "./ProductGroupList.css";

export interface ProductGroupListProps {
	groups: ProductGroup[];
	loading: boolean;
	selected: Set<string>;
	onToggle: (id: string) => void;
	onToggleGroup: (ids: string[]) => void;
	byArtifact: Map<string, DownloadQueueItem>;
	onRetry: (id: string) => void;
	canQueue: boolean;
}

export function ProductGroupList({
	groups,
	loading,
	selected,
	onToggle,
	onToggleGroup,
	byArtifact,
	onRetry,
	canQueue,
}: ProductGroupListProps) {
	const [expandOverrides, setExpandOverrides] = useState<Map<string, boolean>>(new Map());

	if (loading) {
		return <div className="product-group-list__empty">Loading catalog…</div>;
	}
	if (groups.length === 0) {
		return <div className="product-group-list__empty">No artifacts match the current filters.</div>;
	}

	return (
		<div className="product-group-list">
			{groups.map((group) => {
				const defaultExpanded = group.type !== "kubernetes";
				const expanded = expandOverrides.get(group.key) ?? defaultExpanded;
				const selectedCount = group.artifacts.reduce((n, a) => n + (selected.has(a.id) ? 1 : 0), 0);
				const artifactIds = group.artifacts.map((a: CatalogArtifact) => a.id);
				return (
					<section
						key={group.key}
						className={`product-group${group.type === "kubernetes" ? " product-group--kubernetes" : ""}`}
					>
						<button
							type="button"
							className="product-group__header"
							aria-expanded={expanded}
							onClick={() => setExpandOverrides((prev) => new Map(prev).set(group.key, !expanded))}
						>
							<span className="product-group__chevron" aria-hidden="true">
								{expanded ? "▾" : "▸"}
							</span>
							<span className="product-group__name">{group.friendlyName}</span>
							<span className="product-group__key mono">{group.key}</span>
							<span className="product-group__counts mono">
								{group.versionCount} version{group.versionCount === 1 ? "" : "s"} · {group.artifacts.length} artifact
								{group.artifacts.length === 1 ? "" : "s"}
								{selectedCount > 0 ? ` · ${selectedCount} selected` : ""}
							</span>
						</button>
						{expanded && (
							<ArtifactTable
								artifacts={group.artifacts}
								loading={false}
								selected={selected}
								onToggle={onToggle}
								onToggleAll={() => onToggleGroup(artifactIds)}
								byArtifact={byArtifact}
								onRetry={onRetry}
								canQueue={canQueue}
							/>
						)}
					</section>
				);
			})}
		</div>
	);
}
