/**
 * Config → Depot & Tokens tab (issue #571, completing #560's frontend half +
 * #39's screen — backend landed in PR #570 `GET /downloads/readiness` +
 * credential last_tested_at/expires_at, and PR #602 the tool-install paths).
 * docs/ui/prototype README "Depot & Tokens": Broadcom Support Portal token
 * (account, masked, Replace, expiry warning, Test), the download-tool binary
 * with its three install paths, and combined readiness.
 *
 * Three panels:
 *   1. DEPOT CREDENTIAL — write-only depot-token create/replace (reuses
 *      useDepotToken.ts, which itself reuses credentials.ts's generic CRUD
 *      and the step-up/SSE-test machinery credentials already has). No
 *      secret ever renders after entry — the wire has no field to render.
 *   2. READINESS — GET /downloads/readiness, explaining exactly which
 *      prerequisite (token / tool / both) is missing, never inventing detail
 *      the backend didn't supply.
 *   3. DOWNLOAD TOOL — installed/verified state, install-from-local-repo
 *      form (Operator+), manual upload form (artifact + mandatory .sig,
 *      Operator+), a disabled depot-fetch action (backend path deferred to
 *      #39's remainder), and install history including rejected attempts.
 *
 * Role floors mirror the real backend guards, not a UI guess: credential
 * create/update/test/delete is Admin-only (CredentialsController), matching
 * every other row in CredentialsTab.tsx; tool install/upload is
 * Operator+ (ManagedToolController) and history is Viewer+.
 */
import { useCallback, useEffect, useState } from "react";
import { useAuth } from "../../lib/auth-context";
import { ApiError } from "../../lib/api";
import { roleAtLeast, roleGateProps } from "../../lib/roles";
import { useSystem } from "../../lib/system-context";
import { formatHealth, formatTimestamp as formatCredentialTimestamp } from "./credentials";
import {
	fetchDownloadReadiness,
	fetchManagedToolInstalls,
	formatManagedToolOutcome,
	formatSource,
	formatTimestamp,
	type DownloadReadiness,
	type ManagedToolInstall,
} from "./depot";
import { EMPTY_DEPOT_TOKEN_FORM, toDepotTokenFormState, useDepotToken, type DepotTokenFormState } from "./useDepotToken";
import { useManagedToolInstall } from "./useManagedToolInstall";
import "./ConfigurationScreen.css";
import "./DepotTokensTab.css";

export function DepotTokensTab() {
	const { user } = useAuth();
	const { mode } = useSystem();
	const disconnected = mode === "disconnected";

	const adminGate = user ? roleGateProps(user.role, "Admin", `Requires Admin — this action is not available to ${user.role}`) : { disabled: true };
	const operatorGate = user
		? roleGateProps(user.role, "Operator", `Requires Operator — this action is not available to ${user.role}`)
		: { disabled: true };
	const canWriteCredential = user ? roleAtLeast(user.role, "Admin") : false;

	const depotToken = useDepotToken();

	const [readiness, setReadiness] = useState<DownloadReadiness | null>(null);
	const [readinessError, setReadinessError] = useState<string | null>(null);
	const [installs, setInstalls] = useState<ManagedToolInstall[]>([]);
	const [installsLoading, setInstallsLoading] = useState(true);
	const [installsError, setInstallsError] = useState<string | null>(null);

	const loadReadinessAndHistory = useCallback(() => {
		setReadinessError(null);
		fetchDownloadReadiness()
			.then(setReadiness)
			.catch((err: unknown) => setReadinessError(err instanceof ApiError ? err.message : "Could not load download readiness."));
		setInstallsLoading(true);
		setInstallsError(null);
		fetchManagedToolInstalls()
			.then(setInstalls)
			.catch((err: unknown) => setInstallsError(err instanceof ApiError ? err.message : "Could not load install history."))
			.finally(() => setInstallsLoading(false));
	}, []);

	useEffect(() => {
		loadReadinessAndHistory();
	}, [loadReadinessAndHistory]);

	const onInstallSettled = useCallback(() => {
		loadReadinessAndHistory();
		depotToken.reload();
	}, [loadReadinessAndHistory, depotToken]);

	const { install, installError, inFlight, installFromLocalRepository, uploadTool, fetchFromDepot } = useManagedToolInstall(onInstallSettled);

	return (
		<div className="config-tab config-tab--depot">
			<div className="config-tab__grid">
				<DepotCredentialPanel
					canWrite={canWriteCredential}
					writeGate={adminGate}
					depotToken={depotToken}
				/>
				<ReadinessPanel readiness={readiness} error={readinessError} />
			</div>
			<div className="config-panel" style={{ marginTop: 14 }}>
				<ManagedToolPanel
					disconnected={disconnected}
					writeGate={operatorGate}
					readiness={readiness}
					install={install}
					installError={installError}
					inFlight={inFlight}
					onInstallFromLocalRepository={installFromLocalRepository}
					onUpload={uploadTool}
					onFetchFromDepot={fetchFromDepot}
				/>
			</div>
			<div className="config-panel" style={{ marginTop: 14 }}>
				<InstallHistoryTable installs={installs} loading={installsLoading} error={installsError} />
			</div>
		</div>
	);
}

function DepotCredentialPanel({
	canWrite,
	writeGate,
	depotToken,
}: {
	canWrite: boolean;
	writeGate: { disabled: boolean; style?: { opacity: number }; title?: string };
	depotToken: ReturnType<typeof useDepotToken>;
}) {
	const { credential, loading, loadError, editing, setEditing, form, setForm, saving, formError, submit, testing, testMessage, doTest } =
		depotToken;

	const healthClass =
		credential?.health === "valid"
			? "credentials-tab__health--valid"
			: credential?.health === "auth_failing"
				? "credentials-tab__health--bad"
				: "credentials-tab__health--unknown";

	return (
		<div className="config-panel">
			<div className="config-panel__header">
				<div className="config-panel__title">DEPOT CREDENTIAL</div>
				<div className="config-panel__spacer" />
				{!loading && !editing && (
					<button
						type="button"
						{...writeGate}
						onClick={() => {
							setEditing(true);
							setForm(credential ? toDepotTokenFormState(credential) : EMPTY_DEPOT_TOKEN_FORM);
						}}
					>
						{credential ? "Replace" : "Add depot token"}
					</button>
				)}
			</div>

			{loadError && <div className="config-panel__error">{loadError}</div>}
			{formError && <div className="config-panel__error">{formError}</div>}

			{loading && <div className="config-panel__empty">Loading…</div>}

			{!loading && !editing && !credential && (
				<div className="config-panel__empty">
					No Broadcom Support Portal depot token is configured yet. An account/token pair is required for depot-backed
					downloads and catalog authentication.
				</div>
			)}

			{!loading && !editing && credential && (
				<div className="depot-tab__field-list">
					<Field label="Account" value={credential.username || "—"} />
					<Field label="Health">
						<span className={`credentials-tab__health ${healthClass}`} aria-live="polite">
							{formatHealth(credential.health)}
						</span>
					</Field>
					<Field label="Last tested" value={formatCredentialTimestamp(credential.last_tested_at)} />
					<Field label="Last rotated" value={formatCredentialTimestamp(credential.rotated_at)} />
					<Field label="Expiry">
						<ExpiryValue expiresAt={credential.expires_at} />
					</Field>

					<div className="content-tab__actions">
						<div className="content-tab__actions-spacer" />
						<button
							type="button"
							onClick={doTest}
							disabled={writeGate.disabled || testing}
							style={!testing ? writeGate.style : undefined}
							title={!testing ? writeGate.title : undefined}
						>
							{testing ? "Testing…" : "Test"}
						</button>
					</div>
					{testMessage && (
						<div className={testMessage.succeeded ? "credentials-tab__test-ok" : "credentials-tab__test-bad"} aria-live="polite">
							{testMessage.message}
						</div>
					)}
				</div>
			)}

			{!loading && editing && canWrite && (
				<DepotTokenForm
					title={credential ? "Replace depot token" : "New depot token"}
					form={form}
					setForm={setForm}
					saving={saving}
					onCancel={() => {
						setEditing(false);
						setForm(EMPTY_DEPOT_TOKEN_FORM);
					}}
					onSubmit={submit}
				/>
			)}
		</div>
	);
}

/** Never invents a date (issue #560's Risks/Considerations, #571 AC): renders
 * "unknown" whenever `expires_at` is absent, rather than falling back to any
 * computed or placeholder value. A real expiry within 14 days gets the same
 * non-color-only warning treatment as the rest of the app (icon-equivalent
 * text prefix, not just a color change). */
function ExpiryValue({ expiresAt }: { expiresAt: string | null | undefined }) {
	if (!expiresAt) {
		return <span className="depot-tab__expiry-unknown">expiry unknown</span>;
	}
	const date = new Date(expiresAt);
	if (Number.isNaN(date.getTime())) {
		return <span className="depot-tab__expiry-unknown">expiry unknown</span>;
	}
	const daysLeft = Math.floor((date.getTime() - Date.now()) / (24 * 60 * 60 * 1000));
	const soon = daysLeft <= 14;
	return (
		<span className={soon ? "depot-tab__expiry-soon" : undefined} aria-live={soon ? "polite" : undefined}>
			{soon ? "expires soon — " : ""}
			{formatCredentialTimestamp(expiresAt)}
		</span>
	);
}

function Field({ label, value, children }: { label: string; value?: string; children?: React.ReactNode }) {
	return (
		<div className="content-tab__field">
			<span className="content-tab__field-label">{label}</span>
			<span className="content-tab__field-value">{children ?? value}</span>
		</div>
	);
}

function DepotTokenForm({
	title,
	form,
	setForm,
	saving,
	onCancel,
	onSubmit,
}: {
	title: string;
	form: DepotTokenFormState;
	setForm: (f: DepotTokenFormState) => void;
	saving: boolean;
	onCancel: () => void;
	onSubmit: () => void;
}) {
	const canSubmit = form.name.trim().length > 0 && form.secret.trim().length > 0;

	return (
		<form
			className="config-form"
			onSubmit={(e) => {
				e.preventDefault();
				if (canSubmit) onSubmit();
			}}
		>
			<div className="config-form__title">{title}</div>
			<div className="config-form__grid">
				<label className="config-form__field">
					<span>Name</span>
					<input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required />
				</label>
				<label className="config-form__field">
					<span>Account</span>
					<input
						value={form.username}
						onChange={(e) => setForm({ ...form, username: e.target.value })}
						placeholder="e.g. svc-depot@example.internal"
						autoComplete="off"
					/>
				</label>
				<label className="config-form__field">
					<span>Token</span>
					<input
						type="password"
						value={form.secret}
						onChange={(e) => setForm({ ...form, secret: e.target.value })}
						placeholder="required — never displayed again after saving"
						autoComplete="new-password"
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

/** Explains exactly which prerequisite is missing (#560 AC) — never a bare
 * "not ready" with no reason, and never color-only (each state has a
 * distinct text label, not just a color swap). */
function ReadinessPanel({ readiness, error }: { readiness: DownloadReadiness | null; error: string | null }) {
	return (
		<div className="config-panel">
			<div className="config-panel__header">
				<div className="config-panel__title">DOWNLOAD READINESS</div>
			</div>
			{error && <div className="config-panel__error">{error}</div>}
			{!error && !readiness && <div className="config-panel__empty">Loading…</div>}
			{!error && readiness && (
				<div className="depot-tab__field-list">
					<div className={`depot-tab__readiness depot-tab__readiness--${readiness.ready ? "ok" : "bad"}`} aria-live="polite">
						{readiness.ready ? "Ready — depot downloads can run." : "Not ready — see missing prerequisites below."}
					</div>
					<Field label="Depot token">
						<ReadinessDetail readiness={readiness} kind="token" />
					</Field>
					<Field label="Download tool">
						<ReadinessDetail readiness={readiness} kind="tool" />
					</Field>
				</div>
			)}
		</div>
	);
}

function ReadinessDetail({ readiness, kind }: { readiness: DownloadReadiness; kind: "token" | "tool" }) {
	if (kind === "token") {
		if (readiness.missing_prerequisites.includes("depot_token")) {
			return <span className="depot-tab__readiness-missing">not configured</span>;
		}
		if (readiness.missing_prerequisites.includes("depot_token_auth_failing")) {
			return <span className="depot-tab__readiness-missing">configured, but authentication is failing</span>;
		}
		return <span className="depot-tab__readiness-ok">{formatHealth(readiness.depot_token_health ?? "unknown")}</span>;
	}
	if (readiness.tool_installed === undefined) {
		return <span className="depot-tab__expiry-unknown">unknown — no runner has reported yet</span>;
	}
	if (readiness.tool_installed) {
		return <span className="depot-tab__readiness-ok">installed and verified</span>;
	}
	return <span className="depot-tab__readiness-missing">not installed</span>;
}

function ManagedToolPanel({
	disconnected,
	writeGate,
	readiness,
	install,
	installError,
	inFlight,
	onInstallFromLocalRepository,
	onUpload,
	onFetchFromDepot,
}: {
	disconnected: boolean;
	writeGate: { disabled: boolean; style?: { opacity: number }; title?: string };
	readiness: DownloadReadiness | null;
	install: { runId: string; outcome: "failed" | "succeeded" | null } | null;
	installError: string | null;
	inFlight: boolean;
	onInstallFromLocalRepository: (sourcePath: string, version?: string) => Promise<void>;
	onUpload: (artifact: File, checksums: { sha256?: string; md5?: string }, version?: string) => Promise<void>;
	onFetchFromDepot: (version?: string) => Promise<void>;
}) {
	const [sourcePath, setSourcePath] = useState("");
	const [localVersion, setLocalVersion] = useState("");
	const [artifactFile, setArtifactFile] = useState<File | null>(null);
	const [uploadSha256, setUploadSha256] = useState("");
	const [uploadMd5, setUploadMd5] = useState("");
	const [uploadVersion, setUploadVersion] = useState("");
	const [depotVersion, setDepotVersion] = useState("");

	// The depot-fetch action needs BOTH connected mode and a healthy depot
	// credential (issue #39 remainder) -- readiness already reports both, and
	// this mirrors exactly what the backend itself enforces (409
	// mode_unavailable when disconnected; the job fails cleanly with an
	// actionable reason if the credential is missing/auth-failing), so the UI
	// gate is never stricter or looser than the server's own rule.
	const depotCredentialHealthy = readiness?.depot_token_health === "valid";
	const depotFetchDisabled = writeGate.disabled || inFlight || disconnected || !depotCredentialHealthy;
	const depotFetchTitle = writeGate.disabled
		? writeGate.title
		: inFlight
			? "An install is already queued or running"
			: disconnected
				? "Requires connected mode"
				: !depotCredentialHealthy
					? "Requires a configured, healthy depot credential (see DEPOT CREDENTIAL above)"
					: undefined;

	const toolInstalled = readiness?.tool_installed;
	const stateLabel = toolInstalled === undefined ? "unknown" : toolInstalled ? "installed" : "not installed";
	const stateClass =
		toolInstalled === undefined ? "depot-tab__tool-state--amber" : toolInstalled ? "depot-tab__tool-state--ok" : "depot-tab__tool-state--amber";

	const installGateDisabled = writeGate.disabled || inFlight;
	const installGateTitle = writeGate.disabled ? writeGate.title : inFlight ? "An install is already queued or running" : undefined;
	const normalizedSha256 = uploadSha256.trim();
	const normalizedMd5 = uploadMd5.trim();
	const sha256Valid = !normalizedSha256 || /^[0-9a-fA-F]{64}$/.test(normalizedSha256);
	const md5Valid = !normalizedMd5 || /^[0-9a-fA-F]{32}$/.test(normalizedMd5);
	const uploadChecksumsValid = (Boolean(normalizedSha256) || Boolean(normalizedMd5)) && sha256Valid && md5Valid;

	const statusText = inFlight
		? "Install queued or running…"
		: install?.outcome === "failed"
			? "Last install attempt failed or was rejected — see history below."
			: install?.outcome === "succeeded"
				? "Install succeeded."
				: null;

	return (
		<div>
			<div className="config-panel__header" style={{ padding: 0, border: 0, marginBottom: 10 }}>
				<div className="config-panel__title">DOWNLOAD TOOL</div>
				<div className="config-panel__spacer" />
				<span className={`depot-tab__tool-state ${stateClass}`} aria-live="polite">
					{stateLabel}
				</span>
			</div>

			{!toolInstalled && (
				<div className="content-tab__note">
					<div className="content-tab__note-title">Catalog browsing still works</div>
					<div className="content-tab__note-body">
						Licensing prevents shipping the download tool in the appliance image. Until it is installed, the catalog
						remains browsable as an indexed depot — only fetching artifacts is unavailable.
					</div>
				</div>
			)}

			{installError && <div className="config-panel__error">{installError}</div>}
			{statusText && (
				<div className={install?.outcome === "failed" ? "content-tab__pull-status--bad" : "content-tab__pull-status"} aria-live="polite">
					{statusText}
				</div>
			)}

			<div className="depot-tab__tool-forms">
				<form
					className="config-form depot-tab__tool-form"
					onSubmit={(e) => {
						e.preventDefault();
						if (sourcePath.trim()) {
							void onInstallFromLocalRepository(sourcePath.trim(), localVersion.trim() || undefined);
						}
					}}
				>
					<div className="config-form__title">Install from local repository</div>
					<div className="config-form__grid config-form__grid--single">
						<label className="config-form__field">
							<span>Source path (within the local repository)</span>
							<input
								value={sourcePath}
								onChange={(e) => setSourcePath(e.target.value)}
								placeholder="vcf-download-tool/vcf-download-tool-1.4.2.tar.gz"
								disabled={writeGate.disabled}
							/>
						</label>
						<label className="config-form__field">
							<span>Version (optional)</span>
							<input value={localVersion} onChange={(e) => setLocalVersion(e.target.value)} disabled={writeGate.disabled} />
						</label>
					</div>
					<div className="config-form__actions">
						<button
							type="submit"
							className="config-form__submit"
							disabled={installGateDisabled || !sourcePath.trim()}
							title={installGateTitle}
						>
							Install
						</button>
					</div>
				</form>

				<form
					className="config-form depot-tab__tool-form"
					onSubmit={(e) => {
						e.preventDefault();
						if (artifactFile && uploadChecksumsValid) {
							void onUpload(
								artifactFile,
								{ sha256: normalizedSha256 || undefined, md5: normalizedMd5 || undefined },
								uploadVersion.trim() || undefined,
							);
						}
					}}
				>
					<div className="config-form__title">Manual upload</div>
					<div className="config-form__grid config-form__grid--single">
						<label className="config-form__field">
							<span>Artifact file</span>
							<input
								type="file"
								onChange={(e) => setArtifactFile(e.target.files?.[0] ?? null)}
								disabled={writeGate.disabled}
							/>
						</label>
						<label className="config-form__field">
							<span>SHA-256 (preferred)</span>
							<input
								value={uploadSha256}
								onChange={(e) => setUploadSha256(e.target.value)}
								placeholder="64 hexadecimal characters"
								disabled={writeGate.disabled}
							/>
						</label>
						<label className="config-form__field">
							<span>MD5 (legacy integrity only)</span>
							<input
								value={uploadMd5}
								onChange={(e) => setUploadMd5(e.target.value)}
								placeholder="32 hexadecimal characters"
								disabled={writeGate.disabled}
							/>
						</label>
						<div className="depot-tab__depot-fetch-note">
							Copy SHA2 or MD5 from the authenticated Broadcom support download record. Checksums verify file integrity; they do not authenticate the publisher.
						</div>
						<label className="config-form__field">
							<span>Version (optional)</span>
							<input value={uploadVersion} onChange={(e) => setUploadVersion(e.target.value)} disabled={writeGate.disabled} />
						</label>
					</div>
					<div className="config-form__actions">
						<button
							type="submit"
							className="config-form__submit"
							disabled={installGateDisabled || !artifactFile || !uploadChecksumsValid}
							title={installGateTitle}
						>
							Upload &amp; install
						</button>
					</div>
				</form>

				<form
					className="config-form depot-tab__tool-form depot-tab__depot-fetch"
					onSubmit={(e) => {
						e.preventDefault();
						void onFetchFromDepot(depotVersion.trim() || undefined);
					}}
				>
					<div className="config-form__title">Fetch from depot</div>
					<div className="depot-tab__depot-fetch-note">Requires connected mode and a valid depot credential.</div>
					<div className="config-form__grid config-form__grid--single">
						<label className="config-form__field">
							<span>Version (optional)</span>
							<input value={depotVersion} onChange={(e) => setDepotVersion(e.target.value)} disabled={depotFetchDisabled} />
						</label>
					</div>
					<div className="config-form__actions">
						<button type="submit" className="config-form__submit" disabled={depotFetchDisabled} title={depotFetchTitle}>
							Fetch from depot
						</button>
					</div>
				</form>
			</div>
		</div>
	);
}

function InstallHistoryTable({ installs, loading, error }: { installs: ManagedToolInstall[]; loading: boolean; error: string | null }) {
	return (
		<div>
			<div className="config-panel__header">
				<div className="config-panel__title">INSTALL HISTORY</div>
			</div>
			{error && <div className="config-panel__error">{error}</div>}
			<table className="config-table">
				<colgroup>
					<col style={{ width: "16%" }} />
					<col style={{ width: "16%" }} />
					<col style={{ width: "26%" }} />
					<col style={{ width: "12%" }} />
					<col style={{ width: "14%" }} />
					<col style={{ width: "16%" }} />
				</colgroup>
				<thead>
					<tr>
						<th>WHEN</th>
						<th>SOURCE</th>
						<th>SOURCE PATH</th>
						<th>VERSION</th>
						<th>OUTCOME</th>
						<th>INITIATED BY</th>
					</tr>
				</thead>
				<tbody>
					{loading && (
						<tr>
							<td colSpan={6} className="config-table__empty">
								Loading install history…
							</td>
						</tr>
					)}
					{!loading && installs.length === 0 && (
						<tr>
							<td colSpan={6} className="config-table__empty">
								No install attempts yet.
							</td>
						</tr>
					)}
					{!loading &&
						installs.map((entry) => (
							<tr key={entry.id} className="config-table__row">
								<td className="mono">{formatTimestamp(entry.created_at)}</td>
								<td>{formatSource(entry.source)}</td>
								<td className="config-table__truncate mono" title={entry.source_path}>
									{entry.source_path}
								</td>
								<td className="mono">{entry.version ?? "—"}</td>
								<td>
									<span
										className={
											entry.outcome === "installed"
												? "depot-tab__readiness-ok"
												: "depot-tab__readiness-missing"
										}
										title={entry.rejected_reason}
									>
										{formatManagedToolOutcome(entry.outcome)}
										{entry.rejected_reason ? ` — ${entry.rejected_reason}` : ""}
									</span>
								</td>
								<td className="config-table__truncate mono" title={entry.initiated_by}>
									{entry.initiated_by}
								</td>
							</tr>
						))}
				</tbody>
			</table>
		</div>
	);
}
