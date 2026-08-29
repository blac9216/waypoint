// Copyright 2026 Justin Black
//
// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Text.Json;
using Microsoft.Extensions.Options;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.ComplianceContent.Xccdf;
using Waypoint.Core.ConfigDocs;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.PowerShell;
using Waypoint.Core.Scans;
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;
using Waypoint.Core.StigManager;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.PowerShell;
using Waypoint.Infrastructure.Runs;
using Waypoint.Infrastructure.Sites;

namespace Waypoint.Infrastructure.Scans;

/// <summary>
/// The <c>scan</c> job type's handler (issues #274/#275, second and third slices of
/// the #23 split; replaces the #273 not-implemented stub). <c>scan</c> is
/// <see cref="JobShape.Standard"/> (<c>queued -&gt; running -&gt; attesting -&gt;
/// converting -&gt; uploaded</c>, ADR-0012): this handler resolves target + credential
/// and runs InSpec (<c>Stage == null</c>), applies resolved attestations to the HDF
/// (<c>Stage == "attesting"</c>), then converts to CKL and stamps benchmark metadata
/// (<c>Stage == "converting"</c>) -- reporting <see cref="JobOutcomeKind.StageComplete"/>
/// after each non-terminal stage and <see cref="JobOutcomeKind.Succeeded"/> (which the
/// dispatcher forces to <c>uploaded</c>, this shape's terminal state) after convert.
/// <c>uploaded</c> here means "artifacts ready"; #311/PR #318 added the actual STIG
/// Manager upload as a post-convert action inside the convert stage (not a new pipeline
/// stage), gated behind <see cref="ScanUploadCoordinator"/>.
///
/// An <c>ssh</c>-kind target (SRG products: Photon/Aria/vIDM) is <see cref="JobShape.Srg"/>
/// instead (<c>queued -&gt; running -&gt; attesting -&gt; done</c>, issue #309): the InSpec
/// stage dispatches to <c>Invoke-WaypointSrgScan</c> (sudo-aware) and the attest stage
/// reports <see cref="JobOutcomeKind.Succeeded"/> directly rather than advancing to
/// <c>converting</c> -- so the convert stage (and its STIG Manager upload) is
/// unreachable for SRG by construction, matching the HDF-only, no-CKL,
/// no-STIG-Manager-upload predecessor behavior.
///
/// Credential resolution mirrors <see cref="Waypoint.Infrastructure.Discovery.DiscoverJobHandler"/>:
/// a stored credential (<c>jobs.credential_id</c>) is decrypted under job/run
/// attribution (security.md control 4), and a stored vCenter credential with no
/// dedicated <see cref="CredentialResponse.Username"/> fails cleanly rather than
/// falling back to the display name (#262). <c>jobs.credential_id</c> NULL means the ad
/// hoc "my credentials" flow (ADR-0011, #276/#434): the secret comes from
/// <see cref="IRunSecretStore.DecryptAsync"/>, keyed by the job's own run id and
/// audited on every decrypt (never single-shot the way the predecessor in-memory cache
/// was -- a retried or lease-recovered job simply decrypts again, which is exactly what
/// makes this durable across an API restart between run creation and job claim) --
/// never falling back to a stored credential. A job whose run has no row (never
/// registered, or already deleted by a prior terminal run completion / expiry sweep)
/// fails auth-style, exactly like a rejected credential.
/// </summary>
public sealed class ScanJobHandler : IJobHandler
{
	internal const string AttestingStage = "attesting";
	internal const string ConvertingStage = "converting";
	private const string InvocationCommand = "Invoke-WaypointScan";
	private const string NsxInvocationCommand = "Invoke-WaypointNsxScan";
	private const string SrgInvocationCommand = "Invoke-WaypointSrgScan";
	private const string AttestCommand = "Invoke-WaypointAttest";
	private const string ConvertCommand = "Invoke-WaypointConvert";
	private const int LogTailLines = 20;

	private readonly IPowerShellExecutor _executor;
	private readonly ICredentialSecretStore _secrets;
	private readonly Waypoint.Infrastructure.Secrets.CredentialRepository _credentials;
	private readonly TargetRepository _targets;
	private readonly IRunSecretStore _runSecrets;
	private readonly IJobRunnerRepository _jobs;
	private readonly ISecretRedactor _redactor;
	private readonly IOptions<PowerShellOptions> _powerShellOptions;
	private readonly IOptions<ScanOptions> _scanOptions;
	private readonly IOptions<ComplianceContentOptions> _complianceContentOptions;
	private readonly ConfigDocRepository _configDocs;
	private readonly AttestationSnapshotRepository _attestationSnapshots;
	private readonly ScanUploadCoordinator _upload;
	private readonly ComponentProfileRevisionResolver _componentProfileRevisions;
	private readonly IBenchmarkRepository _benchmarks;
	private readonly ComponentResultRecordingService _resultRecording;

	public ScanJobHandler(
		IPowerShellExecutor executor,
		ICredentialSecretStore secrets,
		Waypoint.Infrastructure.Secrets.CredentialRepository credentials,
		TargetRepository targets,
		IRunSecretStore runSecrets,
		IJobRunnerRepository jobs,
		ISecretRedactor redactor,
		IOptions<PowerShellOptions> powerShellOptions,
		IOptions<ScanOptions> scanOptions,
		IOptions<ComplianceContentOptions> complianceContentOptions,
		ConfigDocRepository configDocs,
		AttestationSnapshotRepository attestationSnapshots,
		ScanUploadCoordinator upload,
		ComponentProfileRevisionResolver componentProfileRevisions,
		IBenchmarkRepository benchmarks,
		ComponentResultRecordingService resultRecording)
	{
		ArgumentNullException.ThrowIfNull(executor);
		ArgumentNullException.ThrowIfNull(secrets);
		ArgumentNullException.ThrowIfNull(credentials);
		ArgumentNullException.ThrowIfNull(targets);
		ArgumentNullException.ThrowIfNull(runSecrets);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(redactor);
		ArgumentNullException.ThrowIfNull(powerShellOptions);
		ArgumentNullException.ThrowIfNull(scanOptions);
		ArgumentNullException.ThrowIfNull(complianceContentOptions);
		ArgumentNullException.ThrowIfNull(configDocs);
		ArgumentNullException.ThrowIfNull(attestationSnapshots);
		ArgumentNullException.ThrowIfNull(upload);
		ArgumentNullException.ThrowIfNull(componentProfileRevisions);
		ArgumentNullException.ThrowIfNull(benchmarks);
		ArgumentNullException.ThrowIfNull(resultRecording);

		_executor = executor;
		_secrets = secrets;
		_credentials = credentials;
		_targets = targets;
		_runSecrets = runSecrets;
		_jobs = jobs;
		_redactor = redactor;
		_powerShellOptions = powerShellOptions;
		_scanOptions = scanOptions;
		_complianceContentOptions = complianceContentOptions;
		_configDocs = configDocs;
		_attestationSnapshots = attestationSnapshots;
		_upload = upload;
		_componentProfileRevisions = componentProfileRevisions;
		_benchmarks = benchmarks;
		_resultRecording = resultRecording;
	}

	public string JobType => "scan";

	public Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		// ADR-0012 stage routing: a fresh claim (Stage == null) runs the InSpec stage;
		// a job resumed at a durable marker resumes at that stage's body instead of
		// re-running earlier ones. Any other marker is unreachable today (Standard's
		// only stages are attesting/converting) -- failing cleanly rather than
		// throwing keeps a future new marker's outcome predictable instead of an
		// opaque "Unhandled exception".
		return context.Job.Stage switch
		{
			null => ExecuteInspecStageAsync(context, cancellationToken),
			AttestingStage => ExecuteAttestStageAsync(context, cancellationToken),
			ConvertingStage => ExecuteConvertStageAsync(context, cancellationToken),
			_ => Task.FromResult(JobExecutionOutcome.Failed(
				$"not_implemented: scan stage '{context.Job.Stage}' has no handler.")),
		};
	}

	private async Task<JobExecutionOutcome> ExecuteInspecStageAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ScanPayload payload;
		try
		{
			payload = ParsePayload(context.Job.Payload);
		}
		catch (JsonException exception)
		{
			return JobExecutionOutcome.Failed($"scan payload is invalid: {exception.Message}");
		}
		catch (ArgumentException exception)
		{
			return JobExecutionOutcome.Failed($"scan payload is invalid: {exception.Message}");
		}

		Target? target = await _targets.GetAsync(payload.TargetId, cancellationToken).ConfigureAwait(false);
		if (target is null)
		{
			return JobExecutionOutcome.Failed($"target '{payload.TargetId}' does not exist.");
		}

		if (!string.Equals(target.Kind, TargetKinds.VSphere, StringComparison.Ordinal)
			&& !string.Equals(target.Kind, TargetKinds.NsxApi, StringComparison.Ordinal)
			&& !string.Equals(target.Kind, TargetKinds.Ssh, StringComparison.Ordinal))
		{
			return JobExecutionOutcome.Failed(
				$"target '{payload.TargetId}' is kind '{target.Kind}'; the InSpec stage only supports '{TargetKinds.VSphere}', '{TargetKinds.NsxApi}', and '{TargetKinds.Ssh}'.");
		}

		string? host = TryGetConnectionHost(target.ConnectionJson);
		if (string.IsNullOrWhiteSpace(host))
		{
			return JobExecutionOutcome.Failed($"target '{payload.TargetId}' has no 'connection.host' to scan.");
		}

		// Issue #741/#743: computed here (before credential resolution) so the ssh
		// invocation authenticates with the ITEM's own required purpose -- vcsa-ssh for
		// a named VCSA service, srg-ssh for a whole-appliance SSH product -- rather than
		// the OWNING TARGET's kind-default purpose (vsphere-api for a vsphere-kind
		// target, which is what a VCSA service item's target actually is).
		bool payloadIsSshTransportItem = string.Equals(payload.Transport, CatalogTransports.Ssh, StringComparison.Ordinal)
			&& payload.SelectorKind is CatalogSelectorKinds.Service or CatalogSelectorKinds.Target;
		string? executionPurposeOverride = payloadIsSshTransportItem
			? (payload.SelectorKind == CatalogSelectorKinds.Service ? CredentialPurposes.VcsaSsh : CredentialPurposes.SrgSsh)
			: null;

		ResolvedCredential resolved;
		try
		{
			resolved = await ResolveCredentialAsync(context, target.Kind, cancellationToken, executionPurposeOverride).ConfigureAwait(false);
		}
		catch (ScanCredentialException exception)
		{
			return JobExecutionOutcome.Failed(exception.Message);
		}

		string reportPath = Path.Combine(_scanOptions.Value.ArtifactStorePath, $"{context.Job.Id:N}.json");
		bool isNsx = string.Equals(target.Kind, TargetKinds.NsxApi, StringComparison.Ordinal);

		// Issue #741/#743: an ssh-transport plan item drives the SRG/ssh InSpec
		// invocation regardless of the OWNING TARGET's kind -- a VCSA service item
		// (#741) lives on a `vsphere`-kind target (the appliance is reached over ssh for
		// its own OS-level services, while the target's primary kind stays vsphere for
		// vCenter API access), and a whole-appliance SSH product item (#743, Photon/
		// Aria/vIDM) lives on an `ssh`-kind target. Routing by `payload.Transport` (the
		// item's own frozen catalog transport) rather than `target.Kind` is exactly the
		// #743 AC "connection kind ssh does not imply SRG, and SRG does not identify a
		// product... output behavior and components must come from the selected catalog
		// entry, not the target kind." A legacy/unnarrowed job (no transport on the
		// payload, or an `ssh`-kind target with no narrowable item at all) falls back to
		// the pre-#741 target-kind-driven classification, preserving byte-identical
		// behavior for every job shape that predates this issue.
		bool isSshTransportItem = payloadIsSshTransportItem;
		bool isSrg = isSshTransportItem || string.Equals(target.Kind, TargetKinds.Ssh, StringComparison.Ordinal);

		string legacyFallbackPath = isNsx ? _scanOptions.Value.NsxProfilePath : isSrg ? _scanOptions.Value.SrgProfilePath : _scanOptions.Value.ProfilePath;
		string profilePath = ResolveProfilePath(payload.ProfileKey, legacyFallbackPath);

		// Issue #738, generalized to esxi/vm by #739/#740 and to the ssh family
		// (VCSA service / whole-appliance product) by #741/#743: a narrowable execution
		// item (ScanComponentNarrowing already narrowed it at fan-out time via
		// SelectorKind/SelectorName) resolves its InSpec profile from the ACTIVATED
		// content-revision directory bound to the plan item's own frozen
		// CatalogExecutionProfileId/BaselineId, never the run-level profile_key/legacy
		// fixed path -- each selector/service is a distinct execution component and
		// DISA benchmark (or SRG closure), not the top-level connection's profile (this
		// issue's Motivation). Compatibility gate (AC "only compatible catalog profiles
		// can reach this handler"): the planner already guarantees a narrowable-selector
		// item only exists for a compatible execution profile
		// (ScanPlannerService/ComponentCapabilityMatcher), so this re-check is
		// defensive, not authoritative -- it fails closed rather than trusting an
		// unexpected payload shape.
		bool isVSphereComponent = string.Equals(target.Kind, TargetKinds.VSphere, StringComparison.Ordinal)
			&& string.Equals(payload.Transport, CatalogTransports.VMware, StringComparison.Ordinal)
			&& payload.SelectorKind is CatalogSelectorKinds.VCenter or CatalogSelectorKinds.Esxi or CatalogSelectorKinds.Vm;

		// Issue #742 (epic #726 Wave 3's final transport): a narrowed nsx-api/service
		// item -- one named NSX functional component (Manager, DFW, tier-0/tier-1
		// firewall/router, or a newer catalog-added set) -- resolves and executes its
		// OWN leaf profile exactly like the vSphere/ssh families, rather than the one
		// arbitrary whole-Manager profile the pre-#742 collapsed remainder job ran.
		// Gated on isNsx (the owning target's kind) the same defensive way
		// isVSphereComponent is gated on vsphere -- ScanComponentNarrowing.CanNarrow
		// already restricts which items reach this payload shape at fan-out time; this
		// re-check fails closed rather than trusting an unexpected combination.
		bool isNsxComponent = isNsx
			&& string.Equals(payload.Transport, CatalogTransports.NsxApi, StringComparison.Ordinal)
			&& string.Equals(payload.SelectorKind, CatalogSelectorKinds.Service, StringComparison.Ordinal);
		bool isNarrowedComponent = isVSphereComponent || isSshTransportItem || isNsxComponent;
		string? resolvedInputsYaml = null;
		Guid? attributedContentRevisionId = null;
		Guid? attributedBaselineId = null;
		List<string> droppedScopingKeys = [];
		if (isNarrowedComponent)
		{
			if (payload.CatalogExecutionProfileId is not { } componentExecutionProfileId || payload.BaselineId is not { } componentBaselineId)
			{
				return JobExecutionOutcome.Failed(
					$"job '{context.Job.Id}' is a '{payload.SelectorKind}' component item but carries no catalog_execution_profile_id/baseline_id -- the plan item this job was fanned out from should always freeze both; refusing to fall back to an unscoped profile.");
			}

			ComponentProfileRevisionResult revisionResult = await _componentProfileRevisions
				.ResolveAsync(componentExecutionProfileId, componentBaselineId, cancellationToken).ConfigureAwait(false);
			if (!revisionResult.Succeeded)
			{
				return JobExecutionOutcome.Failed(
					$"'{payload.SelectorKind}' component scan for target '{payload.TargetId}' could not resolve its activated profile revision: {revisionResult.FailureReason}");
			}

			profilePath = revisionResult.ProfilePath!;
			attributedContentRevisionId = revisionResult.ContentRevisionId;
			attributedBaselineId = revisionResult.BaselineId;

			// Issue #738 / #879: materialize this item's frozen resolved Input config
			// docs (Global -> Site -> Target, snapshotted at plan-compile time) as actual
			// InSpec inputs -- the runtime-materialization remainder #879 explicitly left
			// unimplemented ("the runner still resolves ... live against the fixed
			// AttestationProfile" applies to the input side too: nothing consumed
			// InputResolutions before this issue). Every entry not in the Resolved state
			// (Missing/Expired -- Expired never applies to Input, only Attestation) is
			// skipped: a missing REQUIRED input already prevented this item from being
			// accepted by the planner (ScanPlanSkipReasons.MissingRequiredInput), and a
			// missing OPTIONAL input has nothing to materialize.
			List<string> resolvedYamlBodies = [];
			foreach (ScanPayloadInputResolution inputResolution in payload.InputResolutionsOrEmpty)
			{
				if (!string.Equals(inputResolution.State, ConfigResolutionStates.Resolved, StringComparison.Ordinal)
					|| inputResolution.DocId is not { } inputDocId || inputResolution.DocVersion is not { } inputDocVersion)
				{
					continue;
				}

				ConfigDocVersion? version = await _configDocs.GetVersionAsync(inputDocId, inputDocVersion, cancellationToken).ConfigureAwait(false);
				if (version is not null && !string.IsNullOrWhiteSpace(version.BodyYaml))
				{
					resolvedYamlBodies.Add(version.BodyYaml);
				}
			}

			if (resolvedYamlBodies.Count > 0)
			{
				// Each resolved Input doc's body is itself a whole InSpec-inputs-shaped
				// YAML document (one per plan item, ADR-0024 "resolves at whole-document
				// granularity") -- concatenated (not merged key-by-key) into one generated
				// file; InSpec applies later `--input-file` keys over earlier ones on a
				// literal key collision, so ordering here follows the same
				// InputResolutions ordering PlanConfigResolutionService produced.
				//
				// Issue #911, extended by #741/#743 and #742: a NARROWED vmware selector
				// (esxi/vm -- a vcenter selector carries no narrowing key to begin with) is
				// at risk of an operator config-doc body overriding the platform's own
				// vmhostName/vmName scoping key -- the ssh family (service/target
				// selectors) introduces no analogous platform-computed scoping key of its
				// own (no --input-file selector-scope file is generated for ssh at all,
				// see below), so there is nothing for ScanScopingInputFilter's
				// vSphere-scoping keys to protect there; the filter is still applied
				// defensively so an ssh-family config doc is never silently exempted from a
				// future reserved key without a code change. The nsx-api family is
				// different in kind, not merely defensive: its generated auth-input keys
				// (nsxManager/sessionToken/sessionCookieId or the VCF 9.x nsx_* names) are
				// SECRET session material, not scoping -- an operator value colliding with
				// one of those keys must always be dropped, so NSX is filtered
				// unconditionally, same as every narrowed selector. Filtering here (a
				// WARN-logged drop, not a hard reject -- see ScanScopingInputFilter's doc
				// comment for the rationale) is the primary defense; WaypointScan.psm1's
				// own flag-ordering flip (the operator inputs file appended BEFORE the
				// platform/auth file, not after) is the second, independent one for every
				// family including NSX.
				string concatenatedYaml = string.Join('\n', resolvedYamlBodies);
				bool isNarrowedSelector = payload.SelectorKind is CatalogSelectorKinds.Esxi or CatalogSelectorKinds.Vm;
				if (isNarrowedSelector || isSshTransportItem || isNsxComponent)
				{
					ScanScopingFilterResult filterResult = ScanScopingInputFilter.Filter(concatenatedYaml);
					resolvedInputsYaml = filterResult.FilteredYaml;
					droppedScopingKeys = [.. filterResult.DroppedKeys];
				}
				else
				{
					resolvedInputsYaml = concatenatedYaml;
				}
			}
		}

		if (droppedScopingKeys.Count > 0)
		{
			string warnLine = $"job '{context.Job.Id}' operator config-doc inputs for target '{payload.TargetId}' "
				+ $"named reserved selector-scoping/auth key(s) [{string.Join(", ", droppedScopingKeys)}] -- dropped; "
				+ $"the platform's own '{payload.SelectorKind}' scope/session ('{payload.SelectorName}') was applied instead (issues #911/#742).";
			await EmitWarnAsync(context, warnLine, cancellationToken).ConfigureAwait(false);
		}

		PowerShellExecutionResult result;
		string? inputsFilePath = null;
		try
		{
			// The NSX path passes Manager/Username/Password/ProfilePath/ReportPath to
			// Invoke-WaypointNsxScan, which acquires the session token itself, inside its
			// own PowerShell invocation -- the token is generated and consumed entirely
			// within that call and never crosses back into this C# handler, so there is
			// nothing here to additionally track via ISecretTracker: the token's whole
			// lifetime sits inside the same bound-parameter, non-argv, non-logged
			// invocation the vSphere password already relies on (security.md controls
			// 1/2). Password is still resolved and bound the same way for both kinds --
			// NSX authenticates to /api/session/create with it before InSpec ever runs.
			//
			// The SRG (ssh) path passes Sudo/SudoRequiresPassword alongside the same
			// Host/Username/Password shape (issue #309 AC "sudo_enabled honored"):
			// SudoEnabled comes from the resolved credential's typed field for a stored
			// credential (#249), or false for the ephemeral "my credentials" tier, which
			// carries no sudo flag (ADR-0011 scope -- see ResolveCredentialAsync). The ssh
			// password doubles as the sudo password when sudo is enabled, matching the
			// sibling repo's own module.scan.ps1 SRG branch (Config.Sudo -> --sudo,
			// SudoRequiresPassword -> the same credential's password via --config).
			Dictionary<string, object?> parameters;
			string invocationCommand;
			if (isNsx)
			{
				invocationCommand = NsxInvocationCommand;
				parameters = new(StringComparer.Ordinal)
				{
					["Manager"] = host,
					["Username"] = resolved.Username,
					["Password"] = resolved.Secret,
					["ProfilePath"] = profilePath,
					["ReportPath"] = reportPath,
					["TimeoutSeconds"] = _scanOptions.Value.TimeoutSeconds,
				};

				// Issue #742: a narrowed nsx-api/service item passes its own catalog
				// named-function identity (manager/dfw/tier0-fw/...) for
				// logging/diagnostics only -- SelectorName never scopes the NSX Manager
				// API call itself (unlike vmware's esxi/vm object narrowing); the actual
				// per-component scoping already happened via profilePath resolving to
				// THIS component's own activated leaf profile above. A legacy/unnarrowed
				// nsx-api job (no SelectorKind on the payload) omits it, preserving the
				// pre-#742 invocation exactly.
				if (isNsxComponent && !string.IsNullOrWhiteSpace(payload.SelectorName))
				{
					parameters["SelectorName"] = payload.SelectorName;
				}

				// Issue #742, same non-argv discipline as the vmware/ssh families' own
				// InputsFilePath: this narrowed nsx-api component's frozen resolved Input
				// config docs, materialized into a generated, owner-only 0600
				// --input-file. resolvedInputsYaml was already unconditionally filtered
				// through ScanScopingInputFilter above for every nsx-api component (the
				// NSX auth-input keys are reserved regardless of selector), and
				// Invoke-WaypointNsxScan appends this flag BEFORE its own generated
				// auth-block file -- so the runner's real session always wins InSpec's
				// last-file-wins resolution over any operator value, even one that
				// somehow survived the C#-side filter.
				if (isNsxComponent && resolvedInputsYaml is not null)
				{
					inputsFilePath = Path.Combine(
						_scanOptions.Value.ArtifactStorePath, $"{context.Job.Id:N}.nsx-inputs.generated.yml");
					using (FileStream createStream = File.Create(inputsFilePath))
					{
						if (!OperatingSystem.IsWindows())
						{
							File.SetUnixFileMode(inputsFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
						}
					}

					await File.WriteAllTextAsync(inputsFilePath, resolvedInputsYaml, cancellationToken).ConfigureAwait(false);
					parameters["InputsFilePath"] = inputsFilePath;
				}
			}
			else if (isSrg)
			{
				// Issue #743: sudo policy is CONTENT knowledge -- the catalog component
				// declares whether this profile must run under sudo and whether that sudo
				// prompts for a password (frozen into the plan item/payload at
				// plan-compile time, migration 0074), exactly like the sibling repository's
				// own catalog (`$Comp.sudo`/`$Comp.sudoRequiresPassword`,
				// module.transport.ssh.ps1). The pre-#743 shape -- Sudo from the resolved
				// credential's SudoEnabled flag, SudoRequiresPassword hard-coded true --
				// could not express the documented per-product policies (vIDM: sudo with
				// password; Photon: passwordless sudo; Aria family: no sudo) and remains
				// ONLY as the fallback for a legacy/unnarrowed payload carrying no frozen
				// policy, preserving that shape byte-identically.
				bool sudo = payload.RequiresSudo ?? resolved.SudoEnabled;
				bool sudoRequiresPassword = payload.SudoRequiresPassword ?? true;

				invocationCommand = SrgInvocationCommand;
				parameters = new(StringComparer.Ordinal)
				{
					["SshHost"] = host,
					["Username"] = resolved.Username,
					["Password"] = resolved.Secret,
					["ProfilePath"] = profilePath,
					["ReportPath"] = reportPath,
					["TimeoutSeconds"] = _scanOptions.Value.TimeoutSeconds,
					["Sudo"] = sudo,
					["SudoRequiresPassword"] = sudoRequiresPassword,
				};

				// Honest signal, not a gate: the catalog says this profile needs sudo but
				// the STORED credential explicitly declares sudo disabled (#249's typed
				// flag). The catalog policy still runs (the scan fails honestly at the
				// appliance if the account genuinely cannot sudo -- silently dropping
				// --sudo would instead produce quietly wrong results); the WARN gives the
				// operator the actionable why when it does.
				if (payload.RequiresSudo == true && !resolved.SudoEnabled && context.Job.CredentialId is not null)
				{
					await EmitWarnAsync(
						context,
						$"job '{context.Job.Id}': the catalog component requires sudo but the stored ssh credential is marked sudo-disabled; "
							+ "executing with --sudo per the catalog policy -- update the credential's sudo_enabled flag if elevation fails.",
						cancellationToken).ConfigureAwait(false);
				}

				// Issue #741/#743, generalized from the vmware family's #738/#879
				// mechanism: an ssh-transport narrowed item's (VCSA service or
				// whole-appliance product) frozen resolved Input config docs, materialized
				// into a generated, owner-only 0600 --input-file -- same non-argv
				// discipline as the vmware path. ScanScopingInputFilter was already
				// applied to resolvedInputsYaml above for every ssh-transport item
				// (defensive: the ssh family has no platform-computed scoping key of its
				// own today, but the filter is applied unconditionally rather than
				// silently exempting this family from a future reserved key).
				if (isSshTransportItem && resolvedInputsYaml is not null)
				{
					inputsFilePath = Path.Combine(
						_scanOptions.Value.ArtifactStorePath, $"{context.Job.Id:N}.ssh-inputs.generated.yml");
					using (FileStream createStream = File.Create(inputsFilePath))
					{
						if (!OperatingSystem.IsWindows())
						{
							File.SetUnixFileMode(inputsFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
						}
					}

					await File.WriteAllTextAsync(inputsFilePath, resolvedInputsYaml, cancellationToken).ConfigureAwait(false);
					parameters["InputsFilePath"] = inputsFilePath;
				}
			}
			else
			{
				invocationCommand = InvocationCommand;
				parameters = new(StringComparer.Ordinal)
				{
					["VCenter"] = host,
					["Username"] = resolved.Username,
					["Password"] = resolved.Secret,
					["ProfilePath"] = profilePath,
					["ReportPath"] = reportPath,
					["TimeoutSeconds"] = _scanOptions.Value.TimeoutSeconds,
				};

				// Issue #737 item-4: a NARROWABLE plan item (the vSphere-family object
				// selectors -- ScanComponentNarrowing.CanNarrow) scopes the InSpec vmware
				// invocation to THAT component's object (this ESXi host / this VM /
				// vCenter-scoped controls) rather than the whole vCenter, so N sibling
				// component jobs no longer each re-scan the entire target. The ONE
				// collapsed whole-target remainder job carries Unnarrowed == true (and no
				// selector), and a legacy whole-target job carries no selector at all --
				// both fall through with no SelectorKind/SelectorName, preserving the
				// pre-#737 whole-vCenter invocation exactly. SelectorName is passed only
				// for the object-identifying kinds (esxi/vm); a vcenter selector needs no
				// name (the whole vCenter IS the object).
				if (!payload.Unnarrowed && ScanComponentNarrowing.CanNarrow(payload.Transport, payload.SelectorKind))
				{
					parameters["SelectorKind"] = payload.SelectorKind;
					if (!string.IsNullOrWhiteSpace(payload.SelectorName))
					{
						parameters["SelectorName"] = payload.SelectorName;
					}
				}

				// Issue #738/#879, generalized to esxi/vm by #739/#740: the component
				// item's frozen resolved Input config docs, materialized into a
				// generated, owner-only 0600 --input-file -- same non-argv discipline as
				// the selector-scoping file above and Invoke-WaypointNsxScan's
				// session-token file. A separate file (not merged text) so
				// Invoke-WaypointScan can pass it as its own `--input-file` flag
				// alongside the selector file; InSpec accepts multiple `--input-file`
				// flags on one invocation. Issue #911: for esxi/vm this body was already
				// filtered of reserved scoping keys above, AND WaypointScan.psm1 appends
				// this flag BEFORE the selector-scoping flag -- so even an unfiltered
				// collision would still lose to the platform's own scope.
				if (isVSphereComponent && resolvedInputsYaml is not null)
				{
					inputsFilePath = Path.Combine(
						_scanOptions.Value.ArtifactStorePath, $"{context.Job.Id:N}.vsphere-inputs.generated.yml");
					using (FileStream createStream = File.Create(inputsFilePath))
					{
						if (!OperatingSystem.IsWindows())
						{
							File.SetUnixFileMode(inputsFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
						}
					}

					await File.WriteAllTextAsync(inputsFilePath, resolvedInputsYaml, cancellationToken).ConfigureAwait(false);
					parameters["InputsFilePath"] = inputsFilePath;
				}
			}

			PowerShellRequest request = new(
				invocationCommand, PowerShellRequestKind.Command, parameters, context.Job.Id, context.Job.RunId);
			result = await ExecuteOrSyntheticFailureAsync(
				() => _executor.ExecuteAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			if (inputsFilePath is not null && File.Exists(inputsFilePath))
			{
				File.Delete(inputsFilePath);
			}

			// Ends the in-play redaction window as soon as the invocation is done, same
			// discipline as DiscoverJobHandler regardless of which credential tier this
			// came from -- a stored credential's DecryptedSecret and an ad hoc run
			// secret's DecryptedRunSecret both dispose their own redaction handle.
			resolved.Release();
		}

		// Transport-level failure (the invocation itself threw, timed out, or a native
		// exit code outside 0 -- WaypointScan.psm1 never lets a native `inspec` call
		// leave a non-{0,100,101} exit code unhandled, so a nonzero NativeExitCode here
		// means Invoke-ExternalCommand's own AllowedExitCodes check rejected it).
		if (!result.Succeeded)
		{
			string rawNote = result.FailureReason ?? "scan invocation failed with no failure reason.";
			return await FailScanAsync(context, rawNote, cancellationToken).ConfigureAwait(false);
		}

		// Predecessor constraint (#274 AC): InSpec exit codes 100 (compliance failures
		// present) and 101 (skipped controls present) are a completed, reportable scan,
		// not a tool failure -- WaypointScan.psm1 already absorbed that mapping at the
		// `Invoke-ExternalCommand -AllowedExitCodes @(0,100,101)` layer, so a
		// `Succeeded` PowerShellExecutionResult never reaches here for one of those
		// codes. What remains to check is the module's own Success/FailureReason
		// output -- a normal (non-throwing, zero-native-exit) invocation whose function
		// body itself reported failure (e.g. an auth rejection with no native exit code
		// at all) still needs classifying here, exactly as
		// DownloadJobHandler.TryParseOutput inspects its own module's returned object
		// rather than trusting the executor's Succeeded alone.
		ScanInvocationOutput? output = TryParseOutput(result.Output);
		if (output is null)
		{
			return await FailScanAsync(context, "scan invocation returned no result.", cancellationToken).ConfigureAwait(false);
		}

		if (!output.Success)
		{
			string rawNote = SelectFailureNote(output.FailureReason, result.ErrorLines);
			return await FailScanAsync(context, rawNote, cancellationToken).ConfigureAwait(false);
		}

		if (string.IsNullOrWhiteSpace(output.ReportPath) || !File.Exists(output.ReportPath))
		{
			return await FailScanAsync(context, "scan invocation reported success but produced no HDF report file.", cancellationToken).ConfigureAwait(false);
		}

		// Issue #738 AC "HDF contains stable component/endpoint attribution", generalized
		// to esxi/vm by #739/#740's own per-host/per-VM AC, and to the ssh family
		// (VCSA service / whole-appliance product) by #741/#743: a narrowed component
		// job's structured completion event carries the exact component, endpoint
		// (target host), selector (kind + the stable identity -- the discovered object's
		// own vendor identity for esxi/vm, or the catalog's own named-service identity
		// for an ssh `service` selector, stable regardless of display-name churn), and
		// resolved content-revision/baseline identity it executed against --
		// non-secret identifiers only (host, ids, vendor/service identity), never a
		// credential -- so Live Run/Results and any later HDF-metadata enrichment have a
		// stable, provenance-complete attribution record independent of re-deriving it
		// from current (possibly since-changed) catalog/component/inventory state.
		if (isNarrowedComponent)
		{
			await EmitComponentAttributionAsync(
				context, payload, host!, attributedContentRevisionId, attributedBaselineId, cancellationToken).ConfigureAwait(false);
		}

		await context.AdvanceAsync(JobStates.Attesting, "InSpec scan complete; HDF persisted.", cancellationToken).ConfigureAwait(false);
		return JobExecutionOutcome.StageComplete(AttestingStage, $"HDF persisted at '{output.ReportPath}'.");
	}

	/// <summary>
	/// Issue #738, generalized to esxi/vm by #739/#740 and to the ssh family (VCSA
	/// service / whole-appliance product) by #741/#743: one structured <c>job.log</c>
	/// Info event naming the exact component/endpoint/selector/content identity this
	/// job executed -- emitted once, right after a successful InSpec completion,
	/// alongside (not instead of) the ordinary stage-advance note. Component/endpoint/
	/// selector attribution only; no credential material.
	/// </summary>
	private static async Task EmitComponentAttributionAsync(
		JobExecutionContext context,
		ScanPayload payload,
		string endpointHost,
		Guid? contentRevisionId,
		Guid? baselineId,
		CancellationToken cancellationToken)
	{
		string line = $"{payload.SelectorKind} component scan complete: component '{payload.ComponentId}' "
			+ $"selector '{payload.SelectorKind}/{payload.SelectorName}' at endpoint '{endpointHost}', "
			+ $"content_revision '{contentRevisionId}', baseline '{baselineId}'.";
		string eventPayload = JsonSerializer.Serialize(new
		{
			severity = "Info",
			line,
			component_id = payload.ComponentId,
			selector_kind = payload.SelectorKind,
			selector_name = payload.SelectorName,
			endpoint = endpointHost,
			content_revision_id = contentRevisionId,
			baseline_id = baselineId,
		});
		await context.Events.EmitAsync(JobEventTypes.JobLog, context.Job.Id, context.Job.RunId, eventPayload, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// The attest stage (issue #275): resolves the target's attestation config-doc via
	/// <see cref="ConfigDocResolver.Resolve"/> (Global -&gt; Site -&gt; Target,
	/// most-specific-wins, same resolution <c>GET /config-docs/resolve</c> uses) and
	/// applies it to the HDF with the SAF CLI. An expired waiver is never applied --
	/// <see cref="ConfigDocResolution.AttestationExpired"/> already encodes that per
	/// <see cref="ConfigDocResolver"/>'s fall-through -- so it is reported as a WARN
	/// <c>job.log</c> event and folded into this stage's <see cref="JobExecutionOutcome.Note"/>
	/// (docs/domain-model.md: "the control reports Open, the run logs a WARN, and
	/// Results lists expired attestations explicitly"). No resolved doc at all (a
	/// target with no attestation config-doc anywhere in the three layers) is an
	/// equally valid path: the HDF passes through unattested.
	/// </summary>
	private async Task<JobExecutionOutcome> ExecuteAttestStageAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ScanPayload payload;
		try
		{
			payload = ParsePayload(context.Job.Payload);
		}
		catch (JsonException exception)
		{
			return JobExecutionOutcome.Failed($"scan payload is invalid: {exception.Message}");
		}
		catch (ArgumentException exception)
		{
			return JobExecutionOutcome.Failed($"scan payload is invalid: {exception.Message}");
		}

		Target? target = await _targets.GetAsync(payload.TargetId, cancellationToken).ConfigureAwait(false);
		if (target is null)
		{
			return JobExecutionOutcome.Failed($"target '{payload.TargetId}' does not exist.");
		}

		string reportPath = Path.Combine(_scanOptions.Value.ArtifactStorePath, $"{context.Job.Id:N}.json");
		if (!File.Exists(reportPath))
		{
			return JobExecutionOutcome.Failed($"attest stage found no HDF report at '{reportPath}' (InSpec stage did not persist one).");
		}

		string profile = _scanOptions.Value.AttestationProfile;
		ConfigDocWithLatestVersion? global = await _configDocs
			.FindWithLatestVersionAsync(ConfigDocKinds.Attestation, profile, ConfigDocLayers.Global, null, cancellationToken).ConfigureAwait(false);
		ConfigDocWithLatestVersion? site = await _configDocs
			.FindWithLatestVersionAsync(ConfigDocKinds.Attestation, profile, ConfigDocLayers.Site, target.SiteId, cancellationToken).ConfigureAwait(false);
		ConfigDocWithLatestVersion? targetDoc = await _configDocs
			.FindWithLatestVersionAsync(ConfigDocKinds.Attestation, profile, ConfigDocLayers.Target, target.Id, cancellationToken).ConfigureAwait(false);

		ConfigDocResolution resolution = ConfigDocResolver.Resolve(
			ConfigDocKinds.Attestation, profile, global, site, targetDoc, DateTimeOffset.UtcNow);

		if (resolution.AttestationExpired)
		{
			string warnLine = $"config-doc attestation for profile '{profile}' target '{payload.TargetId}' expired "
				+ $"{resolution.AttestationExpiresAt:O}; not applied (control remains Open).";
			await EmitWarnAsync(context, warnLine, cancellationToken).ConfigureAwait(false);
		}

		// Issue #306: persist THIS resolution, right now, as the immutable at-scan-time
		// record -- before anything downstream (or any later edit to the config-doc) can
		// affect what GET /runs/{id}/attestations-applied will ever report for this run.
		// context.Job.RunId is null only for a scan job created outside the normal
		// run-fan-out path, which does not happen in practice (RunsController always
		// fans scan jobs out from a run) -- skipped defensively rather than failing the
		// scan, since there would be no run to attribute the row to.
		if (context.Job.RunId is { } runId)
		{
			await _attestationSnapshots.RecordAsync(
				runId,
				context.Job.Id,
				target.Id,
				profile,
				resolution.Layer ?? ConfigDocLayers.Global,
				resolution.DocId,
				resolution.Version,
				resolution.Author,
				resolution.UpdatedAt,
				applied: resolution.Body is not null,
				expired: resolution.AttestationExpired,
				cancellationToken).ConfigureAwait(false);
		}

		string? templatePath = null;
		string? tempFile = null;
		try
		{
			if (resolution.Body is not null)
			{
				tempFile = Path.Combine(Path.GetTempPath(), $"waypoint-attest-{context.Job.Id:N}.yml");

				// Issue #304's 0600 pattern (mirrors #308/PR #315's NSX inputs-file fix):
				// create the file empty, narrow its mode to owner-only on Unix, THEN write
				// the resolved attestation body -- so it is never world-readable (the shared
				// system temp dir's umask default is typically 0644) even briefly. The body
				// is waiver content (status/justification/expires), not a raw secret, but a
				// justification can carry sensitive rationale worth this defense in depth.
				using (FileStream createStream = File.Create(tempFile))
				{
					if (!OperatingSystem.IsWindows())
					{
						File.SetUnixFileMode(
							tempFile,
							UnixFileMode.UserRead | UnixFileMode.UserWrite);
					}
				}

				await File.WriteAllTextAsync(tempFile, resolution.Body, cancellationToken).ConfigureAwait(false);
				templatePath = tempFile;
			}

			string attestedPath = Path.Combine(_scanOptions.Value.ArtifactStorePath, $"{context.Job.Id:N}.attested.json");
			Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
			{
				["ReportPath"] = reportPath,
				["AttestTemplatePath"] = templatePath,
				["AttestedReportPath"] = attestedPath,
				["TimeoutSeconds"] = _scanOptions.Value.SafTimeoutSeconds,
			};

			PowerShellRequest request = new(AttestCommand, PowerShellRequestKind.Command, parameters, context.Job.Id, context.Job.RunId);
			PowerShellExecutionResult result = await ExecuteOrSyntheticFailureAsync(
				() => _executor.ExecuteAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false);

			if (!result.Succeeded)
			{
				return await FailScanAsync(context, result.FailureReason ?? "attest invocation failed with no failure reason.", cancellationToken).ConfigureAwait(false);
			}

			AttestInvocationOutput? output = TryParseAttestOutput(result.Output);
			if (output is null || !output.Success)
			{
				string rawNote = output?.FailureReason ?? "attest invocation returned no result.";
				return await FailScanAsync(context, rawNote, cancellationToken).ConfigureAwait(false);
			}

			string note = resolution.AttestationExpired
				? $"attest: {(output.AttestApplied ? "applied" : "none applied")}; 1 expired-skipped (profile '{profile}')."
				: $"attest: {(output.AttestApplied ? "applied" : "none applied")}.";

			// A fresh claim always resumes at `running` (JobQueueRepository.ClaimSql
			// unconditionally sets state = 'running' regardless of the durable `stage`
			// marker -- only `stage` records pipeline position, per ADR-0012). This
			// stage's own resumption therefore first replays the `running -> attesting`
			// transition PR #298's InSpec stage made durable on the previous cycle,
			// before advancing further -- legal per JobStateMachine for both shapes, and
			// consistent with how the row's `state` column always reads 'running' the
			// instant any stage is (re)claimed.
			await context.AdvanceAsync(JobStates.Attesting, "attest stage claimed.", cancellationToken).ConfigureAwait(false);

			// Issue #309 AC "HDF-only: attest then terminate at done, NO convert, NO CKL,
			// NO STIG Manager upload", output routing generalized by #741/#743 to the
			// item's own frozen CATALOG kind rather than the target's connection kind
			// (#743 AC "catalog kind, not target kind, determines HDF-only versus CKL
			// pipeline" -- test-pinned, never inferred): an HDF-only item's attest stage
			// is the Srg shape's LAST stage (JobStateMachine.SrgTransitions: attesting ->
			// done), unlike Standard's attesting -> converting. This MUST agree with
			// JobShapes.ForJob's OWN output_kind-first read of the identical payload
			// field -- both read the same value so the state machine JobShapes selected
			// at claim time and the terminal transition this stage actually attempts can
			// never disagree (a disagreement would attempt an illegal transition against
			// JobStateMachine). Reporting Succeeded here (rather than
			// StageComplete(ConvertingStage)) makes ExecuteAsync's Stage switch never see
			// 'converting' for this job at all -- so the convert stage's STIG Manager
			// upload path is unreachable for HDF-only output by construction, not by a
			// runtime kind check inside convert. A legacy/unnarrowed job with no
			// output_kind on its payload falls back to the pre-#741 target-kind
			// inference, preserving byte-identical behavior for every job shape that
			// predates this issue.
			bool isHdfOnly = payload.OutputKind is { } frozenOutputKind
				? string.Equals(frozenOutputKind, CatalogOutputKinds.Hdf, StringComparison.Ordinal)
				: string.Equals(target.Kind, TargetKinds.Ssh, StringComparison.Ordinal);
			if (isHdfOnly)
			{
				// Issue #745: this is the Srg shape's terminal stage (attesting -> done),
				// so it is the only place an HDF-only component's result is ever
				// recordable -- additive, never affects `note`/the job outcome below.
				await _resultRecording.RecordCompletedAsync(
					context.Job,
					hdfPath: reportPath,
					attestedHdfPath: File.Exists(attestedPath) ? attestedPath : null,
					cklPath: null,
					cancellationToken).ConfigureAwait(false);
				return JobExecutionOutcome.Succeeded(note);
			}

			await context.AdvanceAsync(JobStates.Converting, note, cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.StageComplete(ConvertingStage, note);
		}
		finally
		{
			if (tempFile is not null && File.Exists(tempFile))
			{
				File.Delete(tempFile);
			}
		}
	}

	/// <summary>
	/// The convert stage (issue #275): converts the (optionally attested) HDF to a CKL
	/// with the SAF CLI, stamps <see cref="ScanOptions.BenchmarkMetadata"/>, and
	/// persists the CKL next to the HDF under the same artifact-store keying (job id).
	/// This is the pipeline's last stage for <see cref="JobShape.Standard"/> -- success
	/// here reports <see cref="JobOutcomeKind.Succeeded"/>, which the dispatcher forces
	/// to the shape's terminal state (<c>uploaded</c>, meaning "artifacts ready"; the
	/// STIG Manager upload itself is #25).
	/// </summary>
	private async Task<JobExecutionOutcome> ExecuteConvertStageAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ScanPayload payload;
		try
		{
			payload = ParsePayload(context.Job.Payload);
		}
		catch (JsonException exception)
		{
			return JobExecutionOutcome.Failed($"scan payload is invalid: {exception.Message}");
		}
		catch (ArgumentException exception)
		{
			return JobExecutionOutcome.Failed($"scan payload is invalid: {exception.Message}");
		}

		Target? target = await _targets.GetAsync(payload.TargetId, cancellationToken).ConfigureAwait(false);
		if (target is null)
		{
			return JobExecutionOutcome.Failed($"target '{payload.TargetId}' does not exist.");
		}

		string attestedPath = Path.Combine(_scanOptions.Value.ArtifactStorePath, $"{context.Job.Id:N}.attested.json");
		string rawPath = Path.Combine(_scanOptions.Value.ArtifactStorePath, $"{context.Job.Id:N}.json");
		string convertInput = File.Exists(attestedPath) ? attestedPath : rawPath;
		if (!File.Exists(convertInput))
		{
			return JobExecutionOutcome.Failed($"convert stage found no HDF report for job '{context.Job.Id}'.");
		}

		string cklPath = Path.Combine(_scanOptions.Value.ArtifactStorePath, $"{context.Job.Id:N}.ckl");

		// Issue #741/#743: a narrowed component job's frozen BenchmarkRevisionId (set by
		// the planner at plan-compile time from the catalog's own component-to-
		// benchmark-revision mapping, #828/#834 -- ADR-0022) is the authoritative CKL
		// identity for ANY component, VCSA service included -- never inferred from the
		// owning target's kind (a VCSA STIG service on a vsphere-kind target has its own
		// distinct benchmark, unrelated to any vSphere connection-level benchmark stamp).
		// A legacy/unnarrowed job (no BenchmarkRevisionId on its payload) falls back to
		// the pre-#741 static target-kind-keyed stamp, preserving byte-identical
		// behavior for every job shape that predates this issue. This stage is only
		// reachable for HDF+CKL output (ExecuteAttestStageAsync's routing above), so a
		// narrowed item reaching here always has a benchmark identity when its catalog
		// kind is STIG (an SRG item never reaches convert at all).
		ScanBenchmarkMetadata? staticMetadata = _scanOptions.Value.BenchmarkMetadata.GetValueOrDefault(target.Kind);
		StigManagerBenchmarkMetadata fallback = new(staticMetadata?.BenchmarkId, staticMetadata?.Title, staticMetadata?.ReleaseInfo, staticMetadata?.Version);

		// Issue #744: the mapped benchmark revision's own rules (migration 0052's
		// benchmark_rules) are the authoritative CKL rule/vuln identity source -- read
		// here (not just the revision's own title/version already read below) so the
		// convert invocation can correct each CKL Vuln entry's Rule_ID/Vuln_Num against
		// them, never trusting whatever SAF's hdf2ckl converter derived from the raw
		// HDF alone. Empty for a legacy/unmapped job (no frozen BenchmarkRevisionId) --
		// rule correction is then skipped entirely by Invoke-WaypointConvert, matching
		// the pre-#744 behavior exactly.
		Dictionary<string, string?> ruleCorrections = [];
		if (payload.BenchmarkRevisionId is { } benchmarkRevisionId)
		{
			BenchmarkRevision? revision = await _benchmarks.GetRevisionAsync(benchmarkRevisionId, cancellationToken).ConfigureAwait(false);
			if (revision is not null)
			{
				fallback = new StigManagerBenchmarkMetadata(revision.BenchmarkKey, revision.Title, revision.Release, revision.Version);

				IReadOnlyList<BenchmarkRule> rules = await _benchmarks.ListRulesAsync(benchmarkRevisionId, cancellationToken).ConfigureAwait(false);
				foreach (BenchmarkRule rule in rules)
				{
					ruleCorrections[rule.RuleId] = rule.VulnId;
				}
			}
		}

		// Issue #311: enrich the resolved stamp with STIG Manager's installed
		// title/release/version when reachable -- degrades to fallback unchanged
		// on any failure (ScanUploadCoordinator.ResolveBenchmarkMetadataAsync never
		// throws), so this call site needs no try/catch of its own, matching the
		// predecessor Resolve-BenchmarkMetadata's "resolution never throws" contract.
		StigManagerBenchmarkMetadata metadata = await _upload
			.ResolveBenchmarkMetadataAsync(context.Job.Id, target, fallback, cancellationToken).ConfigureAwait(false);

		// Same replay as ExecuteAttestStageAsync: a fresh claim always resumes at
		// `running` (ClaimSql), so this stage first replays `running -> attesting`
		// (the InSpec stage's own durable transition) then `attesting -> converting`
		// before doing any convert work -- both legal per JobStateMachine, and correct
		// regardless of how the convert step itself turns out: a subsequent failure
		// legally transitions converting -> failed, and success legally transitions
		// converting -> uploaded (forced by the dispatcher on JobOutcomeKind.Succeeded).
		await context.AdvanceAsync(JobStates.Attesting, "convert stage claimed.", cancellationToken).ConfigureAwait(false);
		await context.AdvanceAsync(JobStates.Converting, "convert stage claimed.", cancellationToken).ConfigureAwait(false);

		Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
		{
			["ConvertInputPath"] = convertInput,
			["CklOutputPath"] = cklPath,
			["BenchmarkId"] = metadata.BenchmarkId,
			["Title"] = metadata.Title,
			["ReleaseInfo"] = metadata.ReleaseInfo,
			["Version"] = metadata.Version,
			["TimeoutSeconds"] = _scanOptions.Value.SafTimeoutSeconds,
		};
		if (ruleCorrections.Count > 0)
		{
			Dictionary<string, object?> ruleCorrectionsForWire = new(StringComparer.Ordinal);
			foreach (KeyValuePair<string, string?> entry in ruleCorrections)
			{
				ruleCorrectionsForWire[entry.Key] = entry.Value;
			}

			parameters["RuleCorrections"] = ruleCorrectionsForWire;
		}

		PowerShellRequest request = new(ConvertCommand, PowerShellRequestKind.Command, parameters, context.Job.Id, context.Job.RunId);
		PowerShellExecutionResult result = await ExecuteOrSyntheticFailureAsync(
			() => _executor.ExecuteAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false);

		if (!result.Succeeded)
		{
			return await FailScanAsync(context, result.FailureReason ?? "convert invocation failed with no failure reason.", cancellationToken).ConfigureAwait(false);
		}

		ConvertInvocationOutput? output = TryParseConvertOutput(result.Output);
		if (output is null || !output.Success)
		{
			string rawNote = output?.FailureReason ?? "convert invocation returned no result.";
			return await FailScanAsync(context, rawNote, cancellationToken).ConfigureAwait(false);
		}

		if (string.IsNullOrWhiteSpace(output.CklPath) || !File.Exists(output.CklPath))
		{
			return await FailScanAsync(context, "convert invocation reported success but produced no CKL file.", cancellationToken).ConfigureAwait(false);
		}

		// Issue #744 AC "rule-level correction coverage is measured; unresolved/
		// ambiguous rules are visible and cannot masquerade as complete": emit a
		// structured job.log event with the coverage counts and the exact unmatched
		// rule ids -- never silently dropped. No event at all when no correction was
		// attempted (ruleCorrections empty -- legacy/unmapped job), matching the
		// pre-#744 behavior for every job shape without a frozen benchmark revision.
		if (ruleCorrections.Count > 0)
		{
			await EmitRuleCoverageAsync(context, output.RuleCoverage, cancellationToken).ConfigureAwait(false);
		}

		// Issue #311: post-convert upload, not a new pipeline stage -- ADR-0012's
		// `uploaded` terminal already means "artifacts ready" (PR #302), so this action
		// runs inside the same convert-stage execution rather than resting the job at a
		// new intermediate marker. ScanUploadCoordinator.UploadAsync never throws and
		// always persists a jobs.upload_status regardless of outcome; this stage reports
		// Succeeded either way (issue #311 AC: "upload failure must NEVER fail the scan
		// run" -- artifacts stay downloadable, only upload_status reflects the failure).
		// Only reachable from JobShape.Standard's convert stage, so SRG/HDF-only runs
		// (#24/#309) never call this -- they terminate at `attesting -> done` and this
		// method is never entered for them.
		StigManagerUploadResult uploadResult = await _upload.UploadAsync(context.Job.Id, target, output.CklPath, cancellationToken).ConfigureAwait(false);
		string uploadNote = uploadResult.Outcome switch
		{
			StigManagerUploadOutcome.Uploaded => "uploaded to STIG Manager",
			StigManagerUploadOutcome.Conflict => "STIG Manager reported a conflict (retry available)",
			_ => $"STIG Manager upload failed: {uploadResult.Detail ?? "no detail"}",
		};

		// Issue #745: convert is the Standard shape's terminal stage -- the one place a
		// STIG component's full HDF+CKL result is recordable. Additive: never affects
		// the note/outcome below, and a recording failure is swallowed internally.
		await _resultRecording.RecordCompletedAsync(
			context.Job,
			hdfPath: File.Exists(rawPath) ? rawPath : null,
			attestedHdfPath: File.Exists(attestedPath) ? attestedPath : null,
			cklPath: File.Exists(output.CklPath) ? output.CklPath : null,
			cancellationToken).ConfigureAwait(false);

		return JobExecutionOutcome.Succeeded(
			$"CKL persisted at '{output.CklPath}' (benchmark metadata applied: {output.MetadataApplied}; {uploadNote}).");
	}

	/// <summary>
	/// Issue #1020 retry-honesty backstop: runs <paramref name="invoke"/> (an
	/// <see cref="IPowerShellExecutor.ExecuteAsync"/> call) and converts any exception
	/// that escapes it into a synthetic failed <see cref="PowerShellExecutionResult"/>
	/// instead of letting it propagate. Before this, a throw from runspace/module
	/// INIT -- e.g. the pool's <c>AnalysisCache</c> race (the crash this issue exists
	/// to prevent) or any other transient the pool surfaces as an exception rather
	/// than a result -- skipped every stage's existing "<c>!result.Succeeded</c> -&gt;
	/// <see cref="FailScanAsync"/>" classification entirely and reached the job
	/// dispatcher's generic handler-threw catch instead: the job still ended up
	/// Failed (retryable, per ADR-0012), but with no <c>component_results</c> row
	/// recorded (<see cref="FailScanAsync"/> never ran) and a raw "Unhandled
	/// exception: ..." note rather than the documented, redacted execution_error
	/// shape round-9's crashed jobs were supposed to produce. The primary fix is
	/// prevention (the pool no longer races); this backstop makes any residual
	/// exception from that layer honest rather than a raw crash, matching every
	/// other transport-level failure this handler already classifies.
	/// Cancellation is deliberately NOT caught here -- it must keep propagating so
	/// the dispatcher's own cancellation handling (job -&gt; Cancelled) still applies.
	/// </summary>
	private static async Task<PowerShellExecutionResult> ExecuteOrSyntheticFailureAsync(
		Func<Task<PowerShellExecutionResult>> invoke, CancellationToken cancellationToken)
	{
		try
		{
			return await invoke().ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			return new PowerShellExecutionResult(
				Succeeded: false,
				Output: [],
				HadErrors: true,
				TimedOut: false,
				FailureReason: $"PowerShell invocation threw during execution: {exception.Message}",
				NativeExitCode: null);
		}
	}

	/// <summary>
	/// Classifies a raw failure reason (auth-shaped -&gt; the credential halt path,
	/// otherwise ordinary), redacts it for the two sinks it reaches (jobs.note,
	/// job.log), and emits the log-tail event (#274 AC "real failures mapped to failed
	/// with the log tail in job events").
	/// </summary>
	private async Task<JobExecutionOutcome> FailScanAsync(JobExecutionContext context, string rawNote, CancellationToken cancellationToken)
	{
		bool isAuthFailure = AuthFailureClassifier.IsAuthFailure(rawNote, [.. _powerShellOptions.Value.AuthFailureMarkers]);
		string note = _redactor.Redact(rawNote);
		await EmitLogTailAsync(context, note, cancellationToken).ConfigureAwait(false);

		// Issue #745, epic #726 §6: any stage failure for a plan-item-granular job is a
		// component that "cannot execute" -- recorded exactly once as execution_error/
		// Not_Reviewed (never omitted), using the already-redacted note so no raw
		// credential-shaped text ever reaches component_results.detail.
		await _resultRecording.RecordExecutionErrorAsync(context.Job, note, cancellationToken).ConfigureAwait(false);

		return isAuthFailure ? JobExecutionOutcome.AuthFailed(note) : JobExecutionOutcome.Failed(note);
	}

	/// <summary>
	/// Issue #585/#586/#736 (epic #582/#726): the job's immutable per-purpose credential
	/// snapshot (<c>job_credential_bindings</c>, migration 0044) is the preferred source --
	/// the entry for the target kind's execution purpose
	/// (<see cref="CredentialPurposeMatrix.DefaultPurposeByTargetKind"/>: the credential
	/// the InSpec transport actually authenticates with) selects which source to decrypt,
	/// so a later edit to the target's bindings can never change what an in-flight job
	/// executes with. That entry is either a STORED credential (decrypted exactly as
	/// before) or, when <see cref="JobCredentialBinding.IsRunSecret"/> is true (issue
	/// #586), an AD HOC per-target/per-purpose secret decrypted from
	/// <c>run_secrets</c> keyed by <c>RunSecretKey.For(job.TargetId, executionPurpose)</c>
	/// -- least-privilege: this reads exactly the one (target, purpose) row this job's
	/// execution purpose needs, never any sibling purpose/target row on the same run
	/// (issue #586 AC "every decrypt is least-privilege"). A snapshotted-but-unconsumed
	/// purpose (e.g. a vsphere job's <c>vcsa-ssh</c> row, stored or ad hoc -- now snapshotted
	/// only when a selected VCSA component's catalog execution profile actually requires it,
	/// issue #736, rather than opportunistically for every vsphere target) is deliberately
	/// NOT decrypted here: nothing in <c>Invoke-WaypointScan</c>'s <c>inspec -t vmware://</c>
	/// invocation consumes a VCSA SSH credential today (the VCSA component pipeline's own
	/// execution wiring is component-granular job/attempt work, ADR-0024 issues #737+, not
	/// yet built), and decrypting a secret no transport uses would violate least-privilege
	/// and fabricate decrypt-audit rows (ADR-0021's own "do not encode a credential
	/// requirement the underlying transport does not actually use"). This handler therefore
	/// still decrypts exactly one purpose per job -- the target-granular execution purpose --
	/// which remains a strict subset of (never more than) what the snapshot recorded as
	/// consumed for this run.
	///
	/// A job with NO snapshot rows is a legacy row (fanned out before migration 0044)
	/// or a LEGACY flat run-secret job (the pre-#586 one-row-per-run shape, wire-compat):
	/// fall through to the pre-#585 behavior -- stored credential (<c>jobs.credential_id</c>)
	/// when set, otherwise the legacy ad hoc run secret registered for this job's run
	/// (#276/#434, <see cref="RunSecretKey.Legacy"/>). The two tiers never mix: a NULL
	/// <c>credential_id</c> with no run secret row available (never registered, already
	/// deleted on a prior terminal completion, or swept as expired) is an auth-style
	/// failure, never a silent fall-through to a stored credential -- see
	/// <see cref="IRunSecretStore"/>'s "no personal rows, ever" contract.
	/// </summary>
	private async Task<ResolvedCredential> ResolveCredentialAsync(
		JobExecutionContext context, string targetKind, CancellationToken cancellationToken, string? executionPurposeOverride = null)
	{
		IReadOnlyList<JobCredentialBinding> bindings = await _jobs
			.GetJobCredentialBindingsAsync(context.Job.Id, cancellationToken).ConfigureAwait(false);
		if (bindings.Count > 0)
		{
			// Issue #741/#743: an ssh-transport narrowed item's OWN execution purpose
			// (vcsa-ssh for a VCSA service, srg-ssh for a whole-appliance SSH product)
			// overrides the target-kind default -- a VCSA service item's OWNING TARGET is
			// `vsphere`-kind (whose default purpose is vsphere-api, the vCenter API
			// credential), but the ssh invocation this handler is about to make
			// authenticates with the item's OWN required purpose instead. Every other
			// item (no override -- the vmware family and legacy/unnarrowed jobs) keeps
			// the pre-#741 target-kind-keyed lookup exactly as before.
			string? executionPurpose = executionPurposeOverride;
			if (executionPurpose is null && !CredentialPurposeMatrix.DefaultPurposeByTargetKind.TryGetValue(targetKind, out executionPurpose))
			{
				throw new ScanCredentialException(
					$"job '{context.Job.Id}' carries a credential snapshot but target kind '{targetKind}' has no execution purpose in the shared matrix.");
			}

			JobCredentialBinding? executionBinding = bindings
				.FirstOrDefault(b => string.Equals(b.Purpose, executionPurpose, StringComparison.Ordinal));
			if (executionBinding is null)
			{
				throw new ScanCredentialException(
					$"job '{context.Job.Id}' carries a credential snapshot with no entry for its execution purpose '{executionPurpose}'.");
			}

			if (executionBinding.IsRunSecret)
			{
				return await ResolveAdHocPurposeCredentialAsync(context, executionPurpose, cancellationToken).ConfigureAwait(false);
			}

			if (executionBinding.CredentialId is not { } snapshotCredentialId)
			{
				// Only reachable if the credential was deleted while this job was
				// terminal (#593's detach) and the job was somehow re-executed anyway --
				// fail auth-style rather than guessing.
				throw new ScanCredentialException(
					$"job '{context.Job.Id}' snapshot for purpose '{executionPurpose}' no longer names a credential (deleted after this job reached a terminal state).");
			}

			return await ResolveStoredCredentialAsync(context, snapshotCredentialId, cancellationToken).ConfigureAwait(false);
		}

		if (context.Job.CredentialId is not { } credentialId)
		{
			if (context.Job.RunId is not { } runId)
			{
				// A run-scoped secret needs a run to be scoped to -- a NULL credential_id
				// with no run at all is not a shape ad hoc scans ever produce (RunsController
				// only sets HasRunSecret on scan-run fan-out), so this is a defensive guard,
				// not an expected path.
				throw new ScanCredentialException($"job '{context.Job.Id}' has no stored credential and no run to resolve an ad hoc credential against.");
			}

			string runSecretActor = await ResolveActorAsync(context.Job.RunId, cancellationToken).ConfigureAwait(false);
			DecryptedRunSecret? runSecret;
			try
			{
				runSecret = await _runSecrets.DecryptAsync(runId, context.Job.Id, runSecretActor, cancellationToken).ConfigureAwait(false);
			}
			catch (MasterKeyUnavailableException exception)
			{
				throw new ScanCredentialException($"ad hoc run credential could not be decrypted: {exception.Message}");
			}

			if (runSecret is null)
			{
				throw new ScanCredentialException(
					$"job '{context.Job.Id}' has no stored credential and no run secret is available for run '{runId}' (never registered, already deleted on a prior terminal completion, or swept as expired).");
			}

			// The legacy flat ad hoc "my credentials" tier (ADR-0011, #276/#434) carries no
			// sudo flag -- it is a personal, ad hoc secret with no stored typed-credential
			// row to read SudoEnabled from -- so an SRG scan using it always runs without
			// sudo. Sudo (#249's typed credentials field) is only meaningful for a stored
			// credential.
			return new ResolvedCredential(runSecret.Username, runSecret.Secret, SudoEnabled: false, runSecret.Dispose);
		}

		return await ResolveStoredCredentialAsync(context, credentialId, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Issue #586: decrypts a per-target/per-purpose ad hoc credential for
	/// <paramref name="purpose"/> from <c>run_secrets</c>, keyed by
	/// <c>RunSecretKey.For(context.Job.TargetId, purpose)</c> -- the same least-privilege,
	/// per-job-attributed decrypt discipline the legacy flat tier already has (fail-closed
	/// audit in the same transaction as the ciphertext read, sliding expiry, "no personal
	/// rows, ever" -- never a silent fall-through to a stored credential on a miss).
	/// </summary>
	private async Task<ResolvedCredential> ResolveAdHocPurposeCredentialAsync(JobExecutionContext context, string purpose, CancellationToken cancellationToken)
	{
		if (context.Job.RunId is not { } runId || context.Job.TargetId is not { } targetId)
		{
			// An ad hoc-purpose snapshot row is only ever produced by RunCreationService's
			// scan fan-out, which always carries both a run and a target -- defensive guard,
			// not an expected path.
			throw new ScanCredentialException(
				$"job '{context.Job.Id}' carries an ad hoc credential snapshot for purpose '{purpose}' but has no run/target to resolve it against.");
		}

		string runSecretActor = await ResolveActorAsync(runId, cancellationToken).ConfigureAwait(false);
		DecryptedRunSecret? runSecret;
		try
		{
			runSecret = await _runSecrets.DecryptAsync(runId, RunSecretKey.For(targetId, purpose), context.Job.Id, runSecretActor, cancellationToken).ConfigureAwait(false);
		}
		catch (MasterKeyUnavailableException exception)
		{
			throw new ScanCredentialException($"ad hoc credential for target '{targetId}', purpose '{purpose}' could not be decrypted: {exception.Message}");
		}

		if (runSecret is null)
		{
			throw new ScanCredentialException(
				$"job '{context.Job.Id}' carries an ad hoc credential snapshot for target '{targetId}', purpose '{purpose}', but no run secret row is available (never registered, already deleted on a prior terminal completion, or swept as expired).");
		}

		// Same as the legacy flat tier: an ad hoc secret carries no sudo flag.
		return new ResolvedCredential(runSecret.Username, runSecret.Secret, SudoEnabled: false, runSecret.Dispose);
	}

	/// <summary>Decrypt-under-identity for a stored credential (security.md control 4) -- shared by the #585 snapshot path and the legacy <c>jobs.credential_id</c> fallback.</summary>
	private async Task<ResolvedCredential> ResolveStoredCredentialAsync(JobExecutionContext context, Guid credentialId, CancellationToken cancellationToken)
	{
		CredentialResponse? credential = await _credentials.GetAsync(credentialId, cancellationToken).ConfigureAwait(false);
		if (credential is null)
		{
			throw new ScanCredentialException($"job '{context.Job.Id}' references credential '{credentialId}', which no longer exists.");
		}

		if (string.IsNullOrWhiteSpace(credential.Username))
		{
			// Issue #262: no falling back to Name as the SSO login -- same rule
			// DiscoverJobHandler enforces.
			throw new ScanCredentialException(
				$"job '{context.Job.Id}' references credential '{credential.Id}', which has no username set; set one before scanning this target.");
		}

		string actor = await ResolveActorAsync(context.Job.RunId, cancellationToken).ConfigureAwait(false);
		DecryptedSecret decrypted;
		try
		{
			decrypted = await _secrets
				.DecryptAsync(credentialId, actor, context.Job.Id, context.Job.RunId, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (CredentialSecretNotFoundException exception)
		{
			throw new ScanCredentialException($"target credential has no stored secret: {exception.Message}");
		}
		catch (MasterKeyUnavailableException exception)
		{
			throw new ScanCredentialException($"target credential could not be decrypted: {exception.Message}");
		}

		return new ResolvedCredential(credential.Username!, decrypted.Value, credential.SudoEnabled, decrypted.Dispose);
	}

	/// <summary>Attribution for the decrypt audit row (security.md control 4): the run's initiator when recorded, falling back to a fixed system marker -- same pattern as DiscoverJobHandler.ResolveActorAsync.</summary>
	private async Task<string> ResolveActorAsync(Guid? runId, CancellationToken cancellationToken)
	{
		if (runId is null)
		{
			return "system";
		}

		RunQueueState? state = await _jobs.GetRunQueueStateAsync(runId.Value, cancellationToken).ConfigureAwait(false);
		return string.IsNullOrWhiteSpace(state?.InitiatedBy) ? "system" : state!.InitiatedBy!;
	}

	/// <summary>
	/// Log-tail-on-failure (#274 AC "real failures mapped to failed with the log tail in
	/// job events"), mirroring module.scan.ps1's own <c>-SurfaceOutputOnFailure</c>
	/// convention: the redacted failure note is the tail this handler has -- the
	/// PowerShell layer's own captured-output-on-failure already ran through the same
	/// redacting <c>job.log</c> buffer via <see cref="IPowerShellExecutor"/>, so this
	/// emits one more line summarizing the outcome rather than duplicating that stream.
	/// </summary>
	private static async Task EmitLogTailAsync(JobExecutionContext context, string note, CancellationToken cancellationToken)
	{
		string[] lines = note.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		string tail = string.Join('\n', lines.TakeLast(LogTailLines));
		string payload = JsonSerializer.Serialize(new { severity = "Error", line = tail });
		await context.Events.EmitAsync(JobEventTypes.JobLog, context.Job.Id, context.Job.RunId, payload, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// The expired-attestation WARN (#275 AC, docs/domain-model.md: "the run logs a
	/// WARN, and Results lists expired attestations explicitly") -- same job.log
	/// event/severity shape as <see cref="EmitLogTailAsync"/>'s Error line, at Warning
	/// severity instead, so #27's Results sidebar can distinguish the two.
	/// </summary>
	private static async Task EmitWarnAsync(JobExecutionContext context, string line, CancellationToken cancellationToken)
	{
		string payload = JsonSerializer.Serialize(new { severity = "Warning", line });
		await context.Events.EmitAsync(JobEventTypes.JobLog, context.Job.Id, context.Job.RunId, payload, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Issue #744 AC "rule-level correction coverage is measured; unresolved/ambiguous
	/// rules are visible and cannot masquerade as complete": one structured job.log
	/// event per convert stage that attempted correction, naming the exact matched
	/// count and every unmatched rule id -- never summarized away. A coverage-parse
	/// failure (null <paramref name="coverage"/> -- an unexpected PowerShell output
	/// shape) or a correction-attempt exception (<see cref="RuleCoverageResult.Error"/>
	/// set) is reported as a Warning, same severity as an unmatched rule, since both
	/// mean "correction did not fully account for this CKL's rules" -- the raw CKL is
	/// never destroyed or blocked by either case (issue #744 AC "preserve raw HDF/CKL
	/// when metadata correction ... fails").
	/// </summary>
	private static async Task EmitRuleCoverageAsync(JobExecutionContext context, RuleCoverageResult? coverage, CancellationToken cancellationToken)
	{
		if (coverage is null)
		{
			await EmitWarnAsync(
				context,
				$"job '{context.Job.Id}' rule-identity correction produced no coverage result (unexpected convert output shape); CKL rule identifiers were not corrected.",
				cancellationToken).ConfigureAwait(false);
			return;
		}

		if (coverage.Error is not null)
		{
			await EmitWarnAsync(
				context,
				$"job '{context.Job.Id}' rule-identity correction failed: {coverage.Error}; raw CKL preserved uncorrected.",
				cancellationToken).ConfigureAwait(false);
			return;
		}

		string severity = coverage.Unmatched.Count > 0 ? "Warning" : "Info";
		string line = coverage.Unmatched.Count > 0
			? $"job '{context.Job.Id}' rule-identity correction: {coverage.Matched} matched, {coverage.Unmatched.Count} unmatched rule(s) [{string.Join(", ", coverage.Unmatched)}] -- left uncorrected, never dropped."
			: $"job '{context.Job.Id}' rule-identity correction: {coverage.Matched} matched, full coverage.";
		string payload = JsonSerializer.Serialize(new
		{
			severity,
			line,
			matched = coverage.Matched,
			unmatched_count = coverage.Unmatched.Count,
			unmatched_rule_ids = coverage.Unmatched,
		});
		await context.Events.EmitAsync(JobEventTypes.JobLog, context.Job.Id, context.Job.RunId, payload, cancellationToken).ConfigureAwait(false);
	}

	private static string? TryGetConnectionHost(string connectionJson)
	{
		using JsonDocument document = JsonDocument.Parse(connectionJson);
		return document.RootElement.TryGetProperty("host", out JsonElement hostElement) && hostElement.ValueKind == JsonValueKind.String
			? hostElement.GetString()
			: null;
	}

	/// <summary>
	/// Issue #639: resolves the InSpec profile directory a scan actually runs against.
	/// <paramref name="profileKey"/> non-null (the normal path, set by
	/// <see cref="Waypoint.Infrastructure.Runs.RunCreationService.CreateScanRunAsync"/>
	/// after validating the profile is installed) resolves to
	/// <c>{ComplianceContentOptions.ContentPath}/{profile_key}</c> -- the SAME working
	/// tree <c>content-pull</c> clones into (ADR-0017), read-only from this handler's
	/// perspective: nothing on the InSpec/scan execution path ever writes into
	/// <see cref="ComplianceContentOptions.ContentPath"/>, only <c>content-pull</c>/
	/// <c>content-import</c> execution does, so co-locating both job types' mount in
	/// one compliance-runner-owned volume (necessarily read-write at the container
	/// level, since both job types run in this same process) does not weaken that
	/// boundary -- it is enforced here, not by the mount's ro/rw bit.
	///
	/// <paramref name="profileKey"/> null falls back to the legacy fixed
	/// <see cref="ScanOptions.ProfilePath"/>/<see cref="ScanOptions.NsxProfilePath"/>/
	/// <see cref="ScanOptions.SrgProfilePath"/> -- a transitional path for a job row
	/// fanned out before this change (or a future non-portal caller with no profile
	/// selection), not a second long-term way to scan. Every fan-out through
	/// <c>RunCreationService.CreateScanRunAsync</c> going forward always supplies
	/// <see cref="ScanPayload.ProfileKey"/>, so this fallback should see decreasing use.
	/// </summary>
	private string ResolveProfilePath(string? profileKey, string legacyFallbackPath) =>
		profileKey is null ? legacyFallbackPath : Path.Combine(_complianceContentOptions.Value.ContentPath, profileKey);

	/// <summary>
	/// Issue #921: when the InSpec-invoking wrapper module (Invoke-WaypointScan /
	/// Invoke-WaypointSrgScan) reports only the downstream "report file not found"
	/// symptom -- which is what a nonzero, transport-caused InSpec exit with no
	/// report on disk always produces, since the module's own Test-Path check can't
	/// tell WHY the report is missing -- prefer the first PowerShell error-stream
	/// line captured during the SAME invocation. That line is the actual transport
	/// diagnostic (e.g. "Unable to connect to VIServer ... Name or service not
	/// known"), already surfaced by Invoke-ExternalCommand's -SurfaceOutputOnFailure
	/// at Error severity and captured verbatim by PowerShellExecutor's error-stream
	/// handler -- it just previously reached only job.log, one severity below the
	/// terminal note, rather than the note itself. Any other module-reported reason
	/// (auth rejection, argument error, etc.) is specific enough on its own and is
	/// returned unchanged; the underlying error line is a fallback preference, not
	/// an unconditional override, so an unrelated captured error stream line never
	/// masks an already-actionable module message.
	/// </summary>
	private static string SelectFailureNote(string? moduleFailureReason, IReadOnlyList<string>? errorLines)
	{
		string fallback = moduleFailureReason ?? "scan invocation reported failure with no reason.";
		if (errorLines is not { Count: > 0 })
		{
			return fallback;
		}

		bool isMissingReportSymptom = moduleFailureReason is not null
			&& moduleFailureReason.Contains("report file not found", StringComparison.OrdinalIgnoreCase);
		if (!isMissingReportSymptom)
		{
			return fallback;
		}

		return errorLines[0];
	}

	/// <summary>Parses Invoke-WaypointScan's returned [pscustomobject] the same way DownloadJobHandler.TryParseOutput reads its own module's result -- the executor's own Succeeded only reflects the transport, not what the function body itself reported.</summary>
	private static ScanInvocationOutput? TryParseOutput(IReadOnlyList<object?> output)
	{
		object? first = output.Count > 0 ? output[0] : null;
		if (first is not System.Management.Automation.PSObject psObject)
		{
			return null;
		}

		// Issue #976: routed through the PowerShellValueUnwrap chokepoint for uniformity
		// -- top-level pipeline-output properties, already unwrapped by
		// PowerShellExecutor.Unwrap, so never actually exposed to #972's nested-property
		// hazard, but Unwrap/UnwrapAs are idempotent on an already-unwrapped value.
		bool success = PowerShellValueUnwrap.Unwrap(psObject.Properties["Success"]?.Value) is true;
		string? reportPath = PowerShellValueUnwrap.UnwrapAs<string>(psObject.Properties["ReportPath"]?.Value);
		string? failureReason = PowerShellValueUnwrap.UnwrapAs<string>(psObject.Properties["FailureReason"]?.Value);
		return new ScanInvocationOutput(success, reportPath, failureReason);
	}

	/// <summary>Parses Invoke-WaypointAttest's returned [pscustomobject], same rationale as <see cref="TryParseOutput"/>.</summary>
	private static AttestInvocationOutput? TryParseAttestOutput(IReadOnlyList<object?> output)
	{
		object? first = output.Count > 0 ? output[0] : null;
		if (first is not System.Management.Automation.PSObject psObject)
		{
			return null;
		}

		bool success = PowerShellValueUnwrap.Unwrap(psObject.Properties["Success"]?.Value) is true;
		bool attestApplied = PowerShellValueUnwrap.Unwrap(psObject.Properties["AttestApplied"]?.Value) is true;
		string? failureReason = PowerShellValueUnwrap.UnwrapAs<string>(psObject.Properties["FailureReason"]?.Value);
		return new AttestInvocationOutput(success, attestApplied, failureReason);
	}

	/// <summary>Parses Invoke-WaypointConvert's returned [pscustomobject], same rationale as <see cref="TryParseOutput"/>.</summary>
	private static ConvertInvocationOutput? TryParseConvertOutput(IReadOnlyList<object?> output)
	{
		object? first = output.Count > 0 ? output[0] : null;
		if (first is not System.Management.Automation.PSObject psObject)
		{
			return null;
		}

		bool success = PowerShellValueUnwrap.Unwrap(psObject.Properties["Success"]?.Value) is true;
		string? cklPath = PowerShellValueUnwrap.UnwrapAs<string>(psObject.Properties["CklPath"]?.Value);
		bool metadataApplied = PowerShellValueUnwrap.Unwrap(psObject.Properties["MetadataApplied"]?.Value) is true;
		string? failureReason = PowerShellValueUnwrap.UnwrapAs<string>(psObject.Properties["FailureReason"]?.Value);
		RuleCoverageResult? ruleCoverage = TryParseRuleCoverage(PowerShellValueUnwrap.Unwrap(psObject.Properties["RuleCoverage"]?.Value));
		return new ConvertInvocationOutput(success, cklPath, metadataApplied, failureReason, ruleCoverage);
	}

	/// <summary>
	/// Issue #744: parses <c>Set-WaypointCklRuleIdentity</c>'s coverage summary
	/// (Matched/Unmatched/Error) off the convert invocation's nested RuleCoverage
	/// property. Returns null when no correction was attempted (legacy/unmapped job)
	/// or the property is an unexpected shape -- never throws, matching this handler's
	/// existing "a malformed PowerShell output property degrades rather than crashes"
	/// discipline (see TryParseConvertOutput's own sibling properties).
	/// </summary>
	private static RuleCoverageResult? TryParseRuleCoverage(object? value)
	{
		// Issue #976: RuleCoverage is a NESTED property (one level inside the top-level
		// Convert output), so it must go through the chokepoint's Unwrap before the
		// PSObject pattern-match, not directly against the raw property value -- unlike
		// this handler's other TryParse* helpers above, which only read top-level
		// properties already unwrapped by PowerShellExecutor.Unwrap.
		if (PowerShellValueUnwrap.Unwrap(value) is not System.Management.Automation.PSObject coverageObject)
		{
			return null;
		}

		int matched = PowerShellValueUnwrap.Unwrap(coverageObject.Properties["Matched"]?.Value) switch
		{
			int intValue => intValue,
			long longValue => (int)longValue,
			_ => 0,
		};
		List<string> unmatched = [];
		foreach (object? item in PowerShellValueUnwrap.UnwrapEach(coverageObject.Properties["Unmatched"]?.Value))
		{
			if (item is string ruleId)
			{
				unmatched.Add(ruleId);
			}
		}

		string? error = PowerShellValueUnwrap.UnwrapAs<string>(coverageObject.Properties["Error"]?.Value);
		return new RuleCoverageResult(matched, unmatched, error);
	}

	private static ScanPayload ParsePayload(string payloadJson)
	{
		using JsonDocument document = JsonDocument.Parse(payloadJson);
		JsonElement root = document.RootElement;
		if (!root.TryGetProperty("target_id", out JsonElement targetIdElement) || !Guid.TryParse(targetIdElement.GetString(), out Guid targetId))
		{
			throw new ArgumentException("payload requires a GUID string 'target_id' property");
		}

		// profile_key (issue #639) is optional on the wire only for backward
		// compatibility with a job row fanned out before this change (or a future
		// caller that legitimately has no profile selection yet, e.g. a not-yet-ported
		// scheduled scan) -- RunCreationService.CreateScanRunAsync always sets it going
		// forward, having already validated the profile exists at run-creation time.
		string? profileKey = root.TryGetProperty("profile_key", out JsonElement profileKeyElement) && profileKeyElement.ValueKind == JsonValueKind.String
			? profileKeyElement.GetString()
			: null;

		// Issue #737 item-4: component-narrowing fields, written by
		// RunCreationService.BuildPlanItemJobSpec for a NARROWABLE plan item
		// (ScanComponentNarrowing.CanNarrow == true -- the vSphere-family object
		// selectors). All three are absent on a legacy whole-target job and on the ONE
		// collapsed whole-target remainder job (which carries unnarrowed = true instead)
		// -- ResolveNarrowing below (and CanNarrow) treats those as "scan the whole
		// target", so a null/absent field never narrows.
		string? transport = ReadOptionalString(root, "transport");
		string? selectorKind = ReadOptionalString(root, "selector_kind");
		string? selectorName = ReadOptionalString(root, "selector_name");
		bool unnarrowed = root.TryGetProperty("unnarrowed", out JsonElement unnarrowedElement)
			&& unnarrowedElement.ValueKind == JsonValueKind.True;

		// Issue #738, generalized to esxi/vm by #739/#740: present on any narrowable
		// vSphere-family (vcenter/esxi/vm) selector plan-item job
		// (RunCreationService.BuildPlanItemJobSpec's isVSphereComponentProfile branch).
		// Absent on every service/target/legacy/unnarrowed job -- those keep resolving
		// through ResolveProfilePath's run-level profile_key/legacy-fixed-path fallback
		// exactly as before this issue.
		Guid? catalogExecutionProfileId = root.TryGetProperty("catalog_execution_profile_id", out JsonElement profileIdElement)
			&& profileIdElement.ValueKind == JsonValueKind.String
			&& Guid.TryParse(profileIdElement.GetString(), out Guid parsedProfileId)
				? parsedProfileId
				: null;
		Guid? baselineId = root.TryGetProperty("baseline_id", out JsonElement baselineIdElement)
			&& baselineIdElement.ValueKind == JsonValueKind.String
			&& Guid.TryParse(baselineIdElement.GetString(), out Guid parsedBaselineId)
				? parsedBaselineId
				: null;
		Guid? componentId = root.TryGetProperty("component_id", out JsonElement componentIdElement)
			&& componentIdElement.ValueKind == JsonValueKind.String
			&& Guid.TryParse(componentIdElement.GetString(), out Guid parsedComponentId)
				? parsedComponentId
				: null;

		// Issue #741/#743: the item's frozen catalog output kind (hdf | hdf_ckl,
		// CatalogOutputKinds) -- present on any narrowable plan-item job
		// (RunCreationService.BuildPlanItemJobSpec). This is the CATALOG-KIND signal
		// JobShapes.ForJob already reads to pick Standard vs Srg BEFORE this handler ever
		// runs; ExecuteAttestStageAsync reads the SAME field here so its own
		// converting-vs-done branch can never disagree with the job's actual state
		// machine. Absent on a legacy/unnarrowed job -- those keep falling back to the
		// pre-#741 target-kind inference.
		string? outputKind = ReadOptionalString(root, "output_kind");

		// Issue #741/#743: the item's frozen benchmark-revision identity (null for an
		// SRG execution profile -- no XCCDF concept, ADR-0022). The convert stage reads
		// this to stamp the exact catalog-mapped benchmark (#828/#834) rather than a
		// target-kind-keyed static stamp.
		Guid? benchmarkRevisionId = root.TryGetProperty("benchmark_revision_id", out JsonElement benchmarkRevisionIdElement)
			&& benchmarkRevisionIdElement.ValueKind == JsonValueKind.String
			&& Guid.TryParse(benchmarkRevisionIdElement.GetString(), out Guid parsedBenchmarkRevisionId)
				? parsedBenchmarkRevisionId
				: null;

		// Issue #743: the item's frozen catalog sudo policy (migration 0074) -- present
		// (non-null) only on an ssh-transport plan-item job (ScanPlannerService freezes
		// it for ssh only; RunCreationService serializes null for every other
		// transport). Null on any legacy/unnarrowed payload -- the SRG invocation then
		// keeps the pre-#743 credential-driven sudo behavior byte-identically.
		bool? requiresSudo = ReadOptionalBool(root, "requires_sudo");
		bool? sudoRequiresPassword = ReadOptionalBool(root, "sudo_requires_password");

		List<ScanPayloadInputResolution> inputResolutions = [];
		if (root.TryGetProperty("input_resolutions", out JsonElement inputResolutionsElement) && inputResolutionsElement.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement entry in inputResolutionsElement.EnumerateArray())
			{
				string? state = ReadOptionalString(entry, "State") ?? ConfigResolutionStates.Missing;
				Guid? docId = entry.TryGetProperty("DocId", out JsonElement docIdElement)
					&& docIdElement.ValueKind == JsonValueKind.String
					&& Guid.TryParse(docIdElement.GetString(), out Guid parsedDocId)
						? parsedDocId
						: null;
				int? docVersion = entry.TryGetProperty("DocVersion", out JsonElement docVersionElement) && docVersionElement.ValueKind == JsonValueKind.Number
					? docVersionElement.GetInt32()
					: null;
				string? inputName = ReadOptionalString(entry, "InputName");
				if (!string.IsNullOrWhiteSpace(inputName))
				{
					inputResolutions.Add(new ScanPayloadInputResolution(inputName, state, docId, docVersion));
				}
			}
		}

		return new ScanPayload(
			targetId,
			string.IsNullOrWhiteSpace(profileKey) ? null : profileKey,
			transport,
			selectorKind,
			selectorName,
			unnarrowed,
			catalogExecutionProfileId,
			baselineId,
			inputResolutions,
			componentId,
			outputKind,
			benchmarkRevisionId,
			requiresSudo,
			sudoRequiresPassword);
	}

	private static string? ReadOptionalString(JsonElement root, string property) =>
		root.TryGetProperty(property, out JsonElement element) && element.ValueKind == JsonValueKind.String
			? element.GetString()
			: null;

	private static bool? ReadOptionalBool(JsonElement root, string property) =>
		root.TryGetProperty(property, out JsonElement element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False
			? element.GetBoolean()
			: null;

	private sealed record ScanPayload(
		Guid TargetId,
		string? ProfileKey,
		string? Transport = null,
		string? SelectorKind = null,
		string? SelectorName = null,
		bool Unnarrowed = false,
		Guid? CatalogExecutionProfileId = null,
		Guid? BaselineId = null,
		IReadOnlyList<ScanPayloadInputResolution>? InputResolutions = null,
		Guid? ComponentId = null,
		string? OutputKind = null,
		Guid? BenchmarkRevisionId = null,
		bool? RequiresSudo = null,
		bool? SudoRequiresPassword = null)
	{
		public IReadOnlyList<ScanPayloadInputResolution> InputResolutionsOrEmpty => InputResolutions ?? [];
	}

	/// <summary>
	/// Issue #738: the wire shape of one <see cref="Waypoint.Core.ConfigDocs.PlanInputResolution"/>
	/// entry as it rides the job payload. <c>RunCreationService.BuildPlanItemJobSpec</c>
	/// serializes <c>item.InputResolutionsOrEmpty</c> with the default
	/// <see cref="JsonSerializer"/> options (no camelCase naming policy applied anywhere
	/// else in this payload either), so the record's PascalCase property names ride the
	/// wire verbatim -- <c>ParsePayload</c>'s reader below matches that exactly.
	/// </summary>
	private sealed record ScanPayloadInputResolution(string InputName, string State, Guid? DocId, int? DocVersion);

	private sealed record ResolvedCredential(string Username, string Secret, bool SudoEnabled, Action Release);

	private sealed record ScanInvocationOutput(bool Success, string? ReportPath, string? FailureReason);

	private sealed record AttestInvocationOutput(bool Success, bool AttestApplied, string? FailureReason);

	private sealed record ConvertInvocationOutput(
		bool Success, string? CklPath, bool MetadataApplied, string? FailureReason, RuleCoverageResult? RuleCoverage = null);

	/// <summary>Issue #744: rule-level correction coverage for one convert invocation -- <see cref="Unmatched"/> is exact (never truncated) so every unresolved rule id remains visible.</summary>
	private sealed record RuleCoverageResult(int Matched, List<string> Unmatched, string? Error);

	/// <summary>Internal-only signal from <see cref="ResolveCredentialAsync"/> to its caller; never crosses a handler boundary as a thrown exception.</summary>
	private sealed class ScanCredentialException(string message) : Exception(message);
}
