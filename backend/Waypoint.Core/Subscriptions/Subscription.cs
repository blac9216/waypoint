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

namespace Waypoint.Core.Subscriptions;

/// <summary>
/// A durable "keep this scope current" expression (issue #1421, migration 0104,
/// ADR-0028): a <paramref name="Product"/>/<paramref name="Lane"/> pair tracked at
/// <paramref name="LineGranularity"/>, anchored at <paramref name="AnchorVersion"/> --
/// the version whose line other catalog versions are compared against via
/// <see cref="ISubscriptionLineEvaluator"/>. Adopting a subscription pulls the whole
/// release (every bundle/binary), never a filtered subset (epic #16 decision 5) --
/// that acquisition wiring is #1046's slice, not this record's.
/// </summary>
/// <param name="Id">Primary key.</param>
/// <param name="Product">The catalog product key (e.g. <c>VCENTER</c>) this subscription tracks.</param>
/// <param name="Lane">The acquisition lane, one of <see cref="Waypoint.Core.Secrets.RepoStores.All"/>.</param>
/// <param name="LineGranularity">The tracking-line vocabulary this subscription declares.</param>
/// <param name="AnchorVersion">The vendor version string whose line (per <see cref="LineGranularity"/>) is tracked; parsed via <see cref="Waypoint.Core.Versions.ProductVersionParser"/> at evaluation time, never pre-parsed into this record.</param>
/// <param name="PresetId">The preset this subscription was adopted from, or <c>null</c> for a from-scratch subscription.</param>
/// <param name="RefreshWindowDays">Per-lane UMDS/VKS time-window dial override; <c>null</c> means "use the lane's own default".</param>
/// <param name="RetentionOverrideDays">Per-subscription retention/grace override in days; <c>null</c> means "use the download-retention domain's (#1406) default policy".</param>
/// <param name="IsEnabled">Whether the evaluation job (#1046) should currently evaluate this subscription.</param>
/// <param name="CreatedAt">Row creation timestamp.</param>
/// <param name="UpdatedAt">Row last-update timestamp.</param>
public sealed record Subscription(
	Guid Id,
	string Product,
	string Lane,
	SubscriptionLineGranularity LineGranularity,
	string AnchorVersion,
	Guid? PresetId,
	int? RefreshWindowDays,
	int? RetentionOverrideDays,
	bool IsEnabled,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);
