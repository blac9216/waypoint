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
using Waypoint.Core.ComplianceContent.SemanticImport;
using Xunit;
using static Waypoint.Tests.Core.ComplianceContent.SemanticImport.VendorContentEntryBuilder;

namespace Waypoint.Tests.Core.ComplianceContent.SemanticImport;

/// <summary>
/// Issue #729: <see cref="VendorHierarchyInterpreter"/> against representative INVENTED
/// miniature layouts for every documented family (docs/compliance-parity.md). No real
/// vendor content/paths/output appear anywhere in this file -- every path segment and
/// manifest below is fabricated.
/// </summary>
public sealed class VendorHierarchyInterpreterTests
{
	[Fact]
	public void VSphere_ObjectKindLeaves_ClassifyByProductVersionKindComponent()
	{
		VendorContentEntry vcenter = Leaf(
			"vsphere/8-0/v2r3-stig/inspec/vsphere-8-0-stig-baseline/vcenter",
			Manifest("vcenter", "vSphere 8.0 vCenter STIG", "2.3.0", ["vcenter_host"]),
			"controls/vc-000001.rb");
		VendorContentEntry esxi = Leaf(
			"vsphere/8-0/v2r3-stig/inspec/vsphere-8-0-stig-baseline/esxi",
			Manifest("esxi", "vSphere 8.0 ESXi STIG", "2.3.0"),
			"controls/esxi-000001.rb");
		VendorContentEntry vm = Leaf(
			"vsphere/8-0/v2r3-stig/inspec/vsphere-8-0-stig-baseline/vm",
			Manifest("vm", "vSphere 8.0 VM STIG", "2.3.0"),
			"controls/vm-000001.rb");
		VendorContentEntry aggregate = Aggregate(
			"vsphere/8-0/v2r3-stig/inspec/vsphere-8-0-stig-baseline",
			Manifest("vsphere-8-0-stig-baseline", "vSphere 8.0 STIG (all objects)"));

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([vcenter, esxi, vm, aggregate]);

		Assert.Empty(result.Rejections);
		Assert.Equal(4, result.Candidates.Count);

		SemanticCandidate vcenterCandidate = Assert.Single(result.Candidates, c => c.ComponentKey == "vcenter");
		Assert.Equal("8-0", vcenterCandidate.ProductVersionKey);
		Assert.Equal(CatalogKinds.Stig, vcenterCandidate.Kind);
		Assert.Equal(CatalogTransports.VMware, vcenterCandidate.Transport);
		Assert.Equal(CatalogSelectorKinds.VCenter, vcenterCandidate.SelectorKind);
		Assert.Null(vcenterCandidate.SelectorName);
		Assert.False(vcenterCandidate.IsAggregate);
		Assert.True(vcenterCandidate.IsExecutableLeaf);
		Assert.Equal("2.3.0", vcenterCandidate.ManifestVersion);
		Assert.Single(vcenterCandidate.Inputs);

		SemanticCandidate esxiCandidate = Assert.Single(result.Candidates, c => c.ComponentKey == "esxi");
		Assert.Equal(CatalogSelectorKinds.Esxi, esxiCandidate.SelectorKind);

		SemanticCandidate vmCandidate = Assert.Single(result.Candidates, c => c.ComponentKey == "vm");
		Assert.Equal(CatalogSelectorKinds.Vm, vmCandidate.SelectorKind);

		SemanticCandidate aggregateCandidate = Assert.Single(result.Candidates, c => c.ComponentKey == "aggregate");
		Assert.True(aggregateCandidate.IsAggregate);
		Assert.False(aggregateCandidate.IsExecutableLeaf);
	}

	[Fact]
	public void Vcsa_NamedServiceLeaves_ClassifyWithSshTransportAndServiceSelector()
	{
		VendorContentEntry eam = Leaf(
			"vcsa/8-0/v2r3-stig/inspec/vsphere-8-0-vcsa-stig-baseline/eam",
			Manifest("eam", "VCSA EAM STIG", "2.3.0"),
			"controls/eam-000001.rb");
		VendorContentEntry aggregate = Aggregate(
			"vcsa/8-0/v2r3-stig/inspec/vsphere-8-0-vcsa-stig-baseline",
			Manifest("vsphere-8-0-vcsa-stig-baseline", "VCSA STIG (all services)"));

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([eam, aggregate]);

		Assert.Empty(result.Rejections);
		SemanticCandidate eamCandidate = Assert.Single(result.Candidates, c => c.ComponentKey == "eam");
		Assert.Equal(CatalogTransports.Ssh, eamCandidate.Transport);
		Assert.Equal(CatalogSelectorKinds.Service, eamCandidate.SelectorKind);
		Assert.Equal("eam", eamCandidate.SelectorName);
		Assert.False(eamCandidate.IsAggregate);

		SemanticCandidate aggregateCandidate = Assert.Single(result.Candidates, c => c.ComponentKey == "aggregate");
		Assert.True(aggregateCandidate.IsAggregate);
	}

	[Fact]
	public void Nsx_NamedFunctionLeaves_ClassifyWithNsxApiTransport()
	{
		VendorContentEntry manager = Leaf(
			"nsx/4-x/v1r2-stig/inspec/nsx-4-x-stig-baseline/manager",
			Manifest("manager", "NSX Manager STIG", "1.2.0"),
			"controls/nsx-000001.rb");
		VendorContentEntry tier0Firewall = Leaf(
			"nsx/4-x/v1r2-stig/inspec/nsx-4-x-stig-baseline/tier-0-firewall",
			Manifest("tier-0-firewall", "NSX Tier-0 Firewall STIG", "1.2.0"),
			"controls/nsx-000002.rb");

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([manager, tier0Firewall]);

		Assert.Empty(result.Rejections);
		Assert.All(result.Candidates, c => Assert.Equal(CatalogTransports.NsxApi, c.Transport));
		Assert.All(result.Candidates, c => Assert.Equal(CatalogSelectorKinds.Service, c.SelectorKind));
		Assert.Equal(2, result.Candidates.Select(c => c.SelectorName).Distinct().Count());
	}

	[Fact]
	public void Photon_WholeApplianceTargetSelector_NoServiceNameInvented()
	{
		VendorContentEntry photon = Leaf(
			"photon/5-0/v3r3-srg/inspec/photon-os-5-0-srg-baseline",
			Manifest("photon-os-5-0-srg-baseline", "Photon OS 5.0 SRG", "3.3.0"),
			"controls/photon-000001.rb");

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([photon]);

		Assert.Empty(result.Rejections);
		SemanticCandidate candidate = Assert.Single(result.Candidates);
		Assert.Equal(CatalogTransports.Ssh, candidate.Transport);
		Assert.Equal(CatalogSelectorKinds.Target, candidate.SelectorKind);
		Assert.Null(candidate.SelectorName);
		Assert.Equal(CatalogKinds.Srg, candidate.Kind);
		Assert.False(candidate.IsAggregate);
	}

	[Theory]
	[InlineData("aria-operations", "8-x", "v1r4-srg")]
	[InlineData("aria-automation", "8-x", "v1r6-srg")]
	[InlineData("aria-suite-lifecycle", "8-x", "v1r2-srg")]
	public void Aria_WholeApplianceFamilies_ClassifyAsTargetSelector(string family, string version, string release)
	{
		VendorContentEntry entry = Leaf(
			$"{family}/{version}/{release}/inspec/{family}-baseline",
			Manifest($"{family}-baseline", $"{family} SRG", "1.0.0"),
			"controls/aria-000001.rb");

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([entry]);

		Assert.Empty(result.Rejections);
		SemanticCandidate candidate = Assert.Single(result.Candidates);
		Assert.Equal(family, candidate.VendorFamily);
		Assert.Equal(CatalogSelectorKinds.Target, candidate.SelectorKind);
		Assert.Equal(CatalogKinds.Srg, candidate.Kind);
	}

	[Fact]
	public void Vidm_WorkspaceOneAccess_WholeApplianceTargetSelector()
	{
		VendorContentEntry entry = Leaf(
			"vidm/3-3-x/v1r3-srg/inspec/vidm-3-3-x-srg-baseline",
			Manifest("vidm-3-3-x-srg-baseline", "Workspace ONE Access SRG", "1.3.0"),
			"controls/vidm-000001.rb");

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([entry]);

		Assert.Empty(result.Rejections);
		SemanticCandidate candidate = Assert.Single(result.Candidates);
		Assert.Equal(CatalogSelectorKinds.Target, candidate.SelectorKind);
		Assert.Equal(CatalogTransports.Ssh, candidate.Transport);
	}

	[Fact]
	public void AggregateProfile_IsNeverAnExecutableLeaf()
	{
		VendorContentEntry aggregate = Aggregate(
			"vsphere/9-0/y26m05-srg/inspec/vsphere-9-0-srg-baseline",
			Manifest("vsphere-9-0-srg-baseline", "vSphere 9.0 SRG (aggregate)"));

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([aggregate]);

		SemanticCandidate candidate = Assert.Single(result.Candidates);
		Assert.True(candidate.IsAggregate);
		Assert.False(candidate.IsExecutableLeaf);
	}

	[Fact]
	public void UnknownVendorFamily_IsQuarantinedNotGuessed()
	{
		VendorContentEntry unknown = Leaf(
			"totally-new-product/1-0/v1r1-stig/inspec/new-baseline",
			Manifest("new-baseline"),
			"controls/x.rb");

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([unknown]);

		Assert.Empty(result.Candidates);
		SemanticImportRejection rejection = Assert.Single(result.Rejections);
		Assert.Contains("not a recognized vendor family", rejection.Reason);
	}

	[Fact]
	public void ReleaseSegmentWithoutStigOrSrgSuffix_IsQuarantined()
	{
		VendorContentEntry entry = Leaf(
			"vsphere/8-0/v2r3-unknownkind/inspec/vsphere-8-0-baseline/vcenter",
			Manifest("vcenter"),
			"controls/x.rb");

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([entry]);

		Assert.Empty(result.Candidates);
		Assert.Contains(result.Rejections, r => r.Reason.Contains("-stig/-srg", StringComparison.Ordinal) || r.Reason.Contains("distinct first-class kinds", StringComparison.Ordinal));
	}

	[Fact]
	public void MalformedManifest_IsQuarantinedWithActionableDiagnostic()
	{
		VendorContentEntry entry = Leaf(
			"vsphere/8-0/v2r3-stig/inspec/vsphere-8-0-stig-baseline/vcenter",
			"not: [valid: yaml: at: all",
			"controls/x.rb");

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([entry]);

		Assert.Empty(result.Candidates);
		SemanticImportRejection rejection = Assert.Single(result.Rejections);
		Assert.Contains("could not be parsed safely", rejection.Reason);
	}

	[Fact]
	public void UnrecognizedObjectKindLeaf_UnderVSphere_IsQuarantined()
	{
		VendorContentEntry entry = Leaf(
			"vsphere/8-0/v2r3-stig/inspec/vsphere-8-0-stig-baseline/some-unlisted-object-kind",
			Manifest("some-unlisted-object-kind"),
			"controls/x.rb");

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([entry]);

		Assert.Empty(result.Candidates);
		Assert.Single(result.Rejections);
	}

	[Fact]
	public void Interpret_IsDeterministic_RegardlessOfInputOrder()
	{
		VendorContentEntry a = Leaf("vsphere/8-0/v2r3-stig/inspec/base/vcenter", Manifest("vcenter"), "controls/a.rb");
		VendorContentEntry b = Leaf("vsphere/8-0/v2r3-stig/inspec/base/esxi", Manifest("esxi"), "controls/b.rb");
		VendorContentEntry c = Leaf("vsphere/8-0/v2r3-stig/inspec/base/vm", Manifest("vm"), "controls/c.rb");

		VendorHierarchyInterpretation forward = VendorHierarchyInterpreter.Interpret([a, b, c]);
		VendorHierarchyInterpretation reversed = VendorHierarchyInterpreter.Interpret([c, b, a]);

		Assert.Equal(
			forward.Candidates.Select(x => x.ProfileKey),
			reversed.Candidates.Select(x => x.ProfileKey));
		Assert.Equal(
			forward.Candidates.Select(x => x.ContentDigest),
			reversed.Candidates.Select(x => x.ContentDigest));
	}

	[Fact]
	public void NearMissShortDepthPath_ValidFamilyValidReleaseButNoBaselineDir_QuarantinesNotThrows()
	{
		// Round-1 blocker exact repro: a 4-segment path with a recognized family, valid
		// version, valid -srg suffix, and an 'inspec' segment -- but NO baseline profile
		// directory. This previously reached `segments[5..]` and threw
		// ArgumentOutOfRangeException, aborting the whole import. It must now quarantine.
		VendorContentEntry nearMiss = Leaf(
			"photon/5-0/v3r3-srg/inspec",
			Manifest("inspec"),
			"controls/x.rb");

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([nearMiss]);

		Assert.Empty(result.Candidates);
		SemanticImportRejection rejection = Assert.Single(result.Rejections);
		Assert.Equal("photon/5-0/v3r3-srg/inspec", rejection.ProfileKey);
		Assert.Contains("baseline", rejection.Reason, StringComparison.Ordinal);
	}

	[Theory]
	// Too-shallow (4 segments, no baseline dir) -> quarantine, for -stig and -srg.
	[InlineData("photon/5-0/v3r3-srg/inspec", false)]
	[InlineData("vsphere/8-0/v2r3-stig/inspec", false)]
	[InlineData("vcsa/8-0/v2r3-stig/inspec", false)]
	[InlineData("nsx/4-x/v1r2-stig/inspec", false)]
	// Exact-minimum (5 segments, baseline dir present) -> classifies.
	[InlineData("photon/5-0/v3r3-srg/inspec/photon-baseline", true)]
	[InlineData("aria-operations/8-x/v1r4-srg/inspec/aria-baseline", true)]
	[InlineData("vsphere/8-0/v2r3-stig/inspec/vsphere-baseline", true)]
	[InlineData("vcsa/8-0/v2r3-stig/inspec/vcsa-baseline", true)]
	[InlineData("nsx/4-x/v1r2-stig/inspec/nsx-baseline", true)]
	// Extra-deep leaf (6 segments) -> classifies for the split families.
	[InlineData("vsphere/8-0/v2r3-stig/inspec/vsphere-baseline/vcenter", true)]
	[InlineData("vcsa/8-0/v2r3-stig/inspec/vcsa-baseline/eam", true)]
	[InlineData("nsx/4-x/v1r2-stig/inspec/nsx-baseline/manager", true)]
	// Over-deep (7 segments) -> whole-appliance treats extra nesting as aggregate
	// (classifies), split families have no documented sub-shape that deep -> quarantine.
	[InlineData("photon/5-0/v3r3-srg/inspec/photon-baseline/extra/deeper", true)]
	[InlineData("vsphere/8-0/v2r3-stig/inspec/vsphere-baseline/vcenter/deeper", false)]
	// Issue #959: vcf consolidated tree (same shape as vsphere, one segment deeper
	// baseline requirement is unchanged -- only the top-level literal differs).
	[InlineData("vcf/9-0/y26m05-srg/inspec", false)]
	[InlineData("vcf/9-0/y26m05-srg/inspec/vcf-baseline", true)]
	[InlineData("vcf/9-0/y26m05-srg/inspec/vcf-baseline/vcenter", true)]
	// Issue #959: vsphere object-kind-before-inspec shape needs one extra segment
	// before its own baseline-dir requirement kicks in.
	[InlineData("vsphere/8-0/v2r3-stig/vsphere/inspec", false)]
	[InlineData("vsphere/8-0/v2r3-stig/vsphere/inspec/vsphere-baseline", true)]
	[InlineData("vsphere/8-0/v2r3-stig/vsphere/inspec/vsphere-baseline/vcenter", true)]
	[InlineData("vsphere/8-0/v2r3-stig/vsphere/inspec/vsphere-baseline/vcenter/deeper", false)]
	public void DepthSweep_AcrossFamilies_EitherClassifiesOrQuarantinesNeverThrows(string profileKey, bool expectClassified)
	{
		VendorContentEntry entry = Leaf(profileKey, Manifest("m"), "controls/x.rb");

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([entry]);

		if (expectClassified)
		{
			Assert.Single(result.Candidates);
			Assert.Empty(result.Rejections);
		}
		else
		{
			Assert.Empty(result.Candidates);
			Assert.Single(result.Rejections);
		}
	}

	[Fact]
	public void PoisonedEntry_DoesNotAbortSiblingEntries_ContainmentPerEntry()
	{
		// Engineer one entry that throws an UNEXPECTED exception inside interpretation (a
		// null ProfileKey NREs at the Split call) and surround it with two perfectly good
		// siblings. Epic #726 invariant "one failure never stops siblings": the poisoned
		// entry must be quarantined with its exception type/message as the diagnostic and
		// both healthy siblings must still classify.
		VendorContentEntry goodBefore = Leaf(
			"aria-operations/8-x/v1r4-srg/inspec/aria-baseline", Manifest("aria-baseline"), "controls/a.rb");
		VendorContentEntry poisoned = new(
			ProfileKey: null!, RawYaml: Manifest("boom"), HasControlsDirectory: true, HasFilesDirectory: false,
			ControlFileNames: ["controls/x.rb"]);
		VendorContentEntry goodAfter = Leaf(
			"vidm/3-3-x/v1r3-srg/inspec/vidm-baseline", Manifest("vidm-baseline"), "controls/b.rb");

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([goodBefore, poisoned, goodAfter]);

		Assert.Equal(2, result.Candidates.Count);
		Assert.Contains(result.Candidates, c => c.VendorFamily == "aria-operations");
		Assert.Contains(result.Candidates, c => c.VendorFamily == "vidm");
		SemanticImportRejection rejection = Assert.Single(result.Rejections);
		Assert.Contains("unexpected error interpreting profile path", rejection.Reason, StringComparison.Ordinal);
		Assert.Contains("NullReferenceException", rejection.Reason, StringComparison.Ordinal);
	}

	[Fact]
	public void VcfConsolidatedTree_ClassifiesAsVsphereFamily_ObjectKindSplit()
	{
		// Issue #959: upstream `master` now nests the 9.x vSphere/vCenter/ESXi/VM
		// baselines under a consolidated vcf/<major>.x/... tree instead of a top-level
		// vsphere/9-0/... tree. Same vsphere product family, same object-kind-split
		// shape -- only the top-level directory literal differs.
		VendorContentEntry vcenter = Leaf(
			"vcf/9-0/y26m05-srg/inspec/vcf-9-0-srg-baseline/vcenter",
			Manifest("vcenter", "VCF 9.0 vCenter SRG", "1.0.0"),
			"controls/vc-000001.rb");
		VendorContentEntry esxi = Leaf(
			"vcf/9-0/y26m05-srg/inspec/vcf-9-0-srg-baseline/esxi",
			Manifest("esxi", "VCF 9.0 ESXi SRG", "1.0.0"),
			"controls/esxi-000001.rb");
		VendorContentEntry vm = Leaf(
			"vcf/9-0/y26m05-srg/inspec/vcf-9-0-srg-baseline/vm",
			Manifest("vm", "VCF 9.0 VM SRG", "1.0.0"),
			"controls/vm-000001.rb");

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([vcenter, esxi, vm]);

		Assert.Empty(result.Rejections);
		Assert.Equal(3, result.Candidates.Count);
		Assert.All(result.Candidates, c => Assert.Equal("vsphere", c.VendorFamily));
		Assert.All(result.Candidates, c => Assert.Equal(CatalogTransports.VMware, c.Transport));
		Assert.All(result.Candidates, c => Assert.Equal(CatalogKinds.Srg, c.Kind));
		Assert.Equal("9-0", result.Candidates[0].ProductVersionKey);
	}

	[Fact]
	public void VsphereObjectKindBeforeInspec_ClassifiesAsVsphereFamily()
	{
		// Issue #959: the current upstream vsphere/7.0 and vsphere/8.0 trees split by
		// object kind ONE segment before `inspec` --
		// vsphere/<version>/<release>/<object-kind>/inspec/<baseline>/<leaf> -- rather
		// than the documented <family>/<version>/<release>/inspec/<baseline> shape.
		VendorContentEntry vcenter = Leaf(
			"vsphere/8-0/v2r3-stig/vsphere/inspec/vsphere-8-0-stig-baseline/vcenter",
			Manifest("vcenter", "vSphere 8.0 vCenter STIG", "2.3.0"),
			"controls/vc-000001.rb");
		VendorContentEntry vcsaObjectKind = Leaf(
			"vsphere/8-0/v2r3-stig/vcsa/inspec/vsphere-8-0-vcsa-object-stig-baseline/vcenter",
			Manifest("vcenter", "vSphere 8.0 VCSA-object STIG", "2.3.0"),
			"controls/vcsa-000001.rb");

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([vcenter, vcsaObjectKind]);

		Assert.Empty(result.Rejections);
		Assert.Equal(2, result.Candidates.Count);
		Assert.All(result.Candidates, c => Assert.Equal("vsphere", c.VendorFamily));
		Assert.All(result.Candidates, c => Assert.Equal(CatalogTransports.VMware, c.Transport));
		Assert.All(result.Candidates, c => Assert.Equal(CatalogSelectorKinds.VCenter, c.SelectorKind));
	}

	[Fact]
	public void VsphereObjectKindBeforeInspec_UnrecognizedObjectKindSegment_IsQuarantined()
	{
		VendorContentEntry entry = Leaf(
			"vsphere/8-0/v2r3-stig/some-unlisted-object-kind-segment/inspec/vsphere-8-0-stig-baseline/vcenter",
			Manifest("vcenter"),
			"controls/x.rb");

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([entry]);

		Assert.Empty(result.Candidates);
		SemanticImportRejection rejection = Assert.Single(result.Rejections);
		Assert.Contains("expected an 'inspec' directory segment", rejection.Reason);
	}

	[Fact]
	public void VsphereObjectKindBeforeInspec_MissingBaselineDirectory_IsQuarantinedNotThrows()
	{
		VendorContentEntry entry = Leaf(
			"vsphere/8-0/v2r3-stig/vsphere/inspec",
			Manifest("inspec"),
			"controls/x.rb");

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([entry]);

		Assert.Empty(result.Candidates);
		SemanticImportRejection rejection = Assert.Single(result.Rejections);
		Assert.Contains("baseline", rejection.Reason, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("aria")]
	[InlineData("vcd")]
	[InlineData("avi")]
	public void StillUnrecognizedFamilies_RemainQuarantined_NeverGuessed(string unrecognizedFamily)
	{
		// Issue #959's disposition explicitly keeps aria/vcd/avi (and anything else not
		// documented in docs/compliance-parity.md's layout table) quarantined -- only
		// the vcf tree and the vsphere object-kind-before-inspec shape are newly
		// recognized.
		VendorContentEntry entry = Leaf(
			$"{unrecognizedFamily}/1-0/v1r1-srg/inspec/{unrecognizedFamily}-baseline",
			Manifest($"{unrecognizedFamily}-baseline"),
			"controls/x.rb");

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([entry]);

		Assert.Empty(result.Candidates);
		SemanticImportRejection rejection = Assert.Single(result.Rejections);
		Assert.Contains("not a recognized vendor family", rejection.Reason);
	}

	[Fact]
	public void IssueRejectionClasses_BothNowImportCleanly()
	{
		// Issue #959 acceptance: "a fixture reproducing the issue's two rejection
		// classes must now import cleanly" -- the 156-count vcf tree (formerly "'vcf'
		// is not a recognized vendor family directory") and the 111-count
		// object-kind-before-inspec tree (formerly "expected an 'inspec' directory
		// segment after the release directory").
		VendorContentEntry vcfTree = Leaf(
			"vcf/9-0/y26m05-srg/inspec/vcf-9-0-srg-baseline/esxi",
			Manifest("esxi", "VCF 9.0 ESXi SRG", "1.0.0"),
			"controls/esxi-000001.rb");
		VendorContentEntry objectKindBeforeInspecTree = Leaf(
			"vsphere/7-0/v1r1-stig/vsphere/inspec/vsphere-7-0-stig-baseline/vm",
			Manifest("vm", "vSphere 7.0 VM STIG", "1.1.0"),
			"controls/vm-000001.rb");

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([vcfTree, objectKindBeforeInspecTree]);

		Assert.Empty(result.Rejections);
		Assert.Equal(2, result.Candidates.Count);
	}

	[Fact]
	public void DuplicateLeafNames_AcrossDifferentParents_RemainDistinctProfiles()
	{
		// Two DIFFERENT vendor products both happen to have a leaf directory literally
		// named "postgresql" -- issue #729 AC "duplicate leaf names remain distinct".
		VendorContentEntry vcsaPostgres = Leaf(
			"vcsa/8-0/v2r3-stig/inspec/vsphere-8-0-vcsa-stig-baseline/postgresql",
			Manifest("postgresql", "VCSA PostgreSQL STIG"),
			"controls/pg-000001.rb");
		VendorContentEntry ariaPostgres = Leaf(
			"vcsa/9-0/y26m05-srg/inspec/vsphere-9-0-vcsa-srg-baseline/postgresql",
			Manifest("postgresql", "VCSA 9.0 PostgreSQL SRG"),
			"controls/pg-000002.rb");

		VendorHierarchyInterpretation result = VendorHierarchyInterpreter.Interpret([vcsaPostgres, ariaPostgres]);

		Assert.Empty(result.Rejections);
		Assert.Equal(2, result.Candidates.Count);
		Assert.Equal(2, result.Candidates.Select(c => c.ProfileKey).Distinct().Count());
	}
}
