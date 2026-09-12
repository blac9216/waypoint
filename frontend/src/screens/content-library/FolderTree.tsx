/**
 * `FolderTree` — the virtual-folder panel for the content library (issue
 * #1422, epic #1185), rendered from `ContentLibraryFoldersController`'s
 * `GET .../folders` tree (issue #1389, merged). Selection drives
 * `ContentLibraryScreen`'s deep-linked `folder` query param (AC2); create/
 * rename/delete are delegated to caller-supplied `onCreate`/`onRename`/
 * `onDelete` callbacks (which call the folder API and re-fetch the tree),
 * following the same "caller owns data, this renders + calls back" contract
 * `LibraryViewShell` established.
 *
 * Role gating (this issue's AC3, README "Roles & Permissions" convention):
 * create/rename are `[RequireOperatorRole]` server-side, delete is
 * `[RequireAdminRole]` (`ContentLibraryFoldersController`'s own header
 * comment) — every gated control here stays visible but disabled via
 * `roleGateProps`, exactly like `LibraryScreen.tsx`'s queue action.
 */
import { useState } from "react";
import { ApiError } from "../../lib/api";
import { roleGateProps, type Role } from "../../lib/roles";
import type { ContentLibraryFolderNode } from "./content-library";
import "./FolderTree.css";

export interface FolderTreeProps {
	folders: ContentLibraryFolderNode[];
	selectedFolderId: string | undefined;
	onSelect: (folderId: string | undefined) => void;
	role: Role | undefined;
	onCreate: (name: string, parentFolderId: string | null) => Promise<void>;
	/** `parentFolderId` is the node's CURRENT parent, unchanged by a rename —
	 * `renameContentLibraryFolder` applies both fields on every call (a full
	 * replace, not a partial patch), so this component must resend it. */
	onRename: (folderId: string, name: string, parentFolderId: string | null) => Promise<void>;
	onDelete: (folderId: string) => Promise<void>;
}

export function FolderTree({ folders, selectedFolderId, onSelect, role, onCreate, onRename, onDelete }: FolderTreeProps) {
	const [error, setError] = useState<string | null>(null);
	const [creatingRoot, setCreatingRoot] = useState(false);
	const [newRootName, setNewRootName] = useState("");

	const organizeGate = role
		? roleGateProps(role, "Operator", `Requires Operator or Admin — folder changes are not available to ${role}`)
		: { disabled: true };
	const deleteGate = role
		? roleGateProps(role, "Admin", `Requires Admin — deleting a folder is not available to ${role}`)
		: { disabled: true };

	async function runOrReport(action: () => Promise<void>, fallback: string) {
		setError(null);
		try {
			await action();
		} catch (err) {
			setError(err instanceof ApiError ? err.message : fallback);
		}
	}

	const submitNewRoot = async () => {
		const name = newRootName.trim();
		if (!name) return;
		await runOrReport(() => onCreate(name, null), "Could not create the folder.");
		setNewRootName("");
		setCreatingRoot(false);
	};

	return (
		<nav className="folder-tree" aria-label="Folders">
			<div className="folder-tree__header">
				<button
					type="button"
					className={`folder-tree__row folder-tree__row--root ${!selectedFolderId ? "is-selected" : ""}`}
					onClick={() => onSelect(undefined)}
				>
					All items
				</button>
				{!creatingRoot && (
					<button
						type="button"
						className="folder-tree__new-button"
						{...organizeGate}
						onClick={() => setCreatingRoot(true)}
						title={organizeGate.title ?? "New folder"}
					>
						+ New folder
					</button>
				)}
			</div>

			{creatingRoot && (
				<form
					className="folder-tree__create-form"
					onSubmit={(e) => {
						e.preventDefault();
						void submitNewRoot();
					}}
				>
					<input
						autoFocus
						aria-label="New folder name"
						value={newRootName}
						onChange={(e) => setNewRootName(e.target.value)}
					/>
					<button type="submit">Create</button>
					<button type="button" onClick={() => setCreatingRoot(false)}>
						Cancel
					</button>
				</form>
			)}

			{error && <div className="folder-tree__error">{error}</div>}

			<ul className="folder-tree__list">
				{folders.map((node) => (
					<FolderNodeRow
						key={node.id}
						node={node}
						depth={0}
						parentFolderId={null}
						selectedFolderId={selectedFolderId}
						onSelect={onSelect}
						organizeGate={organizeGate}
						deleteGate={deleteGate}
						onCreate={onCreate}
						onRename={onRename}
						onDelete={onDelete}
						runOrReport={runOrReport}
					/>
				))}
			</ul>
		</nav>
	);
}

interface FolderNodeRowProps {
	node: ContentLibraryFolderNode;
	depth: number;
	parentFolderId: string | null;
	selectedFolderId: string | undefined;
	onSelect: (folderId: string | undefined) => void;
	organizeGate: { disabled: boolean; style?: { opacity: number }; title?: string };
	deleteGate: { disabled: boolean; style?: { opacity: number }; title?: string };
	onCreate: (name: string, parentFolderId: string | null) => Promise<void>;
	onRename: (folderId: string, name: string, parentFolderId: string | null) => Promise<void>;
	onDelete: (folderId: string) => Promise<void>;
	runOrReport: (action: () => Promise<void>, fallback: string) => Promise<void>;
}

function FolderNodeRow({
	node,
	depth,
	parentFolderId,
	selectedFolderId,
	onSelect,
	organizeGate,
	deleteGate,
	onCreate,
	onRename,
	onDelete,
	runOrReport,
}: FolderNodeRowProps) {
	const [renaming, setRenaming] = useState(false);
	const [renameValue, setRenameValue] = useState(node.name);
	const [creatingChild, setCreatingChild] = useState(false);
	const [newChildName, setNewChildName] = useState("");

	const submitRename = async () => {
		const name = renameValue.trim();
		if (!name || name === node.name) {
			setRenaming(false);
			return;
		}
		await runOrReport(() => onRename(node.id, name, parentFolderId), "Could not rename the folder.");
		setRenaming(false);
	};

	const submitNewChild = async () => {
		const name = newChildName.trim();
		if (!name) return;
		await runOrReport(() => onCreate(name, node.id), "Could not create the folder.");
		setNewChildName("");
		setCreatingChild(false);
	};

	const doDelete = async () => {
		if (!window.confirm(`Delete folder "${node.name}"? This only works if it's empty.`)) {
			return;
		}
		await runOrReport(() => onDelete(node.id), "Could not delete the folder.");
	};

	return (
		<li className="folder-tree__node" style={{ marginLeft: depth * 16 }}>
			{renaming ? (
				<form
					className="folder-tree__rename-form"
					onSubmit={(e) => {
						e.preventDefault();
						void submitRename();
					}}
				>
					<input aria-label={`Rename ${node.name}`} value={renameValue} onChange={(e) => setRenameValue(e.target.value)} autoFocus />
					<button type="submit">Save</button>
					<button type="button" onClick={() => setRenaming(false)}>
						Cancel
					</button>
				</form>
			) : (
				<div className="folder-tree__row-wrap">
					<button
						type="button"
						className={`folder-tree__row ${selectedFolderId === node.id ? "is-selected" : ""}`}
						onClick={() => onSelect(node.id)}
					>
						{node.name} <span className="mono folder-tree__count">{node.item_ids.length}</span>
					</button>
					<button
						type="button"
						className="folder-tree__action"
						{...organizeGate}
						onClick={() => setRenaming(true)}
						title={organizeGate.title ?? "Rename"}
					>
						Rename
					</button>
					<button
						type="button"
						className="folder-tree__action"
						{...organizeGate}
						onClick={() => setCreatingChild(true)}
						title={organizeGate.title ?? "New subfolder"}
					>
						+ Sub
					</button>
					<button
						type="button"
						className="folder-tree__action folder-tree__action--delete"
						{...deleteGate}
						onClick={() => void doDelete()}
						title={deleteGate.title ?? "Delete"}
					>
						Delete
					</button>
				</div>
			)}

			{creatingChild && (
				<form
					className="folder-tree__create-form"
					onSubmit={(e) => {
						e.preventDefault();
						void submitNewChild();
					}}
				>
					<input
						autoFocus
						aria-label={`New subfolder of ${node.name}`}
						value={newChildName}
						onChange={(e) => setNewChildName(e.target.value)}
					/>
					<button type="submit">Create</button>
					<button type="button" onClick={() => setCreatingChild(false)}>
						Cancel
					</button>
				</form>
			)}

			{node.children.length > 0 && (
				<ul className="folder-tree__list">
					{node.children.map((child) => (
						<FolderNodeRow
							key={child.id}
							node={child}
							depth={depth + 1}
							parentFolderId={node.id}
							selectedFolderId={selectedFolderId}
							onSelect={onSelect}
								organizeGate={organizeGate}
							deleteGate={deleteGate}
							onCreate={onCreate}
							onRename={onRename}
							onDelete={onDelete}
							runOrReport={runOrReport}
						/>
					))}
				</ul>
			)}
		</li>
	);
}
