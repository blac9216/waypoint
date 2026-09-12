/**
 * Subscription editor — create/edit (issue #1473, epic #1182). Wired
 * against #1450's real `SubscriptionsController` (`POST /subscriptions`,
 * `GET`/`PUT /subscriptions/{id}`) rather than the pre-#1450 design-record
 * assumption of a multi-line/projected-size API — see `subscriptions.ts`'s
 * doc comment for the full reinterpretation.
 *
 * Three ways to land here:
 *   - `/subscriptions/new` — blank create.
 *   - `/subscriptions/new?preset=<id>` — the presets screen's (#1469) adopt
 *     path: fetches that preset and pre-fills `line_granularity`/
 *     `anchor_version`/`preset_id`; those two fields are then disabled
 *     client-side (the server ignores any value sent for them on an adopt
 *     write anyway, per api-contract.md's "Subscriptions & presets" note).
 *   - `/subscriptions/new?id=<id>` — edit: fetches the existing subscription
 *     and saves back via `PUT` instead of `POST`.
 *
 * All writes are Admin-only (RBAC decision R2-10), gated the same
 * visible-but-disabled way `PresetsScreen.tsx` gates clone/edit.
 */
import { useCallback, useEffect, useMemo, useState, type FormEvent } from "react";
import { useAuth } from "../../lib/auth-context";
import { ApiError } from "../../lib/api";
import { roleGateProps } from "../../lib/roles";
import { fetchCatalogArtifacts, type CatalogArtifact } from "../catalog/catalog";
import { fetchPreset, type LineGranularity } from "../presets/presets";
import {
	computeProjectedSizeBytes,
	createSubscription,
	fetchSubscription,
	formatBytes,
	updateSubscription,
	type Subscription,
	type SubscriptionWriteFields,
} from "./subscriptions";
import "./SubscriptionEditorScreen.css";

const LINE_GRANULARITIES: LineGranularity[] = ["subminor", "minor", "major"];

/** `RepoStores.All` (`RepoCredentialDtos.cs`) — the closed acquisition-lane
 * vocabulary `subscriptions.lane` reuses (api-contract.md "Subscriptions &
 * presets": "`lane` ... one of `RepoStores.All`"). */
const LANES = ["depot", "umds", "photon", "vmtools", "vks", "content-libraries"];

interface FormState {
	product: string;
	lane: string;
	line_granularity: LineGranularity;
	anchor_version: string;
	refresh_window_days: string;
	retention_override_days: string;
	is_enabled: boolean;
	preset_id: string | null;
}

const BLANK_FORM: FormState = {
	product: "",
	lane: LANES[0],
	line_granularity: "minor",
	anchor_version: "",
	refresh_window_days: "",
	retention_override_days: "",
	is_enabled: true,
	preset_id: null,
};

function fromSubscription(subscription: Subscription): FormState {
	return {
		product: subscription.product,
		lane: subscription.lane,
		line_granularity: subscription.line_granularity,
		anchor_version: subscription.anchor_version,
		refresh_window_days: subscription.refresh_window_days?.toString() ?? "",
		retention_override_days: subscription.retention_override_days?.toString() ?? "",
		is_enabled: subscription.is_enabled,
		preset_id: subscription.preset_id,
	};
}

function toFields(form: FormState): SubscriptionWriteFields {
	return {
		product: form.product,
		lane: form.lane,
		line_granularity: form.line_granularity,
		anchor_version: form.anchor_version,
		preset_id: form.preset_id,
		refresh_window_days: form.refresh_window_days ? Number(form.refresh_window_days) : null,
		retention_override_days: form.retention_override_days ? Number(form.retention_override_days) : null,
		is_enabled: form.is_enabled,
	};
}

/** Reads `?preset=`/`?id=` off `/subscriptions/new`. The router (`router.tsx`)
 * deliberately keeps query strings out of its own `path` state — consumers
 * that need one read `window.location.search` directly, the same precedent
 * `useRunIdFromQuery.ts` set for `?run=`. */
function useEditorQuery(): { presetId?: string; subscriptionId?: string } {
	const [query, setQuery] = useState(() => new URLSearchParams(window.location.search));
	useEffect(() => {
		const sync = () => setQuery(new URLSearchParams(window.location.search));
		window.addEventListener("popstate", sync);
		return () => window.removeEventListener("popstate", sync);
	}, []);
	return { presetId: query.get("preset") ?? undefined, subscriptionId: query.get("id") ?? undefined };
}

export function SubscriptionEditorScreen() {
	const { user } = useAuth();
	const { presetId, subscriptionId } = useEditorQuery();
	const isEditMode = Boolean(subscriptionId);

	const [form, setForm] = useState<FormState>(BLANK_FORM);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);
	const [saveError, setSaveError] = useState<string | null>(null);
	const [saving, setSaving] = useState(false);
	const [saved, setSaved] = useState(false);
	const [artifacts, setArtifacts] = useState<CatalogArtifact[]>([]);

	useEffect(() => {
		let cancelled = false;
		fetchCatalogArtifacts()
			.then((res) => {
				if (!cancelled) {
					setArtifacts(res.artifacts);
				}
			})
			.catch(() => {
				// Indexed catalog metadata is a picker convenience, not a hard
				// dependency of the editor — a load failure (e.g. disconnected
				// mode) just leaves the picker empty; product/version stay
				// free-text-enterable below.
			});
		return () => {
			cancelled = true;
		};
	}, []);

	useEffect(() => {
		let cancelled = false;
		setLoading(true);
		setLoadError(null);

		const load = async () => {
			if (subscriptionId) {
				const subscription = await fetchSubscription(subscriptionId);
				if (!cancelled) {
					setForm(fromSubscription(subscription));
				}
				return;
			}
			if (presetId) {
				const preset = await fetchPreset(presetId);
				if (!cancelled) {
					setForm({
						...BLANK_FORM,
						line_granularity: preset.line_granularity,
						anchor_version: preset.anchor_version ?? "",
						preset_id: preset.id,
					});
				}
				return;
			}
			if (!cancelled) {
				setForm(BLANK_FORM);
			}
		};

		load()
			.catch((err: unknown) => {
				if (!cancelled) {
					setLoadError(err instanceof ApiError ? err.message : "Could not load the subscription editor.");
				}
			})
			.finally(() => {
				if (!cancelled) {
					setLoading(false);
				}
			});

		return () => {
			cancelled = true;
		};
	}, [subscriptionId, presetId]);

	const productOptions = useMemo(() => Array.from(new Set(artifacts.map((a) => a.product))).sort(), [artifacts]);
	const versionOptions = useMemo(
		() => Array.from(new Set(artifacts.filter((a) => a.product === form.product).map((a) => a.version))).sort(),
		[artifacts, form.product],
	);
	const projectedSizeBytes = useMemo(
		() => computeProjectedSizeBytes(artifacts.filter((a) => a.product === form.product), form.anchor_version, form.line_granularity),
		[artifacts, form.product, form.anchor_version, form.line_granularity],
	);

	const isAdopting = Boolean(form.preset_id) && !isEditMode;
	const adminGate = user ? roleGateProps(user.role, "Admin") : { disabled: true };

	const handleRemoveLine = useCallback(() => {
		setForm((prev) => ({ ...prev, product: "", anchor_version: "" }));
	}, []);

	const handleSave = useCallback(
		async (event: FormEvent) => {
			event.preventDefault();
			setSaveError(null);
			setSaved(false);
			setSaving(true);
			try {
				const fields = toFields(form);
				if (isEditMode && subscriptionId) {
					const updated = await updateSubscription(subscriptionId, fields);
					setForm(fromSubscription(updated));
				} else {
					const created = await createSubscription(fields);
					setForm(fromSubscription(created));
				}
				setSaved(true);
			} catch (err) {
				setSaveError(err instanceof ApiError ? err.message : "Could not save this subscription.");
			} finally {
				setSaving(false);
			}
		},
		[form, isEditMode, subscriptionId],
	);

	if (loading) {
		return (
			<div className="subscription-editor-screen">
				<p>Loading…</p>
			</div>
		);
	}

	return (
		<div className="subscription-editor-screen">
			<div className="subscription-editor-screen__header">
				<h1>{isEditMode ? "Edit subscription" : "New subscription"}</h1>
				<p className="subscription-editor-screen__subtitle">
					{isAdopting
						? "Adopted from a preset — line granularity and anchor version are seeded from it and cannot be changed here."
						: "A durable product/lane tracking expression (ADR-0028)."}
				</p>
			</div>

			{loadError && <div className="subscription-editor-screen__error">{loadError}</div>}
			{saveError && <div className="subscription-editor-screen__error">{saveError}</div>}
			{saved && <div className="subscription-editor-screen__success">Saved.</div>}

			<form className="subscription-editor-form" onSubmit={handleSave} aria-label="Subscription editor">
				<div className="subscription-editor-form__line-picker">
					<h2>Tracked line</h2>
					<label>
						Product
						<input
							type="text"
							list="subscription-editor-products"
							value={form.product}
							onChange={(e) => setForm({ ...form, product: e.target.value, anchor_version: "" })}
							disabled={adminGate.disabled}
							required
						/>
						<datalist id="subscription-editor-products">
							{productOptions.map((p) => (
								<option key={p} value={p} />
							))}
						</datalist>
					</label>
					<label>
						Anchor version
						<input
							type="text"
							list="subscription-editor-versions"
							value={form.anchor_version}
							onChange={(e) => setForm({ ...form, anchor_version: e.target.value })}
							disabled={adminGate.disabled || isAdopting}
							required
						/>
						<datalist id="subscription-editor-versions">
							{versionOptions.map((v) => (
								<option key={v} value={v} />
							))}
						</datalist>
					</label>
					<button type="button" onClick={handleRemoveLine} disabled={adminGate.disabled || isAdopting || !form.product}>
						Remove line
					</button>
					<div className="subscription-editor-form__projected-size" data-testid="projected-size">
						Projected size: {formatBytes(projectedSizeBytes)}
					</div>
				</div>

				<label>
					Lane
					<select value={form.lane} onChange={(e) => setForm({ ...form, lane: e.target.value })} disabled={adminGate.disabled}>
						{LANES.map((lane) => (
							<option key={lane} value={lane}>
								{lane}
							</option>
						))}
					</select>
				</label>
				<label>
					Line granularity
					<select
						value={form.line_granularity}
						onChange={(e) => setForm({ ...form, line_granularity: e.target.value as LineGranularity })}
						disabled={adminGate.disabled || isAdopting}
					>
						{LINE_GRANULARITIES.map((g) => (
							<option key={g} value={g}>
								{g}
							</option>
						))}
					</select>
				</label>
				<label>
					Refresh window (days)
					<input
						type="number"
						value={form.refresh_window_days}
						onChange={(e) => setForm({ ...form, refresh_window_days: e.target.value })}
						disabled={adminGate.disabled}
					/>
				</label>
				<label>
					Retention override (days)
					<input
						type="number"
						value={form.retention_override_days}
						onChange={(e) => setForm({ ...form, retention_override_days: e.target.value })}
						disabled={adminGate.disabled}
					/>
				</label>
				<label className="subscription-editor-form__enabled">
					<input
						type="checkbox"
						checked={form.is_enabled}
						onChange={(e) => setForm({ ...form, is_enabled: e.target.checked })}
						disabled={adminGate.disabled}
					/>
					Enabled
				</label>

				<button type="submit" disabled={adminGate.disabled || saving} title={adminGate.title}>
					{saving ? "Saving…" : "Save"}
				</button>
			</form>
		</div>
	);
}
