/**
 * Config → Depot & Tokens tab (issue #571, completing #560's frontend half +
 * #39's screen — backend landed in PR #570 `GET /downloads/readiness` +
 * credential last_tested_at/expires_at, and PR #602 the tool-install paths;
 * issue #690 splits the single depot-token concept into two independent,
 * non-interchangeable credentials). docs/ui/prototype README "Depot &
 * Tokens", updated per #690's design: the VCF 9.1 Software Depot Activation
 * Code (authenticates `vcf-download-tool` commands) and the legacy Broadcom
 * Download Token (UMDS/older `dl.broadcom.com` URL-template flows only) are
 * presented as two clearly distinct credentials — never one shared "depot
 * token" concept — each with its own account/masked/Replace/expiry/Test UI.
 *
 * Four panels:
 *   1. ACTIVATION CODE — write-only create/replace for the VCF 9.1 credential
 *      that actually authenticates the download tool (reuses
 *      useDepotToken.ts's shared factory). No secret ever renders after
 *      entry — the wire has no field to render.
 *   2. LEGACY DOWNLOAD TOKEN — the deprecated UMDS/older-flow credential,
 *      labeled as legacy guidance, never presented as interchangeable with
 *      the Activation Code.
 *   3. READINESS — GET /downloads/readiness, explaining exactly which
 *      prerequisite (activation code / tool / both) is missing, never
 *      inventing detail the backend didn't supply.
 *   4. DOWNLOAD TOOL — installed/verified state, install-from-local-repo
 *      form (Operator+), manual upload form (artifact + mandatory .sig,
 *      Operator+), a depot-fetch action gated on the Activation Code (not
 *      the legacy token), and install history including rejected attempts.
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
import {
	EMPTY_DEPOT_TOKEN_FORM,
	toDepotTokenFormState,
	useDepotActivationCode,
	useLegacyDownloadToken,
	type DepotTokenFormState,
	type UseDepotTokenResult,
} from "./useDepotToken";
import { useDepotEnrollment, type UseDepotEnrollmentResult } from "./useDepotEnrollment";
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

	const activationCode = useDepotActivationCode();
	const legacyToken = useLegacyDownloadToken();
	const enrollment = useDepotEnrollment();

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
		activationCode.reload();
		legacyToken.reload();
		enrollment.reload();
	}, [loadReadinessAndHistory, activationCode, legacyToken, enrollment]);

	const { install, installError, inFlight, installFromLocalRepository, uploadTool, fetchFromDepot } = useManagedToolInstall(onInstallSettled);

	return (
		<div className="config-tab config-tab--depot">
			<div className="config-panel" style={{ marginBottom: 14 }}>
				<EnrollmentPanel writeGate={adminGate} enrollment={enrollment} />
			</div>
			<div className="config-tab__grid">
				<DepotCredentialPanel
					title="ACTIVATION CODE"
					addLabel="Add Activation Code"
					emptyCopy="No VCF 9.1 Software Depot Activation Code is configured yet. The Activation Code (paired to a Software Depot ID) authenticates every vcf-download-tool metadata/binary command."
					secretLabel="Activation Code"
					canWrite={canWriteCredential}
					writeGate={adminGate}
					depotToken={activationCode}
				/>
				<DepotCredentialPanel
					title="LEGACY DOWNLOAD TOKEN"
					addLabel="Add legacy token"
					emptyCopy="No legacy Broadcom Download Token is configured. Deprecated: replaced by the Activation Code for VCF 9.1 (dl.broadcom.com replaced Download Tokens with Activation Codes). Configure this only for UMDS or other pre-9.1 URL-template flows — it cannot authenticate vcf-download-tool commands."
					secretLabel="Download Token"
					deprecated
					canWrite={canWriteCredential}
					writeGate={adminGate}
					depotToken={legacyToken}
				/>
			</div>
			<div className="config-tab__grid" style={{ marginTop: 14 }}>
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
	title,
	addLabel,
	emptyCopy,
	secretLabel,
	deprecated = false,
	canWrite,
	writeGate,
	depotToken,
}: {
	title: string;
	addLabel: string;
	emptyCopy: string;
	secretLabel: string;
	deprecated?: boolean;
	canWrite: boolean;
	writeGate: { disabled: boolean; style?: { opacity: number }; title?: string };
	depotToken: UseDepotTokenResult;
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
				<div className="config-panel__title">
					{title}
					{deprecated && <span className="depot-tab__deprecated-badge"> — DEPRECATED</span>}
				</div>
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
						{credential ? "Replace" : addLabel}
					</button>
				)}
			</div>

			{loadError && <div className="config-panel__error">{loadError}</div>}
			{formError && <div className="config-panel__error">{formError}</div>}

			{loading && <div className="config-panel__empty">Loading…</div>}

			{!loading && !editing && !credential && <div className="config-panel__empty">{emptyCopy}</div>}

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
					title={credential ? `Replace ${secretLabel}` : `New ${secretLabel}`}
					secretLabel={secretLabel}
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

/**
 * The issue #691 assisted enrollment panel: state machine display, the two
 * entry paths ("use existing code" vs "I need a code"), and an explicit
 * confirmed identity reset. The Activation Code value is never rendered here
 * or anywhere else in this tab — only the non-secret Depot ID and enrollment
 * state. The registration link is always the server-supplied
 * `registration_url` (issue #691: the VCFDT 9.1 `.net` typo corrected to
 * `.com`), never a hardcoded/guessed URL.
 */
function EnrollmentPanel({
	writeGate,
	enrollment,
}: {
	writeGate: { disabled: boolean; style?: { opacity: number }; title?: string };
	enrollment: UseDepotEnrollmentResult;
}) {
	const { enrollment: state, loading, loadError, busy, actionError, doGenerateDepotId, doAcceptActivationCode, doValidate, doReset } = enrollment;
	const [codeInput, setCodeInput] = useState("");
	const [resetConfirming, setResetConfirming] = useState(false);

	if (loading) {
		return (
			<div>
				<div className="config-panel__header">
					<div className="config-panel__title">DEPOT ENROLLMENT</div>
				</div>
				<div className="config-panel__empty">Loading…</div>
			</div>
		);
	}

	if (loadError || !state) {
		return (
			<div>
				<div className="config-panel__header">
					<div className="config-panel__title">DEPOT ENROLLMENT</div>
				</div>
				<div className="config-panel__error">{loadError ?? "Could not load depot enrollment status."}</div>
			</div>
		);
	}

	const stateLabel: Record<string, string> = {
		tool_unavailable: "Download tool not installed",
		depot_id_unavailable: "Software Depot ID not yet generated",
		awaiting_portal_registration: "Awaiting Broadcom portal registration",
		activation_code_stored: "Activation Code stored — not yet validated",
		validated: "Validated — ready",
		auth_failing: "Activation Code rejected",
	};

	const submitCode = async () => {
		const trimmed = codeInput.trim();
		if (!trimmed) {
			return;
		}
		const ok = await doAcceptActivationCode(trimmed);
		if (ok) {
			setCodeInput("");
		}
	};

	return (
		<div>
			<div className="config-panel__header" style={{ padding: 0, border: 0, marginBottom: 10 }}>
				<div className="config-panel__title">DEPOT ENROLLMENT</div>
				<div className="config-panel__spacer" />
				<span
					className={`depot-tab__tool-state ${state.state === "validated" ? "depot-tab__tool-state--ok" : "depot-tab__tool-state--amber"}`}
					aria-live="polite"
				>
					{stateLabel[state.state] ?? state.state}
				</span>
			</div>

			{actionError && <div className="config-panel__error">{actionError}</div>}
			{state.state === "auth_failing" && state.last_validation_failure && (
				<div className="config-panel__error">{state.last_validation_failure}</div>
			)}

			<div className="depot-tab__field-list">
				<Field label="Software Depot ID">
					{state.depot_id ? (
						<span className="mono">
							{state.depot_id}{" "}
							<button
								type="button"
								onClick={() => void navigator.clipboard?.writeText(state.depot_id ?? "")}
								style={{ marginLeft: 8 }}
							>
								Copy
							</button>
						</span>
					) : (
						<span className="depot-tab__expiry-unknown">not yet generated</span>
					)}
				</Field>

				{!state.depot_id && (
					<div className="content-tab__actions">
						<div className="content-tab__actions-spacer" />
						<button type="button" {...writeGate} onClick={() => void doGenerateDepotId()} disabled={writeGate.disabled || busy}>
							{busy ? "Generating…" : "Generate Software Depot ID"}
						</button>
					</div>
				)}

				{state.depot_id && !state.activation_code_configured && (
					<div className="depot-tab__depot-fetch-note">
						Register this exact Software Depot ID at{" "}
						<a href={state.registration_url} target="_blank" rel="noreferrer">
							{state.registration_url}
						</a>{" "}
						(Software Depot Registrations → New Registration) to receive a paired Activation Code, or paste an
						existing compatible code below. Waypoint never issues this code itself.
					</div>
				)}

				{state.depot_id && (
					<div className="config-form" style={{ padding: "10px 0 0" }}>
						<div className="config-form__grid config-form__grid--single">
							<label className="config-form__field">
								<span>Activation Code</span>
								<input
									type="password"
									value={codeInput}
									onChange={(e) => setCodeInput(e.target.value)}
									placeholder="paste an existing or portal-issued Activation Code"
									autoComplete="off"
									disabled={writeGate.disabled || busy}
								/>
							</label>
						</div>
						<div className="config-form__actions">
							<button
								type="button"
								className="config-form__submit"
								onClick={() => void submitCode()}
								disabled={writeGate.disabled || busy || !codeInput.trim()}
								title={writeGate.title}
							>
								{busy ? "Saving…" : "Store Activation Code"}
							</button>
						</div>
					</div>
				)}

				{state.activation_code_configured && (
					<div className="content-tab__actions">
						<div className="content-tab__actions-spacer" />
						<button type="button" {...writeGate} onClick={() => void doValidate()} disabled={writeGate.disabled || busy}>
							{busy ? "Validating…" : "Validate stored Activation Code"}
						</button>
					</div>
				)}

				{state.depot_id && (
					<div className="content-tab__actions" style={{ marginTop: 10 }}>
						<div className="content-tab__actions-spacer" />
						{!resetConfirming ? (
							<button type="button" {...writeGate} onClick={() => setResetConfirming(true)} disabled={writeGate.disabled || busy}>
								Reset identity…
							</button>
						) : (
							<>
								<span className="depot-tab__expiry-soon" style={{ marginRight: 8 }}>
									This invalidates the current Depot ID/Activation Code pairing. Confirm?
								</span>
								<button type="button" onClick={() => setResetConfirming(false)} disabled={busy}>
									Cancel
								</button>
								<button
									type="button"
									{...writeGate}
									onClick={() => {
										setResetConfirming(false);
										void doReset();
									}}
									disabled={writeGate.disabled || busy}
									style={{ marginLeft: 8 }}
								>
									Confirm reset
								</button>
							</>
						)}
					</div>
				)}
			</div>
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
	secretLabel,
	form,
	setForm,
	saving,
	onCancel,
	onSubmit,
}: {
	title: string;
	secretLabel: string;
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
					<span>{secretLabel}</span>
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
					<Field label="Activation Code">
						<ReadinessDetail readiness={readiness} kind="activation_code" />
					</Field>
					<Field label="Legacy Download Token">
						<ReadinessDetail readiness={readiness} kind="legacy_token" />
					</Field>
					<Field label="Download tool">
						<ReadinessDetail readiness={readiness} kind="tool" />
					</Field>
				</div>
			)}
		</div>
	);
}

function ReadinessDetail({ readiness, kind }: { readiness: DownloadReadiness; kind: "activation_code" | "legacy_token" | "tool" }) {
	if (kind === "activation_code") {
		if (readiness.missing_prerequisites.includes("activation_code")) {
			return <span className="depot-tab__readiness-missing">not configured</span>;
		}
		if (readiness.missing_prerequisites.includes("activation_code_auth_failing")) {
			return <span className="depot-tab__readiness-missing">configured, but authentication is failing</span>;
		}
		return <span className="depot-tab__readiness-ok">{formatHealth(readiness.activation_code_health ?? "unknown")}</span>;
	}
	if (kind === "legacy_token") {
		// Never gates readiness (issue #690) -- reported for visibility only, so this
		// never renders "missing"/"blocking" language, just the raw configured state.
		if (!readiness.legacy_download_token_configured) {
			return <span className="depot-tab__expiry-unknown">not configured (optional, legacy UMDS flows only)</span>;
		}
		return <span className="depot-tab__readiness-ok">{formatHealth(readiness.legacy_download_token_health ?? "unknown")}</span>;
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

	// The depot-fetch action needs BOTH connected mode and a healthy Activation
	// Code credential (issue #39 remainder, #690: never the legacy Download
	// Token -- it cannot authenticate vcf-download-tool commands) -- readiness
	// already reports both, and this mirrors exactly what the backend itself
	// enforces (409 mode_unavailable when disconnected; the job fails cleanly
	// with an actionable reason if the credential is missing/auth-failing), so
	// the UI gate is never stricter or looser than the server's own rule.
	const activationCodeHealthy = readiness?.activation_code_health === "valid";
	const depotFetchDisabled = writeGate.disabled || inFlight || disconnected || !activationCodeHealthy;
	const depotFetchTitle = writeGate.disabled
		? writeGate.title
		: inFlight
			? "An install is already queued or running"
			: disconnected
				? "Requires connected mode"
				: !activationCodeHealthy
					? "Requires a configured, healthy Activation Code credential (see ACTIVATION CODE above)"
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
					<div className="depot-tab__depot-fetch-note">Requires connected mode and a valid Activation Code credential.</div>
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
