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
		// must never silently let one shadow the other -- both are quarantined.
		VendorContentEntry a = Leaf("vsphere/8-0/v2r3-stig/inspec/base-one/vcenter", Manifest("vcenter"), "controls/a.rb");
		VendorContentEntry b = Leaf("vsphere/8-0/v2r3-stig/inspec/base-two/vcenter", Manifest("vcenter"), "controls/b.rb");

		SemanticImportReport report = ReconcileAll([a, b]);

		Assert.Empty(report.Accepted);
		Assert.Equal(2, report.Rejected.Count);
		Assert.All(report.Rejected, r => Assert.Contains("collides with", r.Reason));
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

	private static SemanticImportReport ReconcileAll(IReadOnlyList<VendorContentEntry> entries)
	{
		VendorHierarchyInterpretation interpretation = VendorHierarchyInterpreter.Interpret(entries);
		return SemanticImportReconciler.Reconcile(SourceCommit, interpretation, entries);
	}
}
