/**
 * SITES sidebar — docs/ui/prototype/README.md "Sidebar lists sites with
 * target counts and their STIG Manager binding (inherit or override)." Also
 * carries the site-level create/edit/delete forms (not in the static
 * prototype, which only mocks reads) since #237's acceptance criteria
 * requires full CRUD for sites, not just targets.
 */
import { useState } from "react";
import { useAuth } from "../../lib/auth";
import { roleAtLeast, roleGateProps } from "../../lib/roles";
import type { Site } from "./sites";
import "./ConfigurationScreen.css";

interface SiteFormState {
	name: string;
	description: string;
}

const EMPTY_FORM: SiteFormState = { name: "", description: "" };

export function SitesSidebar({
	sites,
	selectedId,
	onSelect,
	onCreate,
	onUpdate,
	onDelete,
	saving,
	formError,
}: {
	sites: Site[];
	selectedId: string | null;
	onSelect: (id: string) => void;
	onCreate: (input: SiteFormState) => void;
	onUpdate: (id: string, input: SiteFormState) => void;
	onDelete: (id: string, name: string) => void;
	saving: boolean;
	formError: string | null;
}) {
	const { user } = useAuth();
	const canWrite = user ? roleAtLeast(user.role, "Admin") : false;
	const writeGate = user ? roleGateProps(user.role, "Admin", `Requires Admin — this action is not available to ${user.role}`) : { disabled: true };

	const [creating, setCreating] = useState(false);
	const [createForm, setCreateForm] = useState<SiteFormState>(EMPTY_FORM);
	const [editingId, setEditingId] = useState<string | null>(null);
	const [editForm, setEditForm] = useState<SiteFormState>(EMPTY_FORM);

	return (
		<div className="config-panel config-sidebar">
			<div className="config-panel__header">
				<div className="config-panel__title">SITES</div>
				<div className="config-panel__spacer" />
				<button
					type="button"
					{...writeGate}
					onClick={() => {
						setCreating((v) => !v);
						setEditingId(null);
					}}
				>
					{creating ? "Cancel" : "Add site"}
				</button>
			</div>

			{formError && <div className="config-panel__error">{formError}</div>}

			{creating && canWrite && (
				<SiteForm
					form={createForm}
					setForm={setCreateForm}
					saving={saving}
					onCancel={() => setCreating(false)}
					onSubmit={() => {
						onCreate(createForm);
						setCreateForm(EMPTY_FORM);
						setCreating(false);
					}}
				/>
			)}

			{sites.length === 0 && <div className="config-sidebar__empty">No sites yet.</div>}

			{sites.map((site) => (
				<div key={site.id} className={`config-sidebar__item ${selectedId === site.id ? "is-selected" : ""}`}>
					<button type="button" className="config-sidebar__select" onClick={() => onSelect(site.id)}>
						<div className="config-sidebar__name">{site.name}</div>
						{site.description && <div className="config-sidebar__desc">{site.description}</div>}
					</button>
					<div className="config-sidebar__row-actions">
						<button
							type="button"
							{...writeGate}
							onClick={() => {
								setEditForm({ name: site.name, description: site.description ?? "" });
								setEditingId(editingId === site.id ? null : site.id);
								setCreating(false);
							}}
						>
							{editingId === site.id ? "Close" : "Edit"}
						</button>
						<button type="button" {...writeGate} onClick={() => onDelete(site.id, site.name)}>
							Delete
						</button>
					</div>
					{editingId === site.id && canWrite && (
						<SiteForm
							form={editForm}
							setForm={setEditForm}
							saving={saving}
							onCancel={() => setEditingId(null)}
							onSubmit={() => {
								onUpdate(site.id, editForm);
								setEditingId(null);
							}}
						/>
					)}
				</div>
			))}
		</div>
	);
}

function SiteForm({
	form,
	setForm,
	saving,
	onCancel,
	onSubmit,
}: {
	form: SiteFormState;
	setForm: (f: SiteFormState) => void;
	saving: boolean;
	onCancel: () => void;
	onSubmit: () => void;
}) {
	const canSubmit = form.name.trim().length > 0;
	return (
		<form
			className="config-form"
			onSubmit={(e) => {
				e.preventDefault();
				if (canSubmit) onSubmit();
			}}
		>
			<div className="config-form__grid config-form__grid--single">
				<label className="config-form__field">
					<span>Name</span>
					<input
						value={form.name}
						onChange={(e) => setForm({ ...form, name: e.target.value })}
						placeholder="e.g. Alpha Enclave"
						required
					/>
				</label>
				<label className="config-form__field">
					<span>Description</span>
					<input
						value={form.description}
						onChange={(e) => setForm({ ...form, description: e.target.value })}
						placeholder="optional"
					/>
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
