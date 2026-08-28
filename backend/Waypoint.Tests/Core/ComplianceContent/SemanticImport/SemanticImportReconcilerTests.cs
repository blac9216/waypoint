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

using Waypoint.Core.ComplianceContent.SemanticImport;
using Xunit;
using static Waypoint.Tests.Core.ComplianceContent.SemanticImport.VendorContentEntryBuilder;

namespace Waypoint.Tests.Core.ComplianceContent.SemanticImport;

/// <summary>
/// Issue #729 deliverables 4-5: <see cref="SemanticImportReconciler"/> vocabulary
/// reconciliation, quarantine, and the deterministic import report. All fixtures are
/// invented miniature layouts, never real vendor content.
/// </summary>
public sealed class SemanticImportReconcilerTests
{
	private const string SourceCommit = "deadbeefcafefeed0000000000000000000000";

	[Fact]
	public void Reconcile_ValidLeafAndAggregate_AggregateNotAcceptedAsExecutable()
	{
		VendorContentEntry leaf = Leaf(
			"vsphere/8-0/v2r3-stig/inspec/base/vcenter",
			Manifest("vcenter", "vCenter STIG", "2.3.0", ["vcenter_host"]),
			"controls/a.rb");
		VendorContentEntry aggregate = Aggregate("vsphere/8-0/v2r3-stig/inspec/base", Manifest("base"));

		SemanticImportReport report = ReconcileAll([leaf, aggregate]);

		Assert.Equal(2, report.Accepted.Count);
		Assert.Empty(report.Rejected);
		SemanticImportAccepted acceptedAggregate = Assert.Single(report.Accepted, a => a.Candidate.IsAggregate);
		Assert.False(acceptedAggregate.Candidate.IsExecutableLeaf);
	}

	[Fact]
	public void Reconcile_LeafWithNoControls_IsRejected()
	{
		// A leaf-shaped candidate (object-kind selector) whose entry declares no
		// controls/*.rb at all fails the structure-validation gate (deliverable 3).
		VendorContentEntry emptyLeaf = new(
			"vsphere/8-0/v2r3-stig/inspec/base/vcenter",
			Manifest("vcenter"),
			HasControlsDirectory: false,
			HasFilesDirectory: false,
			ControlFileNames: []);

		SemanticImportReport report = ReconcileAll([emptyLeaf]);

		Assert.Empty(report.Accepted);
		SemanticImportRejected rejection = Assert.Single(report.Rejected);
		Assert.Contains("no controls/*.rb", rejection.Reason);
	}

	[Fact]
	public void Reconcile_LeafMissingVersionOrInputs_AcceptedWithWarnings()
	{
		VendorContentEntry leaf = Leaf(
			"vsphere/8-0/v2r3-stig/inspec/base/esxi",
			Manifest("esxi", "ESXi STIG"),
			"controls/a.rb");

		SemanticImportReport report = ReconcileAll([leaf]);

		Assert.Single(report.Accepted);
		Assert.Equal(2, report.Warnings.Count);
		Assert.Contains(report.Warnings, w => w.Message.Contains("no version"));
		Assert.Contains(report.Warnings, w => w.Message.Contains("no inputs"));
	}

	[Fact]
	public void Reconcile_UnknownLayout_PropagatesInterpreterRejectionIntoReport()
	{
		VendorContentEntry unknown = Leaf("some-unknown-family/1-0/v1r1-stig/inspec/base", Manifest("base"), "controls/a.rb");

		SemanticImportReport report = ReconcileAll([unknown]);

		Assert.Empty(report.Accepted);
		Assert.Single(report.Rejected);
	}

	[Fact]
	public void Reconcile_ComponentKeyCollisionWithinScope_QuarantinesBothProfiles()
	{
		// Two distinct profile keys that (by construction of a hypothetical future
		// family bug) resolve to the same (product-version, kind, component_key) scope
		// AND the same release must never silently let one shadow the other -- both are
		// quarantined. Same-release collisions are a genuine shape ambiguity, distinct
		// from the cross-release "newest wins" case below.
		VendorContentEntry a = Leaf("vsphere/8-0/v2r3-stig/inspec/base-one/vcenter", Manifest("vcenter"), "controls/a.rb");
		VendorContentEntry b = Leaf("vsphere/8-0/v2r3-stig/inspec/base-two/vcenter", Manifest("vcenter"), "controls/b.rb");

		SemanticImportReport report = ReconcileAll([a, b]);

		Assert.Empty(report.Accepted);
		Assert.Equal(2, report.Rejected.Count);
		// Issue #986 preserves this pre-existing reason string BYTE-FOR-BYTE for the
		// same-release tie class (no appended release-ordering diagnostic) -- pin it
		// exactly so a future edit that extends it fails here, not in a live report.
		SemanticImportRejected first = Assert.Single(report.Rejected, r => r.ProfileKey == a.ProfileKey);
		Assert.Equal(
			"component_key 'vcenter' collides with 1 other profile(s) in the same product-version/kind scope: vsphere/8-0/v2r3-stig/inspec/base-two/vcenter",
			first.Reason);
	}

	[Fact]
	public void Reconcile_TwoReleasesSameScope_NewestPromotesOldestSupersededQuarantined()
	{
		// Issue #986 (owner decision 2026-08-28, "newest release wins"): round-5 live
		// data showed the SAME component across two releases of one declared version
		// scope collapsing into the pre-#986 collision path, which quarantined BOTH ever.
		// This is the failing-test-first proof: before #986's fix this asserts the OLD
		// (both-rejected) behavior; after the fix, the newest release's profile is
		// promoted and the older one is quarantined by name.
		VendorContentEntry older = Leaf("vsphere/8-0/v2r2-stig/inspec/base/vcenter", Manifest("vcenter", "vCenter STIG", "2.2.0"), "controls/a.rb");
		VendorContentEntry newer = Leaf("vsphere/8-0/v2r3-stig/inspec/base/vcenter", Manifest("vcenter", "vCenter STIG", "2.3.0"), "controls/a.rb");

		SemanticImportReport report = ReconcileAll([older, newer]);

		SemanticImportAccepted winner = Assert.Single(report.Accepted);
		Assert.Equal(newer.ProfileKey, winner.Candidate.ProfileKey);
		Assert.Equal("v2r3-stig", winner.Candidate.ReleaseKey);

		SemanticImportRejected loser = Assert.Single(report.Rejected);
		Assert.Equal(older.ProfileKey, loser.ProfileKey);
		Assert.Equal("superseded by release 'v2r3-stig' (profile 'vsphere/8-0/v2r3-stig/inspec/base/vcenter') -- newest release wins within one declared version scope (issue #986)", loser.Reason);
	}

	[Fact]
	public void Reconcile_ThreeReleasesSameScope_OnlyNewestWins()
	{
		VendorContentEntry oldest = Leaf("photon/5-0/v1r3-srg/inspec/base", Manifest("base", version: "1.3"), "controls/a.rb");
		VendorContentEntry middle = Leaf("photon/5-0/v2r1-srg/inspec/base", Manifest("base", version: "2.1"), "controls/a.rb");
		VendorContentEntry newest = Leaf("photon/5-0/v3r1-srg/inspec/base", Manifest("base", version: "3.1"), "controls/a.rb");

		SemanticImportReport report = ReconcileAll([oldest, middle, newest]);

		SemanticImportAccepted winner = Assert.Single(report.Accepted);
		Assert.Equal(newest.ProfileKey, winner.Candidate.ProfileKey);
		Assert.Equal(2, report.Rejected.Count);
		Assert.All(report.Rejected, r => Assert.Contains("superseded by release 'v3r1-srg'", r.Reason));
	}

	[Fact]
	public void Reconcile_UnknownReleaseForm_FailsClosedQuarantinesAllInScope()
	{
		// A release segment that does not parse under either closed form (V#R# or
		// Y##M##[-srg]) cannot be ordered, so the whole scope fails closed rather than
		// guessing a winner. (ParseReleaseSegment's own -stig/-srg suffix gate already
		// rejects segments with no suffix at all, so this drives an entry whose suffix IS
		// recognized but whose ordering prefix is NOT one of the two closed forms.)
		VendorContentEntry weird = Leaf("photon/5-0/weird-release-srg/inspec/base", Manifest("base"), "controls/a.rb");
		VendorContentEntry normal = Leaf("photon/5-0/v1r3-srg/inspec/base", Manifest("base"), "controls/a.rb");

		SemanticImportReport report = ReconcileAll([weird, normal]);

		Assert.Empty(report.Accepted);
		Assert.Equal(2, report.Rejected.Count);
		Assert.All(report.Rejected, r => Assert.Contains("release ordering", r.Reason, StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void Reconcile_CrossFormTieWithinSameScope_FailsClosedAsCollision()
	{
		// A V#R# release and a Y##M##-srg release both present in the SAME declared
		// version scope/kind/component is a design hole the owner decision explicitly
		// declined to resolve with an invented cross-form ordering: fail closed into
		// quarantine for both, naming the ambiguity, rather than guessing which form is
        // "newer".
		VendorContentEntry vForm = Leaf("photon/5-0/v3r3-srg/inspec/base", Manifest("base"), "controls/a.rb");
		VendorContentEntry yForm = Leaf("photon/5-0/Y26M05-srg/inspec/base", Manifest("base"), "controls/a.rb");

		SemanticImportReport report = ReconcileAll([vForm, yForm]);

		Assert.Empty(report.Accepted);
		Assert.Equal(2, report.Rejected.Count);
		Assert.All(report.Rejected, r => Assert.Contains("cross-form", r.Reason, StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void Reconcile_SingleRelease_UnchangedBehavior()
	{
		VendorContentEntry only = Leaf("photon/5-0/v3r3-srg/inspec/base", Manifest("base"), "controls/a.rb");

		SemanticImportReport report = ReconcileAll([only]);

		Assert.Single(report.Accepted);
		Assert.Empty(report.Rejected);
	}

	[Fact]
	public void Reconcile_DeterministicUnderShuffledEnumeration_SameWinnerRegardlessOfOrder()
	{
		VendorContentEntry r1 = Leaf("photon/5-0/v1r3-srg/inspec/base", Manifest("base"), "controls/a.rb");
		VendorContentEntry r2 = Leaf("photon/5-0/v2r1-srg/inspec/base", Manifest("base"), "controls/a.rb");
		VendorContentEntry r3 = Leaf("photon/5-0/v3r1-srg/inspec/base", Manifest("base"), "controls/a.rb");

		SemanticImportReport forward = ReconcileAll([r1, r2, r3]);
		SemanticImportReport shuffled = ReconcileAll([r3, r1, r2]);
		SemanticImportReport reversed = ReconcileAll([r3, r2, r1]);

		Assert.Equal(forward.Accepted.Single().Candidate.ProfileKey, shuffled.Accepted.Single().Candidate.ProfileKey);
		Assert.Equal(forward.Accepted.Single().Candidate.ProfileKey, reversed.Accepted.Single().Candidate.ProfileKey);
		Assert.Equal(forward.SourceDigest, shuffled.SourceDigest);
		Assert.Equal(forward.SourceDigest, reversed.SourceDigest);
	}

	[Fact]
	public void Reconcile_IsDeterministic_SameDigestRegardlessOfInputOrder()
	{
		VendorContentEntry a = Leaf("vsphere/8-0/v2r3-stig/inspec/base/vcenter", Manifest("vcenter", version: "1.0"), "controls/a.rb");
		VendorContentEntry b = Leaf("vsphere/8-0/v2r3-stig/inspec/base/esxi", Manifest("esxi", version: "1.0"), "controls/b.rb");

		SemanticImportReport forward = ReconcileAll([a, b]);
		SemanticImportReport reversed = ReconcileAll([b, a]);

		Assert.Equal(forward.SourceDigest, reversed.SourceDigest);
		Assert.Equal(
			forward.Accepted.Select(x => x.Candidate.ProfileKey),
			reversed.Accepted.Select(x => x.Candidate.ProfileKey));
	}

	[Fact]
	public void Reconcile_DifferentContent_ProducesDifferentDigest()
	{
		VendorContentEntry a = Leaf("vsphere/8-0/v2r3-stig/inspec/base/vcenter", Manifest("vcenter", version: "1.0"), "controls/a.rb");
		VendorContentEntry aChanged = Leaf("vsphere/8-0/v2r3-stig/inspec/base/vcenter", Manifest("vcenter", version: "1.1"), "controls/a.rb");

		SemanticImportReport report1 = ReconcileAll([a]);
		SemanticImportReport report2 = ReconcileAll([aChanged]);

		Assert.NotEqual(report1.SourceDigest, report2.SourceDigest);
	}

	[Fact]
	public void Reconcile_ReportCarriesSourceCommit()
	{
		VendorContentEntry entry = Leaf("photon/5-0/v3r3-srg/inspec/base", Manifest("base"), "controls/a.rb");

		SemanticImportReport report = ReconcileAll([entry]);

		Assert.Equal(SourceCommit, report.SourceCommit);
	}

	[Fact]
	public void Reconcile_ExecutableLeafWithNoMatchingSourceEntry_QuarantinesInsteadOfThrowing()
	{
		// Same unguarded-indexing class as the interpreter slice crash: previously the
		// reconciler indexed entriesByKey[candidate.ProfileKey] directly, so a candidate
		// with no matching source entry would throw KeyNotFoundException and abort the
		// whole reconcile. Drive that inconsistency directly: interpret a real leaf, then
		// reconcile it against an EMPTY entry list. It must quarantine, not throw.
		VendorContentEntry leaf = Leaf(
			"vsphere/8-0/v2r3-stig/inspec/base/vcenter",
			Manifest("vcenter", "vCenter STIG", "2.3.0", ["vcenter_host"]),
			"controls/a.rb");
		VendorHierarchyInterpretation interpretation = VendorHierarchyInterpreter.Interpret([leaf]);

		SemanticImportReport report = SemanticImportReconciler.Reconcile(SourceCommit, interpretation, []);

		Assert.Empty(report.Accepted);
		SemanticImportRejected rejection = Assert.Single(report.Rejected);
		Assert.Contains("no matching source content entry", rejection.Reason, StringComparison.Ordinal);
	}

	[Fact]
	public void Reconcile_DuplicateSourceEntryKeys_DoesNotThrow()
	{
		// A duplicate ProfileKey in the raw entry list (filesystem-walk artifact) once
		// made entries.ToDictionary throw ArgumentException, aborting reconcile. It must
		// now be tolerated (first-writer-wins) and still produce a report.
		VendorContentEntry a = Leaf("photon/5-0/v3r3-srg/inspec/base", Manifest("base"), "controls/a.rb");
		VendorContentEntry aDuplicateKey = Leaf("photon/5-0/v3r3-srg/inspec/base", Manifest("base"), "controls/b.rb");

		VendorHierarchyInterpretation interpretation = VendorHierarchyInterpreter.Interpret([a]);
		SemanticImportReport report = SemanticImportReconciler.Reconcile(SourceCommit, interpretation, [a, aDuplicateKey]);

		Assert.NotNull(report);
	}

	private static SemanticImportReport ReconcileAll(IReadOnlyList<VendorContentEntry> entries)
	{
		VendorHierarchyInterpretation interpretation = VendorHierarchyInterpreter.Interpret(entries);
		return SemanticImportReconciler.Reconcile(SourceCommit, interpretation, entries);
	}
}
