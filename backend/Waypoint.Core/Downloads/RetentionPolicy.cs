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

namespace Waypoint.Core.Downloads;

/// <summary>
/// One row of the <c>download_retention_policies</c> table (migration 0107, issue
/// #1406, epic #1182 "Subscriptions, retention & scheduling"). <see cref="ScopeKey"/>
/// <c>"default"</c> is the seeded, always-present appliance-wide fallback every
/// artifact resolves to when no more specific policy exists; a per-subscription
/// override is any other <see cref="ScopeKey"/> value (a subscription id rendered as
/// text -- no FK, since the subscriptions table does not exist yet, see #1421).
/// Model and persistence only -- the sweep that reads this shape is #1436, the
/// manual-download dial's own resolution logic is #1440.
/// </summary>
public sealed record RetentionPolicy(
	Guid Id,
	string ScopeKey,
	int GracePeriodDays,
	int GraceMaxRefreshes,
	string ManualDownloadDialDefault,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);

/// <summary>The appliance-wide fallback <see cref="RetentionPolicy.ScopeKey"/>, seeded by migration 0107.</summary>
public static class RetentionPolicyScopes
{
	public const string Default = "default";
}

/// <summary>The exact string values of <c>download_retention_policies.manual_download_dial_default</c>, matching <c>download_retention_policies_dial_check</c> in migration 0107. #1440 owns the dial's resolution logic; this is only the scope-level default it starts from.</summary>
public static class ManualDownloadDialOptions
{
	public const string AutoPrune = "auto-prune";
	public const string Keep = "keep";
	public const string Review = "review";
}
