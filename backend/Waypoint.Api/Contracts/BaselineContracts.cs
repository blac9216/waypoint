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

namespace Waypoint.Api.Contracts;

/// <summary>Response body for one entry of <c>GET /api/v1/baselines</c> (docs/api-contract.md, issue #731).</summary>
public sealed record BaselineResponse(
	string Id,
	string ContentRevisionId,
	string CatalogExecutionProfileId,
	string? BenchmarkRevisionId,
	string Status,
	DateTimeOffset? ActivatedAt,
	string? ActivatedBy,
	DateTimeOffset? SupersededAt,
	DateTimeOffset CreatedAt)
{
	public static BaselineResponse FromDomain(Baseline baseline)
	{
		ArgumentNullException.ThrowIfNull(baseline);
		return new BaselineResponse(
			baseline.Id.ToString(),
			baseline.ContentRevisionId.ToString(),
			baseline.CatalogExecutionProfileId.ToString(),
			baseline.BenchmarkRevisionId?.ToString(),
			baseline.Status,
			baseline.ActivatedAt,
			baseline.ActivatedBy,
			baseline.SupersededAt,
			baseline.CreatedAt);
	}
}

/// <summary>
/// Request body for <c>POST /api/v1/baselines</c> -- stages (does not activate) a
/// baseline binding an already-staged <see cref="ContentRevision"/> to a catalog
/// execution profile. Issue #731: this is the missing caller for
/// <see cref="Waypoint.Core.ComplianceContent.IBaselineRepository.CreateStagedBaselineAsync"/>
/// -- the naming ("stage a baseline" as its own resource-create rather than an
/// implicit side effect of content-pull) is a documented assumption; docs/api-contract.md
/// names the read (<c>GET /baselines</c>) and activate/rollback actions but not a
/// staging-create route, so this slice adds the minimal honest one under the
/// documented <c>/baselines</c> resource rather than inventing a new top-level noun.
/// </summary>
public sealed record CreateBaselineRequest(Guid? ContentRevisionId, Guid? CatalogExecutionProfileId, Guid? BenchmarkRevisionId);

/// <summary>Request body for <c>POST /api/v1/baselines/{id}/activate</c> and <c>/rollback</c> (docs/api-contract.md confirmation-phrase convention).</summary>
public sealed record BaselineActivationRequest(string? Confirmation);

/// <summary>Response body for <c>GET /api/v1/baselines/{id}/impact-diff</c> (issue #731 AC "operators see a deterministic impact diff before activation").</summary>
public sealed record BaselineImpactDiffResponse(int AddedProfiles, int ChangedProfiles, int RemovedProfiles, int UnsupportedCapabilities)
{
	public static BaselineImpactDiffResponse FromDomain(BaselineImpactDiff diff)
	{
		ArgumentNullException.ThrowIfNull(diff);
		return new BaselineImpactDiffResponse(diff.AddedProfiles, diff.ChangedProfiles, diff.RemovedProfiles, diff.UnsupportedCapabilities);
	}
}
