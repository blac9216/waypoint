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
