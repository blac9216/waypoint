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

using Waypoint.Core.ComplianceContent;
using Waypoint.Core.Components;
using Waypoint.Core.Scans;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.Sites;

namespace Waypoint.Infrastructure.Runs;

/// <summary>
/// Compiles a <see cref="ResolvedTargetScope"/>'s resolved (already catalog-compatible,
/// non-retired, non-absent, non-conflicted) component set into an immutable
/// <see cref="ScanPlan"/> (issue #734, epic #726 Wave 2, ADR-0023 "Immutable plans",
/// ADR-0024). This is the join-and-validate step between
/// <see cref="ScopeResolutionService"/>'s WHICH-components answer and the WHAT-would-
/// be-done-to-each-one freeze migration 0057 persists -- it performs no persistence
/// itself (<see cref="Waypoint.Core.Scans.IScanPlanRepository"/> is the caller's job,
/// same division of labor <see cref="ScopeResolutionService"/>/<see cref="Waypoint.Core.Jobs.IRunScopeSnapshotRepository"/>
/// already established for scope).
///
/// <b>Skip-vs-fail reconciliation (issue #734's own required reading):</b> ADR-0023 §3
/// states plainly that "Missing facts/baselines skip only the affected component" and
/// that an explicit scope containing only unsupported components "produces an honest
/// plan with no executable items rather than widening" -- i.e. a per-component gap is
/// never a reason to reject the whole plan by itself. ADR-0024 independently confirms
/// this for credentials/inputs: "A missing, incompatible, or ambiguous credential
/// affects only components requiring that purpose... The run is incomplete, not
/// rejected wholesale." Issue #734's own AC-1 ("No run/job rows are created when any
/// required execution item is invalid") reads, on its face, like whole-plan rejection
/// on any single gap -- but every ADR-0023/0024 passage governing this exact situation
/// says the opposite for a PER-COMPONENT gap. The reconciliation this planner applies:
/// AC-1's "invalid" means the REQUEST/PLAN itself is unrunnable (unknown schema
/// version, a resolved scope that already failed upstream, or -- the case this method
/// actually enforces -- EVERY candidate component fails to plan, leaving zero
/// executable items, which mirrors <see cref="ResolvedTargetScope.HasAnyResolvedComponent"/>'s
/// already-shipped "zero survives, whole request rejected" gate one layer up). A gap
/// affecting only SOME candidates is recorded as a <see cref="ScanPlanSkip"/> and its
/// siblings still plan normally, exactly like a <see cref="ScopeOmission"/> one layer
/// up. This planner therefore never returns a plan with some items accepted and others
/// silently dropped with no trace -- every candidate is either an accepted
/// <see cref="ScanPlanItem"/> or an explicit, reasoned <see cref="ScanPlanSkip"/>, and
/// the two lists partition the candidate set completely (issue #734 AC "enumerate all
/// gaps", not just the first).
///
/// <b>Skip vs. integrity-failure (the second, distinct axis):</b> the skip path above is
/// only for the ARCHITECTURALLY SKIPPABLE states epic #726 §3/§5 enumerate -- an
/// operator-fixable gap in an otherwise-consistent catalog (unsupported capability, no
/// active baseline yet; #736/#753 add missing input/credential). An unmapped STIG
/// benchmark is deliberately NOT in this set (issue #1021 retired it as a skip reason):
/// per the owner correction on #730 and the decided lifecycle in #1002, execution
/// requires only the approved profile baseline -- the XCCDF is optional CKL-metadata
/// enrichment, never an execution gate -- so a STIG execution profile's active baseline
/// with no mapped benchmark revision plans normally with <see cref="ScanPlanItem.IsBenchmarkMissing"/>
/// set, surfacing the gap as a non-blocking annotation instead of a dead end.
/// A PLANNER-INTEGRITY failure -- corrupt/inconsistent catalog state the planner cannot
/// validate, e.g. an SRG execution profile whose active baseline unexpectedly carries a
/// benchmark revision -- is NOT a skip. Epic §3/§5 never sanction silently dropping a
/// component on a data-integrity violation while its siblings proceed; that would narrow
/// a run's coverage on corruption instead of surfacing it. Such a state throws
/// <see cref="ScanPlanIntegrityException"/> from <see cref="CompileOneAsync"/>, which
/// propagates out of <see cref="CompileAsync"/> and fails the WHOLE plan compilation
/// closed -- because the caller compiles the plan as a pre-creation validation step
/// (<see cref="Waypoint.Infrastructure.Runs.RunCreationService"/>), no run/plan/job row
/// is ever persisted, and the API surfaces a distinct <c>plan_integrity_failure</c>
/// diagnostic rather than a silent skip row.
///
/// Deterministic: for the same persisted catalog/baseline/component state, planning the
/// same resolved scope twice always yields the same accepted-item set and therefore the
/// same <see cref="ScanPlanDigest"/> (issue #734 AC-4) -- no wall-clock or random
/// ordering anywhere in this class; every list is sorted before being handed to the
/// digest function.
/// </summary>
public sealed class ScanPlannerService
{
	private readonly IComponentRepository _components;
	private readonly ICatalogRepository _catalog;
	private readonly IBaselineRepository _baselines;
	private readonly TargetRepository _targets;
	private readonly PlanConfigResolutionService _configResolution;

	public ScanPlannerService(
		IComponentRepository components,
		ICatalogRepository catalog,
		IBaselineRepository baselines,
		TargetRepository targets,
		PlanConfigResolutionService configResolution)
	{
		ArgumentNullException.ThrowIfNull(components);
		ArgumentNullException.ThrowIfNull(catalog);
		ArgumentNullException.ThrowIfNull(baselines);
		ArgumentNullException.ThrowIfNull(targets);
		ArgumentNullException.ThrowIfNull(configResolution);
		_components = components;
		_catalog = catalog;
		_baselines = baselines;
		_targets = targets;
		_configResolution = configResolution;
	}

	/// <summary>
	/// Compiles a plan for exactly <paramref name="resolvedComponentIds"/> (the already-
	/// eligible set <see cref="ScopeResolutionService.ResolveAsync"/> produced --
	/// re-validating eligibility, e.g. lifecycle/fact-conflict, is that service's job,
	/// not this one's). <paramref name="runId"/> is null for a preview call.
	/// </summary>
	public async Task<ScanPlan> CompileAsync(
		Guid? runId, IReadOnlyList<Guid> resolvedComponentIds, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(resolvedComponentIds);

		List<ScanPlanItem> items = [];
		List<ScanPlanSkip> skips = [];

		foreach (Guid componentId in resolvedComponentIds.Distinct().OrderBy(id => id))
		{
			(ScanPlanItem? item, ScanPlanSkip? skip) = await CompileOneAsync(componentId, cancellationToken).ConfigureAwait(false);
			if (item is not null)
			{
				items.Add(item);
			}
			else
			{
				skips.Add(skip!);
			}
		}

		string digest = ScanPlanDigest.Compute(ScanPlanSchema.CurrentVersion, resolvedComponentIds, items);
		string explanation = BuildExplanation(resolvedComponentIds.Count, items.Count, skips);

		return new ScanPlan(runId, ScanPlanSchema.CurrentVersion, items, skips, digest, explanation);
	}

	/// <summary>
	/// Plans one already-eligible component. An operator-fixable per-component gap
	/// (unsupported / no-active-baseline -- the architecturally skippable states epic
	/// #726 §3/§5 sanction) is an explicit <see cref="ScanPlanSkip"/>, never an
	/// exception, so its siblings still plan. An unmapped STIG benchmark is NOT one of
	/// these gaps (issue #1021/#1002): it plans as an accepted item with
	/// <see cref="ScanPlanItem.IsBenchmarkMissing"/> set. This method throws only for a
	/// caller programming error (null argument) or a data-integrity violation the planner
	/// cannot validate (<see cref="ScanPlanIntegrityException"/>, e.g. an SRG profile
	/// whose active baseline carries a benchmark revision) -- corrupt catalog state that
	/// must fail the whole plan closed rather than silently narrow coverage via a skip row.
	/// </summary>
	private async Task<(ScanPlanItem? Item, ScanPlanSkip? Skip)> CompileOneAsync(Guid componentId, CancellationToken cancellationToken)
	{
		Component? component = await _components.GetAsync(componentId, cancellationToken).ConfigureAwait(false);
		if (component is null || component.CatalogComponentId is not { } catalogComponentId)
		{
			return (null, new ScanPlanSkip(componentId, ScanPlanSkipReasons.Unsupported,
				$"Component '{componentId}' is not linked to a catalog component; no execution profile can be resolved."));
		}

		IReadOnlyList<CatalogExecutionProfileDetail> profiles =
			await _catalog.ListExecutionProfilesByComponentAsync(catalogComponentId, cancellationToken).ConfigureAwait(false);
		if (profiles.Count == 0)
		{
			return (null, new ScanPlanSkip(componentId, ScanPlanSkipReasons.Unsupported,
				$"Component '{componentId}' has no catalog execution profile; content may not yet be staged or activated."));
		}

		// A component's resolved exact version already matched exactly one product
		// version's execution profile set at scope-resolution time
		// (ComponentCapabilityMatcher); every profile ListExecutionProfilesByComponentAsync
		// returns for this catalog component id is therefore already a compatible
		// candidate. Multiple rows mean multiple content releases target the same
		// component (issue #728 AC "multi-release components") -- deterministically pick
		// the one with an active baseline; if more than one has an active baseline
		// (should not happen given one-active-per-execution-profile, but each profile is
		// a distinct id so it is possible for two DIFFERENT profiles of the same
		// component to each have their own active baseline), the docs/compliance-
		// parity.md "Priority" row (NSX STIG 1 ... every SRG 6, i.e. every STIG report
		// group sorts strictly below every SRG group) breaks the tie first -- issue
		// #1012 (round-8 report): the prior lowest-execution-profile-id-only tiebreak
		// let an imported, credential-less SRG profile beat a seeded, credential-
		// bearing STIG profile for the exact same component purely on insertion order,
		// which is not a documented preference at all. Report-group priority IS
		// documented catalog data (CatalogReportGroup.Priority, migration 0050), so
		// preferring the lower (higher-precedence) priority is a doc-grounded rule, not
		// an invented one. Execution profile id remains the final tiebreaker beneath
		// priority so the method stays fully deterministic. NOTE (flagged for
		// reviewer/owner): docs/compliance-parity.md's Priority row documents STIG-
		// before-SRG ordering for REPORT SEQUENCING across different components, not
		// explicitly for choosing between two co-existing profiles of the SAME
		// component -- no ADR states that preference directly. This PR treats "prefer
		// the lower documented priority" as the most defensible reading (STIG is
		// always the more complete, credential-bearing, upload-eligible content for a
		// component that has both), but if the owner intends a different rule (e.g.
		// seed-provenance always wins over imported, regardless of priority), that
		// should be recorded as its own decision and this ordering revisited.
		foreach (CatalogExecutionProfileDetail profile in profiles
			.OrderBy(p => p.ReportGroup.Priority)
			.ThenBy(p => p.ExecutionProfile.Id))
		{
			Baseline? active = await _baselines.GetActiveBaselineAsync(profile.ExecutionProfile.Id, cancellationToken).ConfigureAwait(false);
			if (active is null)
			{
				continue;
			}

			// Owner correction on #730 (2026-08-28) and the decided lifecycle in #1002:
			// "execution requires the approved PROFILE baseline; the XCCDF is optional
			// enrichment -- a CKL is produced from the profile alone, and the
			// XCCDF/STIG-Manager connection enriches CKL metadata for uploadability." A
			// STIG execution profile's active baseline with no mapped benchmark revision
			// is therefore RUNNABLE, not a planning dead-end (issue #1021: the prior
			// `unmapped_benchmark` skip here made the component permanently unplannable
			// the instant an Admin activated STIG content ahead of #730's XCCDF ledger,
			// even with a perfectly runnable SRG baseline coexisting -- there was no
			// fallback and no recovery). This item plans exactly like a mapped STIG item
			// (BenchmarkRevisionId stays null, matching an SRG item's shape); the item's
			// own computed ScanPlanItem.IsBenchmarkMissing flags the gap so callers can
			// surface #1002's standing "benchmark missing -- add and re-approve with
			// matching data" alert without treating it as blocking. ExecuteConvertStageAsync
			// (ScanJobHandler, issue #744) already tolerates a null BenchmarkRevisionId on
			// the job payload -- rule-identity correction is skipped and the CKL falls back
			// to ScanOptions.BenchmarkMetadata, exactly the "profile-only CKL, XCCDF
			// enriches metadata" contract the owner described; there is no CKL-generation
			// change needed here.
			bool isStig = profile.BenchmarkReference is not null;

			if (!isStig && active.BenchmarkRevisionId is not null)
			{
				// Data-integrity violation, NOT an operator-fixable skip: an SRG execution
				// profile has no XCCDF benchmark concept (ADR-0022), so an active baseline
				// carrying a benchmark revision means the catalog is internally
				// inconsistent (corrupt/inconsistent state, a should-never-happen). Epic
				// #726 §3/§5 sanction skip-and-continue only for the enumerated
				// architecturally-skippable gaps (unsupported / no-active-baseline /
				// unmapped-benchmark); silently dropping this component while its siblings
				// proceed would narrow the run's coverage on corruption rather than
				// surface it. Fail the WHOLE plan compilation closed instead -- this throws
				// before RunCreationService persists any run/plan/job row.
				throw new ScanPlanIntegrityException(
					componentId,
					$"Component '{componentId}' resolves to an SRG execution profile ('{profile.ExecutionProfile.Id}') whose active baseline "
						+ $"('{active.Id}') unexpectedly carries a benchmark revision ('{active.BenchmarkRevisionId}'). "
						+ "An SRG profile has no benchmark concept; this indicates inconsistent catalog/baseline state. "
						+ "No run was created. Investigate the baseline's content-import provenance before retrying.");
			}

			List<string> purposes = [.. profile.CredentialRequirements
				.Where(r => r.IsRequired)
				.Select(r => r.Purpose)
				.OrderBy(p => p, StringComparer.Ordinal)];

			// Issue #1012 defense-in-depth: every transport in docs/compliance-parity.md's
			// closed vocabulary that a runner path can actually DISPATCH today (vmware,
			// ssh, nsx-api) implies at least one required credential purpose per the
			// provenance matrix's Purpose column -- there is no documented credential-free
			// dispatchable transport. A resolved profile whose requirement set is
			// nonetheless empty is exactly the silent-dispatch hole the round-8 report
			// found (an importer-promoted profile with zero catalog_credential_requirements
			// rows): with an empty RequiredPurposes, RunCreationService's credential
			// resolution has nothing to check, finds no gap, and the job fans out with no
			// credential and no preview-time warning. Surface it here, at plan-compile
			// time, as an explicit skip instead -- this is a safety net beneath
			// CredentialRequirementDerivation (which now makes this state unreachable for
			// every documented shape at promotion time), not the primary fix.
			//
			// vcf-api is deliberately EXCLUDED from this guard: ADR-0024/CredentialPurposes
			// (Waypoint.Core.Secrets.CredentialPurposes.VcfApi's own doc comment) records
			// vcf-api as catalog-declared but not yet resolvable -- no CredentialTypes
			// value satisfies it in CredentialPurposeMatrix.SatisfyingCredentialTypes, and
			// ScanComponentNarrowing gates every vcf-api component into an unnarrowed
			// placeholder job no runner path executes yet (#807/#977 residual, tracked
			// separately). Guarding it here would treat an already-tracked, already-inert
			// architectural gap as this issue's silent-dispatch hole, which it structurally
			// cannot be (nothing dispatches a vcf-api scan with a live credential need
			// today) -- CredentialRequirementDerivation still WRITES the vcf-api
			// requirement at promotion time (doc-authority correctness for when a runner
			// path lands), this planner guard simply does not additionally gate on it
			// being empty in the meantime.
			bool transportCanDispatchToday = !string.Equals(profile.Component.Transport, CatalogTransports.VcfApi, StringComparison.Ordinal);
			if (purposes.Count == 0 && transportCanDispatchToday)
			{
				return (null, new ScanPlanSkip(componentId, ScanPlanSkipReasons.CredentialedTransportWithNoRequirement,
					$"Component '{componentId}' resolves to execution profile '{profile.ExecutionProfile.Id}' (transport '{profile.Component.Transport}'), " +
						"which requires a credential per the closed transport vocabulary but carries zero catalog_credential_requirements rows. " +
						"This indicates incomplete catalog content; no scan attempt was created for this component."));
			}

			// Carry the catalog's required/optional flag through (issue #735 Finding 1):
			// a bare name list would discard IsRequired, which is exactly what let a
			// missing REQUIRED input slip through as an accepted, executable item. The
			// name-only list is still the value ScanPlanItem.DeclaredInputNames snapshots.
			List<PlanDeclaredInput> declaredInputs = [.. profile.DeclaredInputs
				.Select(i => new PlanDeclaredInput(i.Name, i.IsRequired))
				.OrderBy(i => i.Name, StringComparer.Ordinal)];
			List<string> declaredInputNames = [.. declaredInputs.Select(i => i.Name)];

			// Issue #735/ADR-0024: resolve this item's Input/Attestation config-doc
			// snapshot NOW, at plan-compile time, from the target's own site -- the same
			// "resolve once, freeze forever" discipline this method already applies to
			// baseline/benchmark identity above. A component with no resolvable target
			// (should not happen -- ON DELETE RESTRICT from components.parent_target_id
			// -- defensive only) skips config resolution rather than failing the whole
			// plan; the item still plans with an empty snapshot, matching
			// ScanPlanItem's "no resolution attempted" default.
			Target? target = await _targets.GetAsync(component.ParentTargetId, cancellationToken).ConfigureAwait(false);
			IReadOnlyList<Waypoint.Core.ConfigDocs.PlanInputResolution>? inputResolutions = null;
			Waypoint.Core.ConfigDocs.PlanAttestationResolution? attestationResolution = null;
			if (target is not null)
			{
				PlanConfigResolution configResolution = await _configResolution.ResolveAsync(
					profile.ExecutionProfile.Id, target.SiteId, target.Id, declaredInputs, DateTimeOffset.UtcNow, cancellationToken)
					.ConfigureAwait(false);
				inputResolutions = configResolution.Inputs;
				attestationResolution = configResolution.Attestation;

				// Missing-required-input gate (issue #735 owner decision "missing input
				// isolation", ADR-0024 line 114): a declared input marked required that
				// resolved to no document at any layer means this component cannot execute
				// without its required environmental input. Skip it (visible, non-secret
				// diagnostic naming the input definition) so siblings still plan -- the
				// sanctioned per-component skip path, NOT a plan-integrity failure.
				List<string> missingRequired = [.. inputResolutions
					.Where(r => r.IsRequired && string.Equals(r.State, Waypoint.Core.ConfigDocs.ConfigResolutionStates.Missing, StringComparison.Ordinal))
					.Select(r => r.InputName)
					.OrderBy(n => n, StringComparer.Ordinal)];
				if (missingRequired.Count > 0)
				{
					return (null, new ScanPlanSkip(componentId, ScanPlanSkipReasons.MissingRequiredInput,
						$"Component '{componentId}' requires input(s) '{string.Join("', '", missingRequired)}' which resolve to no configuration document at any layer (Global/Site/Target) for execution profile '{profile.ExecutionProfile.Id}'. "
							+ "Author an Input document supplying the required value(s) before scanning; no scan attempt was created for this component."));
				}
			}

			ScanPlanItem item = new(
				ComponentId: componentId,
				CatalogExecutionProfileId: profile.ExecutionProfile.Id,
				BaselineId: active.Id,
				BenchmarkRevisionId: active.BenchmarkRevisionId,
				Transport: profile.Component.Transport,
				SelectorKind: profile.Component.SelectorKind,
				SelectorName: profile.Component.SelectorName,
				ReportGroupKey: profile.ReportGroup.GroupKey,
				Priority: profile.ReportGroup.Priority,
				OutputKind: profile.ExecutionProfile.OutputKind,
				RequiredPurposes: purposes,
				DeclaredInputNames: declaredInputNames,
				InputResolutions: inputResolutions,
				AttestationResolution: attestationResolution);

			return (item, null);
		}

		return (null, new ScanPlanSkip(componentId, ScanPlanSkipReasons.NoActiveBaseline,
			$"Component '{componentId}' has a catalog-compatible execution profile but no active baseline; an Admin must activate one (ADR-0022)."));
	}

	private static string BuildExplanation(int candidateCount, int acceptedCount, List<ScanPlanSkip> skips)
	{
		if (candidateCount == 0)
		{
			return "No components were requested; this is an intentionally empty plan.";
		}

		if (skips.Count == 0)
		{
			return $"{acceptedCount} of {candidateCount} requested component(s) accepted into the plan; no gaps found.";
		}

		IEnumerable<string> reasonCounts = skips
			.GroupBy(s => s.Reason, StringComparer.Ordinal)
			.OrderBy(g => g.Key, StringComparer.Ordinal)
			.Select(g => $"{g.Count()} {g.Key}");

		return $"{acceptedCount} of {candidateCount} requested component(s) accepted into the plan; " +
			$"{skips.Count} skipped ({string.Join(", ", reasonCounts)}).";
	}
}
