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

using Waypoint.Core.Downloads.Photon;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Core.Downloads.Photon;

/// <summary>
/// Drift guard for <see cref="PhotonImageChannels.All"/>/<see cref="PhotonImageKinds.All"/>
/// against migration 0130's <c>photon_image_index_channel_check</c>/
/// <c>photon_image_index_kind_check</c>, mirroring
/// <c>PhotonRepoVariantsConstraintDriftTests</c>'s own convention.
///
/// <para>Issue #1876: this file used to carry its own private, unscoped
/// <c>CHECK ... IN (...)</c> regex against the full text of every embedded migration --
/// the exact cross-table-read hazard #1814 eliminated for eight other guards -- because
/// it arrived on <c>main</c> via #1844 after PR #1832's scope closed. It now routes
/// through the shared, table-scoped <see cref="ConstraintDriftScan"/> helper like every
/// other CHECK IN-list guard.</para>
/// </summary>
public sealed class PhotonImageIndexConstantsConstraintDriftTests
{
	private const string Table = "photon_image_index";

	[Fact]
	public void PhotonImageChannelsAll_EqualsChannelCheckConstraintValueSet()
	{
		List<string> constraintValues = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(
			Table, "photon_image_index_channel_check", "channel");

		Assert.Equal(
			new HashSet<string>(PhotonImageChannels.All, StringComparer.Ordinal),
			new HashSet<string>(constraintValues, StringComparer.Ordinal));
	}

	[Fact]
	public void PhotonImageKindsAll_EqualsKindCheckConstraintValueSet()
	{
		List<string> constraintValues = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(
			Table, "photon_image_index_kind_check", "image_kind");

		Assert.Equal(
			new HashSet<string>(PhotonImageKinds.All, StringComparer.Ordinal),
			new HashSet<string>(constraintValues, StringComparer.Ordinal));
	}

	/// <summary>
	/// Mutation-proof for #1876 AC2: a decoy migration declaring an identically-named
	/// CHECK on a different table must not change what this guard reads.
	/// </summary>
	[Fact]
	public void ParseLatestTableScopedCheckAcrossSql_IgnoresAnIdenticallyNamedCheckOnADifferentTable()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE IF NOT EXISTS photon_image_index (
			    channel TEXT NOT NULL CONSTRAINT photon_image_index_channel_check CHECK (channel IN ('stable', 'edge'))
			);

			CREATE TABLE reviewer_probe_table (
			    channel TEXT NOT NULL CONSTRAINT photon_image_index_channel_check CHECK (channel IN ('bogus'))
			);
			""",
		];

		List<string>? values = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossSql(
			migrations, Table, "photon_image_index_channel_check", "channel");

		Assert.Equal(["stable", "edge"], values);
	}
}
