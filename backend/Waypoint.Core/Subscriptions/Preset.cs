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
/// A subscription starting point (issue #1421, migration 0104, ADR-0028): either a
/// shipped, read-only row (<paramref name="IsCustom"/> <c>false</c>, curated in-repo
/// content refreshed by appliance updates) or an operator's clone-to-custom row
/// (<paramref name="IsCustom"/> <c>true</c>, <paramref name="SourcePresetId"/> naming
/// the preset it was cloned from). <paramref name="Generation"/> is a string, not an
/// integer, so no release generation is ever a literal in migration or C# source
/// (epic #16 decision 5) -- the appliance's shipped-preset seed data supplies the
/// actual values.
/// </summary>
/// <param name="Id">Primary key.</param>
/// <param name="Stack">The product stack, <c>VCF</c> or <c>VVF</c>.</param>
/// <param name="Generation">The stack generation this preset targets, e.g. the text <c>"9.0"</c> -- data, never a hardcoded literal.</param>
/// <param name="Name">Display name.</param>
/// <param name="LineGranularity">The tracking-line vocabulary a subscription adopted from this preset starts with.</param>
/// <param name="AnchorVersion">The starting anchor version, or <c>null</c> for a from-scratch custom preset with no version chosen yet.</param>
/// <param name="IsCustom">Whether this is an operator clone (<c>true</c>) or shipped read-only content (<c>false</c>).</param>
/// <param name="SourcePresetId">The preset this row was cloned from, when known; <c>null</c> for a shipped preset or a from-scratch custom preset.</param>
/// <param name="CreatedAt">Row creation timestamp.</param>
/// <param name="UpdatedAt">Row last-update timestamp.</param>
public sealed record Preset(
	Guid Id,
	string Stack,
	string Generation,
	string Name,
	SubscriptionLineGranularity LineGranularity,
	string? AnchorVersion,
	bool IsCustom,
	Guid? SourcePresetId,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);
