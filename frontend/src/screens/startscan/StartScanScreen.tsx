/**
 * Start a Scan — five-step wizard (docs/ui/prototype/README.md screen 3;
 * issue #284, second sub-issue of the #26 split; PR #288's Live Run view is
 * the destination after confirm, PR #285's run controls are out of scope
 * here).
 *
 * Steps: site -> scope (inventory checkbox tree, target-level fallback) ->
 * credential (service picker, Cyber+, or ADR-0011 enter-now personal,
 * Operator+) -> run/schedule stub (disabled; scheduling is M3) -> confirm
 * (summary + POST /runs -> navigate to Live Run with the new run id).
 *
 * Role gate: the whole flow is Cyber+ (README "Roles & Permissions" —
 * "Cyber + initiate scans using assigned service credentials"). A Viewer
 * sees the entry rendered visible-but-disabled by the router's screen-level
 * guard (redirects to Dashboard) plus this screen's own gate for anyone who
 * lands here directly with an insufficient role (e.g. a stale tab after a
 * role downgrade).
 *
 * This screen is a thin orchestrator (issue #419): all wizard state lives in
 * useScanWizard.ts and every step panel is one of the presentation
 * components in StartScanSteps.tsx. This file only wires the two together
 * and renders the stepper/nav chrome.
 */
import { useAuth } from "../../lib/auth-context";
import { useRouter } from "../../lib/router-context";
import { CredentialStep, ConfirmStep, ScheduleStep, ScopeStep, SiteStep } from "./StartScanSteps";
import { STEPS, useScanWizard, type StepKey } from "./useScanWizard";
import "./StartScanScreen.css";

export function StartScanScreen() {
	const { user } = useAuth();
	const { navigate } = useRouter();
	const wizard = useScanWizard({ userRole: user?.role, navigate });

	if (!user) {
		return null;
	}

	const { step, setStep, stepIndex, canAdvance, allowed, gate } = wizard;

	return (
		<div className="start-scan-screen">
			<div className="start-scan-screen__stepper">
				{STEPS.map((s, index) => (
					<button
						key={s.key}
						type="button"
						className={`start-scan-screen__step${step === s.key ? " is-active" : ""}`}
						disabled={!allowed || (index > stepIndex && !canAdvance(s.key))}
						onClick={() => setStep(s.key)}
					>
						<span className="start-scan-screen__step-index">{index + 1}</span>
						<span>{s.label}</span>
					</button>
				))}
			</div>

			{!allowed && (
				<div className="start-scan-screen__gate">
					<p {...gate}>Starting a scan requires Cyber or higher.</p>
				</div>
			)}

			{allowed && (
				<div className="start-scan-screen__body">
					{step === "site" && (
						<SiteStep sites={wizard.sites} loading={wizard.sitesLoading} error={wizard.sitesError} siteId={wizard.siteId} onSelect={wizard.selectSite} />
					)}

					{step === "scope" && (
						<ScopeStep
							selections={wizard.selections}
							loading={wizard.scopeLoading}
							error={wizard.scopeError}
							onToggleTarget={wizard.toggleTarget}
							onToggleItem={wizard.toggleInventoryItem}
							profiles={wizard.profiles}
							profilesLoading={wizard.profilesLoading}
							profilesError={wizard.profilesError}
							profileId={wizard.profileId}
							onProfileChange={wizard.setProfileId}
						/>
					)}

					{step === "credential" && (
						<CredentialStep
							mode={wizard.credentialMode}
							onModeChange={wizard.setCredentialMode}
							canUsePersonal={wizard.canUsePersonal}
							personalGate={wizard.personalGate}
							credentialOptions={wizard.credentialOptions}
							credentialOptionsError={wizard.credentialOptionsError}
							serviceCredentialId={wizard.serviceCredentialId}
							onServiceCredentialChange={wizard.setServiceCredentialId}
							personalUsername={wizard.personalUsername}
							onPersonalUsernameChange={wizard.setPersonalUsername}
							personalSecret={wizard.personalSecret}
							onPersonalSecretChange={wizard.setPersonalSecret}
						/>
					)}

					{step === "schedule" && <ScheduleStep />}

					{step === "confirm" && (
						<ConfirmStep
							siteName={wizard.siteName}
							targetCount={wizard.selectedTargetIds.length || wizard.selections.length}
							totalTargets={wizard.selections.length}
							profileName={wizard.selectedProfileName}
							credentialMode={wizard.credentialMode}
							credentialName={wizard.selectedCredentialName}
							canConfirm={wizard.canConfirm}
							submitting={wizard.submitting}
							error={wizard.submitError}
							onConfirm={wizard.submit}
						/>
					)}

					<div className="start-scan-screen__nav">
						<button type="button" disabled={stepIndex === 0} onClick={() => setStep(STEPS[stepIndex - 1].key)}>
							Back
						</button>
						{step !== "confirm" && (
							<button
								type="button"
								disabled={!canAdvance(STEPS[stepIndex + 1]?.key as StepKey)}
								onClick={() => setStep(STEPS[stepIndex + 1].key)}
							>
								Next
							</button>
						)}
					</div>
				</div>
			)}
		</div>
	);
}
