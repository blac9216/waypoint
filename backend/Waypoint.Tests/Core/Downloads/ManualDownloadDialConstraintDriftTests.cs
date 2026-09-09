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

using Waypoint.Core.Downloads;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Core.Downloads;

/// <summary>
/// Drift guard for the two closed vocabularies migration 0107 introduced (issue
/// #1406) alongside <c>ManualDownloadRetentionDialResolver.Parse</c> (issue #1440),
/// following this repo's convention for every other closed-vocabulary/CHECK pairing
/// (<c>OciBundleStatusesConstraintDriftTests</c>, <c>RunTypesConstraintDriftTests</c>,
/// <c>InventoryItemTypesConstraintDriftTests</c>,
/// <c>ComponentResultStatusConstraintDriftTests</c>): parse the authoritative value
/// set straight out of the embedded migration SQL (no live database) and assert the
/// application-side constant matches exactly, in order. Issue #1686: neither
/// <see cref="ManualDownloadDialOptions"/> nor <see cref="RetainedContentStates"/> had
/// this guard despite <c>Parse</c> throwing on any value outside the three/five
/// constants -- a migration that widens or renames either CHECK without mirroring the
/// C# side previously produced only a hard runtime <see cref="ArgumentException"/> in
/// the retention path, with nothing in CI failing.
///
/// Resolution is scoped by BOTH the owning table and the constraint name via the
/// shared <see cref="ConstraintDriftScan"/> helper (issue #1814 -- every
/// <c>*ConstraintDriftTests</c> guard shares that one table-scoped, ALTER-visible
/// helper rather than each carrying its own unscoped or privately-scoped regex): a
/// CHECK is only read when it is declared inside the owning table's own
/// <c>CREATE TABLE</c> body or added to that table by a later
/// <c>ALTER TABLE ... ADD CONSTRAINT</c>, so an identically- OR differently-named
/// <c>... IN (...)</c> CHECK on some other table can never be picked up in its place.
/// Round-1 review (PR #1821) proved a name-only scan wrong by mutation: a scratch
/// migration declaring a brand-new <c>reviewer_probe_table</c> with a CHECK
/// constraint carrying the REAL constraint's own name
/// (<c>download_retention_policies_dial_check</c>) repointed the old, name-only guard
/// at the decoy's <c>('decoy-only')</c> value set (dropped, observed red, removed,
/// tree restored byte-identical -- see PR #1821's evidence). The two "ignores a decoy
/// on a different table" tests below keep that proof permanent, now against the
/// shared helper's own <see cref="ConstraintDriftScan.ParseLatestTableScopedCheckAcrossSql"/>.
/// </summary>
public sealed class ManualDownloadDialConstraintDriftTests
{
	private const string RetentionPoliciesTable = "download_retention_policies";
	private const string RetainedContentStateTable = "download_retained_content_state";

	[Fact]
	public void ManualDownloadDialOptionsAll_EqualsDownloadRetentionPoliciesDialCheckConstraintValueSet()
	{
		List<string> constraintValues = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(
			RetentionPoliciesTable, "download_retention_policies_dial_check", "manual_download_dial_default");

		Assert.Equal(ManualDownloadDialOptions.All, constraintValues);
	}

	[Fact]
	public void RetainedContentStatesAll_EqualsDownloadRetainedContentStateStateCheckConstraintValueSet()
	{
		List<string> constraintValues = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(
			RetainedContentStateTable, "download_retained_content_state_state_check", "state");

		Assert.Equal(RetainedContentStates.All, constraintValues);
	}

	/// <summary>
	/// Issue #1686 AC3: <c>ManualDownloadRetentionDialResolver.ToWireValue</c>'s
	/// <c>default</c> arm throws for any <see cref="ManualDownloadDial"/> member it
	/// does not explicitly handle -- iterating every current member here turns a
	/// future member added without updating <c>Parse</c>/<c>ToWireValue</c> into a
	/// failing test the moment it is added, rather than a defect that only surfaces
	/// as a runtime throw the first time that value is actually resolved. The
	/// round-trip (wire value back to the same enum member) also pins
	/// <see cref="ManualDownloadRetentionDialResolver.Parse"/> against the same set.
	/// </summary>
	[Fact]
	public void ManualDownloadDial_EveryEnumMember_RoundTripsThroughToWireValueAndParse()
	{
		foreach (ManualDownloadDial dial in Enum.GetValues<ManualDownloadDial>())
		{
			string wireValue = ManualDownloadRetentionDialResolver.ToWireValue(dial);

			Assert.Contains(wireValue, ManualDownloadDialOptions.All);
			Assert.Equal(dial, ManualDownloadRetentionDialResolver.Parse(wireValue));
		}
	}

	/// <summary>
	/// PR #1821 review round 1, F2 -- the round-1 reviewer's own probe, reproduced
	/// here permanently: a CHECK on an unrelated table carrying the REAL dial
	/// constraint's own name must never be read in its place. Table scoping is what
	/// makes the real <c>['auto-prune', 'keep', 'review']</c> still resolve. The real
	/// declaration comes first in this fixture's text (PR #1832 review round 1, F3)
	/// so the test cannot pass vacuously on a scan that keeps the LAST match
	/// regardless of table.
	/// </summary>
	[Fact]
	public void ParseLatestTableScopedCheckAcrossSql_IgnoresAnIdenticallyNamedDialCheckOnADifferentTable()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE IF NOT EXISTS download_retention_policies (
			    manual_download_dial_default TEXT NOT NULL CONSTRAINT download_retention_policies_dial_check CHECK (manual_download_dial_default IN ('auto-prune', 'keep', 'review'))
			);

			CREATE TABLE reviewer_probe_table (
			    manual_download_dial_default TEXT NOT NULL CONSTRAINT download_retention_policies_dial_check CHECK (manual_download_dial_default IN ('decoy-only'))
			);
			""",
		];

		List<string>? values = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossSql(
			migrations, RetentionPoliciesTable, "download_retention_policies_dial_check", "manual_download_dial_default");

		Assert.Equal(["auto-prune", "keep", "review"], values);
	}

	/// <summary>Same proof as above for the state vocabulary/table pairing, so both halves of this guard carry the same discrimination coverage.</summary>
	[Fact]
	public void ParseLatestTableScopedCheckAcrossSql_IgnoresAnIdenticallyNamedStateCheckOnADifferentTable()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE IF NOT EXISTS download_retained_content_state (
			    state TEXT NOT NULL CONSTRAINT download_retained_content_state_state_check CHECK (state IN ('tracked', 'grace', 'pinned', 'pending-purge', 'purged'))
			);

			CREATE TABLE reviewer_probe_table_2 (
			    state TEXT NOT NULL CONSTRAINT download_retained_content_state_state_check CHECK (state IN ('decoy-only'))
			);
			""",
		];

		List<string>? values = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossSql(
			migrations, RetainedContentStateTable, "download_retained_content_state_state_check", "state");

		Assert.Equal(["tracked", "grace", "pinned", "pending-purge", "purged"], values);
	}
}
