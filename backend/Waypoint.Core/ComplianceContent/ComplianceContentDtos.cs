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

namespace Waypoint.Core.ComplianceContent;

/// <summary>Closed <c>ref_type</c> values (migration 0035's CHECK constraint).</summary>
public static class ComplianceContentRefTypes
{
	public const string Tag = "tag";
	public const string Branch = "branch";

	public static bool IsValid(string? value) =>
		value is Tag or Branch;
}

/// <summary>Closed pull-history <c>status</c> values.</summary>
public static class ComplianceContentPullStatuses
{
	public const string Succeeded = "succeeded";
	public const string Failed = "failed";
}

/// <summary>Closed profile-inventory <c>state</c> values (docs/api-contract.md, issue #40 AC).</summary>
public static class ProfileStates
{
	public const string Current = "current";
	public const string UpdatePending = "update_pending";
	public const string Pinned = "pinned";
}

/// <summary>
/// The singleton compliance-content configuration (<c>compliance_content</c>,
/// migration 0035): which upstream ref to track and the commit last actually pulled.
/// Null <see cref="PulledCommit"/>/<see cref="PulledBy"/>/<see cref="PulledAt"/> means
/// configured but never successfully pulled yet.
/// </summary>
public sealed record ComplianceContentConfig(
	string RepositoryUrl,
	string RefType,
	string RefValue,
	string? PulledCommit,
	string? PulledBy,
	DateTimeOffset? PulledAt,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);

/// <summary>One row of pull history (<c>compliance_content_pulls</c>).</summary>
public sealed record ComplianceContentPull(
	Guid Id,
	Guid? JobId,
	string RefType,
	string RefValue,
	string? Commit,
	string Status,
	string? Note,
	string? InitiatedBy,
	DateTimeOffset CreatedAt);

/// <summary>One installed profile (<c>profiles</c>).</summary>
public sealed record Profile(
	Guid Id,
	string ProfileKey,
	string Name,
	string? Version,
	string Commit,
	string State,
	DateTimeOffset UpdatedAt);

/// <summary>A profile row to upsert -- what a content-pull run discovers per profile.</summary>
public sealed record ProfileUpsert(string ProfileKey, string Name, string? Version, string Commit, string State);
