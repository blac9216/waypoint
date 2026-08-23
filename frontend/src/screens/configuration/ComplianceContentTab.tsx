/**
 * Config → Compliance Content tab (issue #40, frontend slice 2 of 2 — the
 * backend landed in PR #566). docs/ui/prototype's Config → Compliance
 * Content spec (`vcs-ops-console.dc.html` `cfgContent`): a COMPLIANCE
 * CONTENT SOURCE panel (repository, pinned-tag-vs-tracked-branch, recorded
 * commit, Pull Updates) plus a PROFILE INVENTORY panel (name/kind/version/
 * state) that feeds the Benchmarks screen's profile list (#559).
 *
 * Pull Updates follows the discovery-feedback pattern from
 * `SiteTargetsPanel.tsx` (issue #557/PR #562): `POST /pull` returns 202 with
 * a run id, the run is polled until terminal, and a same-tab re-pull is
 * blocked while one is in flight (duplicate-submission prevention) rather
 * than relying on the button's `disabled` alone, which a fast double
 * click / keyboard repeat can race past a single render.
 *
 * Connected-mode gating: `content-pull` fans out to `compliance-runner`,
 * which needs live GitHub access (ADR-0017) — mirrors
 * `DownloadCatalogScreen.tsx`'s `useSystem().mode` gate. Unlike the catalog
 * screen this tab is not route-gated (Compliance Content is one tab among
 * six on a single Configuration screen, not its own route), so the pull
 * action itself is disabled with a reason in air-gapped mode rather than the
 * whole tab being unreachable; the repo/ref configuration and profile
 * inventory both stay visible and readable regardless of mode, since they
 * are config-plane reads, not the connected-only pull operation.
 *
 * There is no "check for updates against upstream" endpoint in this backend
 * slice (`ComplianceContentController`'s doc comment: `/check` and
 * `/import` are follow-ups) — the only real update signal on the wire is a
 * profile inventory row whose `state` is `update_pending`, so the banner
 * below is derived from that rather than a changelog the backend does not
 * provide yet (see this PR's body for the gap).
 */
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useAuth } from "../../lib/auth-context";
import { ApiError } from "../../lib/api";
import { roleAtLeast, roleGateProps } from "../../lib/roles";
import { useSystem } from "../../lib/system-context";
import {
	fetchComplianceContentConfig,
	fetchComplianceContentPulls,
	fetchContentPullRun,
	fetchProfiles,
	formatProfileState,
	formatPullStatus,
	formatTimestamp,
	isTerminalRunState,
	putComplianceContentConfig,
	startContentPull,
	type ComplianceContentConfig,
	type ComplianceContentPull,
	type ComplianceContentRefType,
	type ComplianceContentWriteInput,
	type Profile,
} from "./compliance-content";
import "./ConfigurationScreen.css";
import "./ComplianceContentTab.css";

interface ConfigFormState {
	repository_url: string;
	ref_type: ComplianceContentRefType;
	ref_value: string;
}

const EMPTY_FORM: ConfigFormState = { repository_url: "", ref_type: "tag", ref_value: "" };

function toFormState(config: ComplianceContentConfig): ConfigFormState {
	return {
		repository_url: config.repository_url,
		ref_type: config.ref_type === "branch" ? "branch" : "tag",
		ref_value: config.ref_value,
	};
}

/** Local pull-in-flight state, same shape/rationale as `SiteTargetsPanel`'s
 * `DiscoveryUiState`: `outcome` stays `null` while polling and is set once
 * the run reaches a terminal state, so a failure keeps its retry affordance
 * across a background `load()` refetch. */
interface PullUiState {
	runId: string;
	outcome: "failed" | null;
}

export function ComplianceContentTab() {
	const { user } = useAuth();
	const { mode } = useSystem();
	const canWrite = user ? roleAtLeast(user.role, "Admin") : false;
	const writeGate = user ? roleGateProps(user.role, "Admin", `Requires Admin — this action is not available to ${user.role}`) : { disabled: true };

	const [config, setConfig] = useState<ComplianceContentConfig | null>(null);
	const [configMissing, setConfigMissing] = useState(false);
	const [pulls, setPulls] = useState<ComplianceContentPull[]>([]);
	const [profiles, setProfiles] = useState<Profile[]>([]);
	const [loading, setLoading] = useState(true);
	const [loadError, setLoadError] = useState<string | null>(null);

	const [editing, setEditing] = useState(false);
	const [form, setForm] = useState<ConfigFormState>(EMPTY_FORM);
	const [saving, setSaving] = useState(false);
	const [formError, setFormError] = useState<string | null>(null);

	const [pull, setPull] = useState<PullUiState | null>(null);
	const [pullError, setPullError] = useState<string | null>(null);
	const pollTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
	const pollGeneration = useRef(0);
	// Synchronous duplicate-submission guard: `pull` state alone is not enough
	// — two clicks in the same event-loop tick both see the same (stale)
	// `pull` via React's batched updates, so a fast double click / keyboard
	// repeat could otherwise race past the state check and fire two POSTs.
	// This ref is set/cleared synchronously around the request instead.
	const pullInFlightRef = useRef(false);

	const load = useCallback(() => {
		setLoading(true);
		setLoadError(null);
		setConfigMissing(false);
		Promise.all([
			fetchComplianceContentConfig()
				.then((cfg) => {
					setConfig(cfg);
					setForm(toFormState(cfg));
				})
				.catch((err: unknown) => {
					if (err instanceof ApiError && err.status === 404) {
						setConfig(null);
						setConfigMissing(true);
						return;
					}
					throw err;
				}),
			fetchComplianceContentPulls()
				.then(setPulls)
				.catch(() => setPulls([])),
			fetchProfiles()
				.then(setProfiles)
				.catch(() => setProfiles([])),
		])
			.catch((err: unknown) => setLoadError(err instanceof ApiError ? err.message : "Could not load compliance content configuration."))
			.finally(() => setLoading(false));
	}, []);

	useEffect(() => {
		load();
	}, [load]);

	useEffect(() => {
		return () => {
			if (pollTimer.current) {
				clearTimeout(pollTimer.current);
			}
		};
	}, []);

	const pollRun = useCallback(
		(runId: string, generation: number) => {
			const POLL_INTERVAL_MS = 3000;
			const tick = async () => {
				if (pollGeneration.current !== generation) {
					return;
				}
				try {
					const run = await fetchContentPullRun(runId);
					if (pollGeneration.current !== generation) {
						return;
					}
					if (isTerminalRunState(run.state)) {
						const failed = run.state !== "completed" || run.job_count_failed > 0 || run.job_count_blocked > 0;
						setPull({ runId, outcome: failed ? "failed" : null });
						// Terminal either way — clear the duplicate-submission guard so
						// a retry (success or failure) can fire a fresh pull.
						pullInFlightRef.current = false;
						// Refresh config/pull-history/profiles either way — a failed
						// pull still lands a row in history (issue #40 AC) and the
						// config's last-pulled-commit fields only move on success.
						load();
						if (!failed) {
							setPull(null);
						}
						return;
					}
					pollTimer.current = setTimeout(() => void tick(), POLL_INTERVAL_MS);
				} catch {
					// Transient poll failure must not abandon the queued/running
					// affordance — keep polling until the run itself resolves.
					pollTimer.current = setTimeout(() => void tick(), POLL_INTERVAL_MS);
				}
			};
			void tick();
		},
		[load],
	);

	const doPull = useCallback(async () => {
		// Duplicate-submission prevention: bail if a pull is already in flight,
		// either per the last-rendered state (covers a poll still running from
		// a prior click) or the synchronous ref (covers two clicks landing in
		// the same tick, before state has re-rendered).
		if ((pull && pull.outcome === null) || pullInFlightRef.current) {
			return;
		}
		pullInFlightRef.current = true;
		setPullError(null);
		const generation = pollGeneration.current + 1;
		pollGeneration.current = generation;
		try {
			const started = await startContentPull();
			setPull({ runId: started.run_id, outcome: null });
			pollRun(started.run_id, generation);
		} catch (err) {
			setPullError(err instanceof ApiError ? err.message : "Could not start a compliance content pull.");
			pullInFlightRef.current = false;
		}
	}, [pull, pollRun]);

	const submitConfig = useCallback(
		async (input: ComplianceContentWriteInput) => {
			setSaving(true);
			setFormError(null);
			try {
				const saved = await putComplianceContentConfig(input);
				setConfig(saved);
				setConfigMissing(false);
				setEditing(false);
			} catch (err) {
				setFormError(err instanceof ApiError ? err.message : "Could not save the compliance content configuration.");
			} finally {
				setSaving(false);
			}
		},
		[],
	);

	const disconnected = mode === "disconnected";
	const pullInFlight = pull !== null && pull.outcome === null;
	const pullGateDisabled = writeGate.disabled || disconnected || !config || pullInFlight;
	const pullGateTitle = writeGate.disabled
		? writeGate.title
		: disconnected
			? "No GitHub access in air-gapped mode — content updates arrive by transfer bundle"
			: !config
				? "Configure a repository below first"
				: pullInFlight
					? "A pull is already queued or running"
					: undefined;

	const pendingCount = useMemo(() => profiles.filter((p) => p.state === "update_pending").length, [profiles]);

	// aria-live status text for the in-flight/last-attempt pull — never
	// color-only (matches SiteTargetsPanel's discovery status convention).
	const pullStatusText = pullInFlight
		? "Pull queued or running…"
		: pull?.outcome === "failed"
			? "Last pull attempt failed — see history below."
			: null;

	return (
		<div className="config-tab config-tab--content">
			<div className="config-tab__grid">
				<div className="config-panel">
					<div className="config-panel__header">
						<div className="config-panel__title">COMPLIANCE CONTENT SOURCE</div>
						<div className="config-panel__spacer" />
						{!loading && config && !editing && (
							<button
								type="button"
								{...writeGate}
								onClick={() => {
									setEditing(true);
									setForm(toFormState(config));
									setFormError(null);
								}}
							>
								Edit
							</button>
						)}
					</div>

					{loadError && <div className="config-panel__error">{loadError}</div>}
					{formError && <div className="config-panel__error">{formError}</div>}
					{pullError && <div className="config-panel__error">{pullError}</div>}

					{loading && <div className="config-panel__empty">Loading…</div>}

					{!loading && configMissing && !editing && (
						<div className="config-panel__empty">
							No compliance-content repository is configured.{" "}
							<button
								type="button"
								{...writeGate}
								onClick={() => {
									setEditing(true);
									setForm(EMPTY_FORM);
									setFormError(null);
								}}
							>
								Configure
							</button>
						</div>
					)}

					{!loading && config && !editing && (
						<ComplianceContentSummary
							config={config}
							connected={!disconnected}
							pendingCount={pendingCount}
							pullInFlight={pullInFlight}
							pullGateDisabled={pullGateDisabled}
							pullGateTitle={pullGateTitle}
							pullStatusText={pullStatusText}
							pullFailed={pull?.outcome === "failed"}
							onPull={doPull}
						/>
					)}

					{!loading && editing && canWrite && (
						<ComplianceContentForm
							form={form}
							setForm={setForm}
							saving={saving}
							onCancel={() => {
								setEditing(false);
								setFormError(null);
								if (config) {
									setForm(toFormState(config));
								}
							}}
							onSubmit={() => submitConfig(form)}
						/>
					)}
				</div>

				<div className="config-panel">
					<div className="config-panel__header">
						<div className="config-panel__title">PROFILE INVENTORY</div>
						<div className="config-panel__spacer" />
						{!loading && config?.pulled_commit && <div className="mono content-tab__kind">{config.pulled_commit.slice(0, 12)}</div>}
					</div>

					<table className="config-table">
						<colgroup>
							<col style={{ width: "52%" }} />
							<col style={{ width: "13%" }} />
							<col style={{ width: "14%" }} />
							<col style={{ width: "21%" }} />
						</colgroup>
						<thead>
							<tr>
								<th>PROFILE</th>
								<th>KIND</th>
								<th>VERSION</th>
								<th>STATE</th>
							</tr>
						</thead>
						<tbody>
							{loading && (
								<tr>
									<td colSpan={4} className="config-table__empty">
										Loading profiles…
									</td>
								</tr>
							)}
							{!loading && profiles.length === 0 && (
								<tr>
									<td colSpan={4} className="config-table__empty">
										No profiles installed yet — run a pull to populate the inventory.
									</td>
								</tr>
							)}
							{!loading &&
								profiles.map((p) => (
									<tr key={p.id} className="config-table__row">
										<td className="config-table__truncate mono" title={p.name}>
											{p.name}
										</td>
										<td className="content-tab__kind mono">{profileKind(p.profile_key)}</td>
										<td className="mono">{p.version ?? "—"}</td>
										<td>
											<span className={`content-tab__state content-tab__state--${p.state}`} aria-live="polite">
												{formatProfileState(p.state)}
											</span>
										</td>
									</tr>
								))}
						</tbody>
					</table>
					<div className="content-tab__footnote">
						Local edits to a profile pin it — it will not be replaced by a pull until the override is cleared.
					</div>
				</div>
			</div>

			<div className="config-panel" style={{ marginTop: 14 }}>
				<div className="config-panel__header">
					<div className="config-panel__title">PULL HISTORY</div>
				</div>
				<table className="config-table">
					<colgroup>
						<col style={{ width: "18%" }} />
						<col style={{ width: "18%" }} />
						<col style={{ width: "18%" }} />
						<col style={{ width: "16%" }} />
						<col style={{ width: "30%" }} />
					</colgroup>
					<thead>
						<tr>
							<th>WHEN</th>
							<th>WHO</th>
							<th>COMMIT</th>
							<th>STATE</th>
							<th>NOTE</th>
						</tr>
					</thead>
					<tbody>
						{loading && (
							<tr>
								<td colSpan={5} className="config-table__empty">
									Loading pull history…
								</td>
							</tr>
						)}
						{!loading && pulls.length === 0 && (
							<tr>
								<td colSpan={5} className="config-table__empty">
									No pulls have been attempted yet.
								</td>
							</tr>
						)}
						{!loading &&
							pulls.map((entry) => (
								<tr key={entry.id} className="config-table__row">
									<td className="mono">{formatTimestamp(entry.created_at)}</td>
									<td className="config-table__truncate mono" title={entry.initiated_by ?? "system"}>
										{entry.initiated_by ?? "system"}
									</td>
									<td className="mono">{entry.commit ? entry.commit.slice(0, 12) : "—"}</td>
									<td>
										<span className={entry.status === "failed" ? "content-tab__pull-status--bad" : "content-tab__pull-status"}>
											{formatPullStatus(entry.status)}
										</span>
									</td>
									<td className="config-table__truncate" title={entry.note ?? ""}>
										{entry.note ?? "—"}
									</td>
								</tr>
							))}
					</tbody>
				</table>
			</div>
		</div>
	);
}

/** Best-effort STIG-vs-SRG-vs-local grouping for display only — the backend
 * has no `kind` field on `ProfileResponse` (see this PR's body: deferred
 * finding). Falls back to "profile" when the key doesn't match either
 * recognizable convention, so an unrecognized key never renders blank. */
function profileKind(profileKey: string): string {
	const key = profileKey.toLowerCase();
	if (key.includes("stig")) return "STIG";
	if (key.includes("srg")) return "SRG";
	return "profile";
}

function ComplianceContentSummary({
	config,
	connected,
	pendingCount,
	pullInFlight,
	pullGateDisabled,
	pullGateTitle,
	pullStatusText,
	pullFailed,
	onPull,
}: {
	config: ComplianceContentConfig;
	connected: boolean;
	pendingCount: number;
	pullInFlight: boolean;
	pullGateDisabled: boolean;
	pullGateTitle: string | undefined;
	pullStatusText: string | null;
	pullFailed: boolean;
	onPull: () => void;
}) {
	const trackingLabel = config.ref_type === "branch" ? `Track branch — ${config.ref_value}` : `Pinned tag — ${config.ref_value}`;
	const pullLabel = pullInFlight ? "Pulling…" : connected ? "Pull updates" : "Pull unavailable";

	return (
		<div>
			<div className="content-tab__field">
				<span className="content-tab__field-label">Repository</span>
				<span className="content-tab__field-value">{config.repository_url}</span>
			</div>
			<div className="content-tab__field">
				<span className="content-tab__field-label">Tracking</span>
				<span className="content-tab__field-value">{trackingLabel}</span>
			</div>
			<div className="content-tab__field">
				<span className="content-tab__field-label">Commit</span>
				<span className="content-tab__field-value">{config.pulled_commit ? config.pulled_commit.slice(0, 12) : "never pulled"}</span>
			</div>
			<div className="content-tab__field">
				<span className="content-tab__field-label">Pulled</span>
				<span className="content-tab__field-value">
					{config.pulled_at ? `${formatTimestamp(config.pulled_at)}${config.pulled_by ? ` · ${config.pulled_by}` : ""}` : "never"}
				</span>
			</div>

			{pendingCount > 0 && (
				<div className="content-tab__note content-tab__note--update">
					<div className="content-tab__note-title">
						{pendingCount} profile{pendingCount === 1 ? "" : "s"} pending update
					</div>
					<div className="content-tab__note-body">
						A prior pull recorded newer content for {pendingCount === 1 ? "this profile" : "these profiles"} than what
						is currently installed. Pull updates to bring the inventory current.
					</div>
				</div>
			)}
			{pendingCount === 0 && !connected && (
				<div className="content-tab__note">
					<div className="content-tab__note-title">Updates arrive by transfer bundle</div>
					<div className="content-tab__note-body">
						This appliance is air-gapped — content updates arrive via an air-gap import bundle, not a live pull.
					</div>
				</div>
			)}

			<div className="content-tab__actions">
				<div className="content-tab__actions-spacer" />
				<button type="button" disabled={pullGateDisabled} title={pullGateTitle} onClick={onPull}>
					{pullLabel}
				</button>
			</div>
			{pullStatusText && (
				<div className={pullFailed ? "content-tab__pull-status--bad" : "content-tab__pull-status"} aria-live="polite">
					{pullStatusText}
				</div>
			)}
		</div>
	);
}

function ComplianceContentForm({
	form,
	setForm,
	saving,
	onCancel,
	onSubmit,
}: {
	form: ConfigFormState;
	setForm: (f: ConfigFormState) => void;
	saving: boolean;
	onCancel: () => void;
	onSubmit: () => void;
}) {
	const canSubmit = form.repository_url.trim().length > 0 && form.ref_value.trim().length > 0;

	return (
		<form
			className="config-form"
			onSubmit={(e) => {
				e.preventDefault();
				if (canSubmit) onSubmit();
			}}
		>
			<div className="config-form__grid">
				<label className="config-form__field">
					<span>Repository URL</span>
					<input
						value={form.repository_url}
						onChange={(e) => setForm({ ...form, repository_url: e.target.value })}
						placeholder="git.example.internal/compliance-content.git"
						required
					/>
				</label>
				<label className="config-form__field">
					<span>Tracking</span>
					<select
						value={form.ref_type}
						onChange={(e) => setForm({ ...form, ref_type: e.target.value as ComplianceContentRefType })}
					>
						<option value="tag">Pinned tag</option>
						<option value="branch">Track branch</option>
					</select>
				</label>
				<label className="config-form__field">
					<span>{form.ref_type === "branch" ? "Branch" : "Tag"}</span>
					<input
						value={form.ref_value}
						onChange={(e) => setForm({ ...form, ref_value: e.target.value })}
						placeholder={form.ref_type === "branch" ? "main" : "v2026.08.1"}
						required
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
