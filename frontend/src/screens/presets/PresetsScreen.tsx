/**
 * Presets screen (issue #1469, epic #1182) — the entry point of the
 * subscription flow (adopt -> edit -> subscription), consuming
 * `PresetsController` (issue #1450). Lists every shipped and custom preset
 * (`GET /presets`); Admin-only actions clone a preset to an independent
 * custom row (`POST /presets/{id}/clone`) and edit a custom clone in place
 * (`PUT /presets/{id}`) — a write to a shipped preset is rejected server-side
 * with 409 `preset_not_custom`, surfaced here rather than silently retried.
 *
 * Adopt navigates to the subscription editor (issue #1473, this issue's
 * sibling) with the preset id pre-filled — that screen owns
 * `POST /subscriptions` (the actual adopt-a-preset write); this screen never
 * calls it directly, keeping its own scope to presets only.
 */
import { Fragment, useCallback, useEffect, useMemo, useState } from "react";
import { useAuth } from "../../lib/auth-context";
import { ApiError } from "../../lib/api";
import { useRouter } from "../../lib/router-context";
import { roleGateProps } from "../../lib/roles";
import { clonePreset, fetchPresets, updatePreset, type LineGranularity, type Preset, type PresetEditFields } from "./presets";
import "./PresetsScreen.css";

const LINE_GRANULARITIES: LineGranularity[] = ["subminor", "minor", "major"];

interface EditFormState {
	name: string;
	line_granularity: LineGranularity;
	anchor_version: string;
}

function toFormState(preset: Preset): EditFormState {
	return {
		name: preset.name,
		line_granularity: preset.line_granularity,
		anchor_version: preset.anchor_version ?? "",
	};
}

export function PresetsScreen() {
	const { user } = useAuth();
	const { navigate } = useRouter();

	const [presets, setPresets] = useState<Preset[]>([]);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);
	const [actionError, setActionError] = useState<string | null>(null);
	const [busyId, setBusyId] = useState<string | null>(null);
	const [editingId, setEditingId] = useState<string | null>(null);
	const [editForm, setEditForm] = useState<EditFormState | null>(null);

	const load = useCallback(() => {
		setLoading(true);
		setLoadError(null);
		fetchPresets()
			.then((rows) => setPresets(rows))
			.catch((err: unknown) => {
				setLoadError(err instanceof ApiError ? err.message : "Could not load presets.");
			})
			.finally(() => setLoading(false));
	}, []);

	useEffect(() => {
		load();
	}, [load]);

	const adminGate = user ? roleGateProps(user.role, "Admin") : { disabled: true };

	const handleAdopt = useCallback(
		(preset: Preset) => {
			navigate(`/subscriptions/new?preset=${encodeURIComponent(preset.id)}`);
		},
		[navigate],
	);

	const handleClone = useCallback(
		async (preset: Preset) => {
			setBusyId(preset.id);
			setActionError(null);
			try {
				await clonePreset(preset.id);
				load();
			} catch (err) {
				setActionError(err instanceof ApiError ? err.message : "Could not clone this preset.");
			} finally {
				setBusyId(null);
			}
		},
		[load],
	);

	const startEdit = useCallback((preset: Preset) => {
		setActionError(null);
		setEditingId(preset.id);
		setEditForm(toFormState(preset));
	}, []);

	const cancelEdit = useCallback(() => {
		setEditingId(null);
		setEditForm(null);
	}, []);

	const handleSaveEdit = useCallback(
		async (preset: Preset) => {
			if (!editForm) {
				return;
			}
			setBusyId(preset.id);
			setActionError(null);
			try {
				const fields: PresetEditFields = {
					name: editForm.name,
					line_granularity: editForm.line_granularity,
					anchor_version: editForm.anchor_version || undefined,
				};
				await updatePreset(preset.id, fields);
				setEditingId(null);
				setEditForm(null);
				load();
			} catch (err) {
				if (err instanceof ApiError && err.code === "preset_not_custom") {
					setActionError(`"${preset.name}" is a shipped preset and is read-only — clone it first to edit.`);
				} else {
					setActionError(err instanceof ApiError ? err.message : "Could not save this preset.");
				}
			} finally {
				setBusyId(null);
			}
		},
		[editForm, load],
	);

	const rows = useMemo(() => presets, [presets]);

	return (
		<div className="presets-screen">
			<div className="presets-screen__header">
				<h1>Presets</h1>
				<p className="presets-screen__subtitle">
					Shipped starting points and custom clones. Adopt into a subscription, clone to a custom preset, or edit an existing clone.
				</p>
			</div>

			{loadError && <div className="presets-screen__error">{loadError}</div>}
			{actionError && <div className="presets-screen__error">{actionError}</div>}

			<table className="presets-table">
				<thead>
					<tr>
						<th>NAME</th>
						<th>STACK</th>
						<th>GENERATION</th>
						<th>GRANULARITY</th>
						<th>ANCHOR VERSION</th>
						<th>TYPE</th>
						<th className="presets-col-actions">ACTIONS</th>
					</tr>
				</thead>
				<tbody>
					{rows.map((preset) => {
						const isEditing = editingId === preset.id;
						const editReadOnlyGate = preset.is_custom
							? adminGate
							: { disabled: true, title: "Shipped presets are read-only — clone to edit.", style: { opacity: 0.42 } };
						return (
							<Fragment key={preset.id}>
								<tr>
									<td>{preset.name}</td>
									<td>{preset.stack}</td>
									<td>{preset.generation}</td>
									<td className="mono">{preset.line_granularity}</td>
									<td className="mono">{preset.anchor_version ?? "—"}</td>
									<td>
										<span className={`presets-badge presets-badge--${preset.is_custom ? "custom" : "shipped"}`}>
											{preset.is_custom ? "custom" : "shipped"}
										</span>
									</td>
									<td className="presets-col-actions">
										<button type="button" onClick={() => handleAdopt(preset)} disabled={adminGate.disabled} title={adminGate.title}>
											Adopt
										</button>
										<button
											type="button"
											onClick={() => handleClone(preset)}
											disabled={adminGate.disabled || busyId === preset.id}
											title={adminGate.title}
										>
											Clone
										</button>
										<button
											type="button"
											onClick={() => (isEditing ? cancelEdit() : startEdit(preset))}
											disabled={editReadOnlyGate.disabled || busyId === preset.id}
											title={editReadOnlyGate.title}
										>
											{isEditing ? "Cancel" : "Edit"}
										</button>
									</td>
								</tr>
								{isEditing && editForm && (
									<tr className="presets-edit-row">
										<td colSpan={7}>
											<div className="presets-edit-form" aria-label={`Edit ${preset.name}`}>
												<label>
													Name
													<input
														type="text"
														value={editForm.name}
														onChange={(e) => setEditForm({ ...editForm, name: e.target.value })}
													/>
												</label>
												<label>
													Line granularity
													<select
														value={editForm.line_granularity}
														onChange={(e) => setEditForm({ ...editForm, line_granularity: e.target.value as LineGranularity })}
													>
														{LINE_GRANULARITIES.map((g) => (
															<option key={g} value={g}>
																{g}
															</option>
														))}
													</select>
												</label>
												<label>
													Anchor version
													<input
														type="text"
														value={editForm.anchor_version}
														onChange={(e) => setEditForm({ ...editForm, anchor_version: e.target.value })}
													/>
												</label>
												<button type="button" onClick={() => handleSaveEdit(preset)} disabled={busyId === preset.id}>
													Save
												</button>
												<button type="button" onClick={cancelEdit}>
													Cancel
												</button>
											</div>
										</td>
									</tr>
								)}
							</Fragment>
						);
					})}
					{!loading && rows.length === 0 && (
						<tr>
							<td colSpan={7} className="presets-table__empty">
								No presets available.
							</td>
						</tr>
					)}
				</tbody>
			</table>
		</div>
	);
}
