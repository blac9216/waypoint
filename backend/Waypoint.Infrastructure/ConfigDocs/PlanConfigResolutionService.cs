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

using Waypoint.Core.ConfigDocs;

namespace Waypoint.Infrastructure.ConfigDocs;

/// <summary>
/// Resolves a single <see cref="Waypoint.Core.Scans.ScanPlanItem"/>'s Input and
/// Attestation config documents Global -> Site -> Target (issue #735, ADR-0024
/// "Control-granular settings and snapshots"), keyed to the plan item's stable
/// <c>CatalogExecutionProfileId</c> (migration 0060) rather than a free-text profile
/// name or a single fixed <c>ScanOptions.AttestationProfile</c> setting. Called by
/// <see cref="Waypoint.Infrastructure.Runs.ScanPlannerService"/> once per accepted
/// item, at plan-compile time -- exactly the same "resolve once, freeze forever" point
/// migration 0057 already established for catalog/baseline identity, extended here to
/// the config-doc layer.
///
/// <b>Whole-document granularity, not per-control (deliberate, matches the epic's own
/// scope note):</b> ADR-0024 supersedes "the profile-wide document shortcut" for a
/// FUTURE per-control settings catalog that does not exist in this codebase yet (the
/// same gap <see cref="AttestationSnapshot"/>'s doc comment already discloses for the
/// runtime side). This resolver therefore reports the SAME resolved Input document
/// against every one of a plan item's declared input names, and the SAME resolved
/// Attestation document for the whole item -- there is no per-control key to resolve
/// against yet. What it DOES fix relative to the pre-#735 state is the KEY: resolution
/// is scoped to the exact catalog execution profile a plan item actually consumes,
/// never a coarse fixed name shared by every scan regardless of profile/component.
///
/// Remediation-input resolution is out of scope for this slice (ADR-0024: "Remediation
/// execution is not authorized by these settings and remains issue #15"; this issue's
/// own "Remainder" list keeps remediation inputs out entirely).
/// </summary>
public sealed class PlanConfigResolutionService
{
	private readonly ConfigDocRepository _configDocs;

	public PlanConfigResolutionService(ConfigDocRepository configDocs)
	{
		ArgumentNullException.ThrowIfNull(configDocs);
		_configDocs = configDocs;
	}

	/// <summary>
	/// Resolves every declared input name plus the attestation slot for one plan item,
	/// against the given target's site (Global -> Site -> Target, matching
	/// <see cref="ConfigDocsController"/>'s own resolve endpoint one layer up). Pure I/O
	/// -- no side effects, no persistence; the caller (<see cref="Waypoint.Infrastructure.Runs.ScanPlannerService"/>)
	/// folds the result into the frozen <see cref="Waypoint.Core.Scans.ScanPlanItem"/>.
	/// </summary>
	public async Task<PlanConfigResolution> ResolveAsync(
		Guid catalogExecutionProfileId,
		Guid siteId,
		Guid targetId,
		IReadOnlyList<string> declaredInputNames,
		DateTimeOffset now,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(declaredInputNames);

		(ConfigDocResolution resolution, ConfigDocWithLatestVersion? global, ConfigDocWithLatestVersion? site, ConfigDocWithLatestVersion? target) =
			await ResolveKindAsync(ConfigDocKinds.Input, catalogExecutionProfileId, siteId, targetId, now, cancellationToken).ConfigureAwait(false);
		_ = (global, site, target); // candidates only needed to build `resolution` above.

		string inputState = resolution.Body is not null
			? ConfigResolutionStates.Resolved
			: ConfigResolutionStates.Missing;

		List<PlanInputResolution> inputs = [.. declaredInputNames
			.OrderBy(n => n, StringComparer.Ordinal)
			.Select(name => new PlanInputResolution(
				name, inputState, resolution.Layer, resolution.DocId, resolution.Version, resolution.Author, resolution.UpdatedAt))];

		(ConfigDocResolution attestationResolution, _, _, _) = await ResolveKindAsync(
			ConfigDocKinds.Attestation, catalogExecutionProfileId, siteId, targetId, now, cancellationToken).ConfigureAwait(false);

		string attestationState = attestationResolution switch
		{
			{ Body: not null } => ConfigResolutionStates.Resolved,
			{ AttestationExpired: true } => ConfigResolutionStates.Expired,
			_ => ConfigResolutionStates.Missing,
		};

		PlanAttestationResolution attestation = new(
			attestationState,
			attestationResolution.Layer,
			attestationResolution.DocId,
			attestationResolution.Version,
			attestationResolution.Author,
			attestationResolution.UpdatedAt,
			Applied: attestationResolution.Body is not null,
			Expired: attestationResolution.AttestationExpired,
			ExpiresAt: attestationResolution.AttestationExpiresAt);

		return new PlanConfigResolution(inputs, attestation);
	}

	private async Task<(ConfigDocResolution Resolution, ConfigDocWithLatestVersion? Global, ConfigDocWithLatestVersion? Site, ConfigDocWithLatestVersion? Target)> ResolveKindAsync(
		string kind, Guid catalogExecutionProfileId, Guid siteId, Guid targetId, DateTimeOffset now, CancellationToken cancellationToken)
	{
		ConfigDocWithLatestVersion? global = await _configDocs
			.FindWithLatestVersionByCatalogExecutionProfileAsync(kind, catalogExecutionProfileId, ConfigDocLayers.Global, null, cancellationToken)
			.ConfigureAwait(false);
		ConfigDocWithLatestVersion? site = await _configDocs
			.FindWithLatestVersionByCatalogExecutionProfileAsync(kind, catalogExecutionProfileId, ConfigDocLayers.Site, siteId, cancellationToken)
			.ConfigureAwait(false);
		ConfigDocWithLatestVersion? target = await _configDocs
			.FindWithLatestVersionByCatalogExecutionProfileAsync(kind, catalogExecutionProfileId, ConfigDocLayers.Target, targetId, cancellationToken)
			.ConfigureAwait(false);

		// catalogExecutionProfileId/kind are passed through only for a resolver-shape
		// call that the (unused) `profile` string parameter never actually inspects for
		// candidate selection -- ConfigDocResolver.Resolve's `profile` argument is
		// carried onto ConfigDocResolution.Profile for display only (see its doc
		// comment); the candidates it picks among are exactly `global`/`site`/`target`
		// as already selected above. A stable placeholder string is passed rather than
		// deriving one, since nothing here has (or needs) the human profile name.
		ConfigDocResolution resolution = ConfigDocResolver.Resolve(
			kind, catalogExecutionProfileId.ToString(), global, site, target, now);

		return (resolution, global, site, target);
	}
}

/// <summary>One plan item's fully resolved config snapshot -- see <see cref="PlanConfigResolutionService"/>.</summary>
public sealed record PlanConfigResolution(IReadOnlyList<PlanInputResolution> Inputs, PlanAttestationResolution Attestation);
