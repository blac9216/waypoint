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
/// The closed <see cref="Preset.Stack"/> vocabulary migration 0104's
/// <c>presets_stack_check</c> CHECK constraint fixes (issue #1421, review round 1
/// finding F2: the migration introduced this vocabulary with no mirroring C#
/// constant and no drift test, unlike <c>subscriptions.lane</c>'s reuse of
/// <see cref="Waypoint.Core.Secrets.RepoStores"/>). Proven against the migration's
/// own CHECK by
/// <see cref="Waypoint.Tests.Infrastructure.Postgres.SubscriptionsConstraintDriftTests"/>.
/// </summary>
public static class PresetStacks
{
	public const string Vcf = "VCF";
	public const string Vvf = "VVF";

	/// <summary>Declaration order matches the migration's CHECK constraint value list.</summary>
	public static readonly IReadOnlyList<string> All = [Vcf, Vvf];

	public static bool IsValid(string? stack) => stack is not null && All.Contains(stack);
}
