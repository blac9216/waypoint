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

/// <summary>Response body for <c>GET</c>/<c>PUT /api/v1/compliance-content</c> (docs/api-contract.md).</summary>
public sealed record ComplianceContentResponse(
	string RepositoryUrl,
	string RefType,
	string RefValue,
	string? PulledCommit,
	string? PulledBy,
	DateTimeOffset? PulledAt,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt)
{
	public static ComplianceContentResponse FromDomain(ComplianceContentConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);
		return new ComplianceContentResponse(
			config.RepositoryUrl, config.RefType, config.RefValue,
			config.PulledCommit, config.PulledBy, config.PulledAt, config.CreatedAt, config.UpdatedAt);
	}
}

/// <summary>Request body for <c>PUT /api/v1/compliance-content</c>: exactly one of ref_type/ref_value pins a tag or tracks a branch.</summary>
public sealed record ComplianceContentBody(string? RepositoryUrl, string? RefType, string? RefValue);

/// <summary>Response body for one entry of <c>GET /api/v1/compliance-content/pulls</c> (pull history: who/when/commit, issue #40 AC).</summary>
public sealed record ComplianceContentPullResponse(
	string Id,
	string? JobId,
	string RefType,
	string RefValue,
	string? Commit,
	string Status,
	string? Note,
	string? InitiatedBy,
	DateTimeOffset CreatedAt)
{
	public static ComplianceContentPullResponse FromDomain(ComplianceContentPull pull)
	{
		ArgumentNullException.ThrowIfNull(pull);
		return new ComplianceContentPullResponse(
			pull.Id.ToString(), pull.JobId?.ToString(), pull.RefType, pull.RefValue,
			pull.Commit, pull.Status, pull.Note, pull.InitiatedBy, pull.CreatedAt);
	}
}

/// <summary>202 response for <c>POST /api/v1/compliance-content/pull</c>.</summary>
public sealed record ContentPullStartedResponse(string RunId);

/// <summary>Response body for one entry of <c>GET /api/v1/profiles</c> (issue #40 AC "Profile inventory drives the Benchmarks profile list", #559).</summary>
public sealed record ProfileResponse(
	string Id,
	string ProfileKey,
	string Name,
	string? Version,
	string Commit,
	string State,
	DateTimeOffset UpdatedAt)
{
	public static ProfileResponse FromDomain(Profile profile)
	{
		ArgumentNullException.ThrowIfNull(profile);
		return new ProfileResponse(
			profile.Id.ToString(), profile.ProfileKey, profile.Name, profile.Version,
			profile.Commit, profile.State, profile.UpdatedAt);
	}
}

/// <summary>
/// Response body for one entry of <c>GET /api/v1/profiles/{id}/controls</c>
/// (docs/api-contract.md: "Control, severity, title, effective input + scope, attest
/// status", issue #598). <c>title</c>/<c>severity</c> come from
/// <see cref="Waypoint.Core.ComplianceContent.ProfileControl"/>, parsed from the
/// profile's InSpec control files at content-pull time -- either can be null for a
/// terse or malformed control file (the control id itself is the only value this
/// endpoint requires).
///
/// <c>effective_input</c>/<c>effective_input_layer</c>/<c>attest_status</c>/
/// <c>attest_layer</c> are a documented deviation from the contract's literal
/// per-control phrasing: config-docs resolve as whole YAML bodies per (kind, profile,
/// layer) (docs/domain-model.md "the schemas belong to Broadcom/MITRE" -- no per-control
/// structured storage exists anywhere in that schema), so these four fields are the
/// PROFILE's effective <c>input</c>/<c>attestation</c> resolution for the caller's
/// <c>target</c> (when supplied), identical across every control row in the same
/// response -- not independently resolved per control id. They are null whenever
/// <c>target</c> is omitted, or when no layer has a doc for that kind at all.
/// <c>attest_status</c> is the coarse "applied" | "expired" | "none" derived from
/// <see cref="Waypoint.Core.ConfigDocs.ConfigDocResolver"/>'s existing attestation-expiry
/// semantics, not a per-control waiver lookup.
/// </summary>
public sealed record ProfileControlResponse(
	string Id,
	string ControlId,
	string? Title,
	string? Severity,
	string? EffectiveInput,
	string? EffectiveInputLayer,
	string AttestStatus,
	string? AttestLayer)
{
	private const string AttestStatusApplied = "applied";
	private const string AttestStatusExpired = "expired";
	private const string AttestStatusNone = "none";

	public static ProfileControlResponse FromDomain(
		ProfileControl control,
		Waypoint.Core.ConfigDocs.ConfigDocResolution? inputResolution,
		Waypoint.Core.ConfigDocs.ConfigDocResolution? attestationResolution)
	{
		ArgumentNullException.ThrowIfNull(control);

		string attestStatus = attestationResolution switch
		{
			{ AttestationExpired: true } => AttestStatusExpired,
			{ Body: not null } => AttestStatusApplied,
			_ => AttestStatusNone,
		};

		return new ProfileControlResponse(
			control.Id.ToString(),
			control.ControlId,
			control.Title,
			control.Severity,
			inputResolution?.Body,
			inputResolution?.Layer,
			attestStatus,
			attestationResolution?.Layer);
	}
}
