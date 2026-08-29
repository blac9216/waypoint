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

using System.Linq;
using Waypoint.Core.Components;
using Waypoint.Core.Discovery;
using Waypoint.Infrastructure.Discovery;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Execution;

/// <summary>
/// Issue #974 (owner decision on #967's options analysis, "Option A"):
/// <see cref="DiscoverJobHandler.MapToComponents"/> must resolve a host's
/// <see cref="DiscoveredComponent.ExactVersion"/> from <see cref="DiscoveredInventoryItem.Version"/>
/// (the semantic vSphere product version), never from <see cref="DiscoveredInventoryItem.Build"/>
/// (the raw build number) -- Build never equals the catalog's byte-for-byte semantic
/// <c>VersionKey</c> (ADR-0022), so using it as ExactVersion made every discovered ESXi
/// component permanently <c>is_compatible=false</c>. These are pure unit tests against
/// the internal mapping function (no PowerShell, no Postgres) -- the Postgres-backed
/// end-to-end proof that a discovered host actually links compatible against the real
/// migration 0064 seed lives in <c>DiscoverJobHandlerEndToEndTests</c>.
/// </summary>
public sealed class DiscoverJobHandlerMapToComponentsTests
{
	private const string InventedSemanticVersion = "8.0.3"; // Matches migration 0064's real seeded 'vsphere'/'8.0.3' row -- invented input, not lab-observed.
	private const string InventedBuildNumber = "99.0.87654321"; // Invented -- never a real lab build number.

	[Fact]
	public void Host_WithVersionReported_ResolvesExactVersionToSemanticVersion_NotBuild()
	{
		DiscoveredInventoryItem host = new(
			InventoryItemTypes.Host, "host-1", "esxi-01.example.internal", ParentMoref: null,
			Build: InventedBuildNumber, MaintenanceMode: false, Version: InventedSemanticVersion);

		IReadOnlyList<DiscoveredComponent> components = DiscoverJobHandler.MapToComponents([host]);

		DiscoveredComponent hostComponent = components.Single(c => c.VendorIdentity == "host-1");
		Assert.Equal(InventedSemanticVersion, hostComponent.ExactVersion);
		Assert.NotEqual(InventedBuildNumber, hostComponent.ExactVersion);
	}

	[Fact]
	public void Host_WithNoVersionReported_ResolvesExactVersionToNull_FailClosed_NeverFallsBackToBuild()
	{
		DiscoveredInventoryItem host = new(
			InventoryItemTypes.Host, "host-2", "esxi-02.example.internal", ParentMoref: null,
			Build: InventedBuildNumber, MaintenanceMode: false, Version: null);

		IReadOnlyList<DiscoveredComponent> components = DiscoverJobHandler.MapToComponents([host]);

		DiscoveredComponent hostComponent = components.Single(c => c.VendorIdentity == "host-2");
		Assert.Null(hostComponent.ExactVersion);
	}

	/// <summary>
	/// Issue #995: a powered-off/disconnected/connecting ESXi host reports Version as an
	/// EMPTY STRING, not null. Before this fix, that "" slipped past the `is null` guard
	/// one layer up in <see cref="DiscoverJobHandler.ResolveCatalogLinkageAsync"/> and
	/// reached <c>CatalogRepository.FindTopLevelComponentsByKeyAndVersionAsync</c>'s
	/// <c>ArgumentException.ThrowIfNullOrWhiteSpace</c>, aborting the whole discovery
	/// job. MapToComponents must normalize "" to null right here -- the exact same
	/// fail-closed outcome as the already-covered null-Version case above -- so an
	/// empty-string host is indistinguishable from a null-version host to every
	/// downstream consumer.
	/// </summary>
	[Fact]
	public void Host_WithEmptyStringVersionReported_ResolvesExactVersionToNull_FailClosed_NeverThrows()
	{
		DiscoveredInventoryItem host = new(
			InventoryItemTypes.Host, "host-3", "esxi-03.example.internal", ParentMoref: null,
			Build: InventedBuildNumber, MaintenanceMode: false, Version: string.Empty);

		IReadOnlyList<DiscoveredComponent> components = DiscoverJobHandler.MapToComponents([host]);

		DiscoveredComponent hostComponent = components.Single(c => c.VendorIdentity == "host-3");
		Assert.Null(hostComponent.ExactVersion);
	}

	/// <summary>
	/// Same as the empty-string case above, for a whitespace-only Version (e.g. a single
	/// space) -- string.IsNullOrWhiteSpace, not string.IsNullOrEmpty, is the correct
	/// normalization at this boundary since that is also what
	/// ArgumentException.ThrowIfNullOrWhiteSpace guards on downstream.
	/// </summary>
	[Fact]
	public void Host_WithWhitespaceOnlyVersionReported_ResolvesExactVersionToNull_FailClosed_NeverThrows()
	{
		DiscoveredInventoryItem host = new(
			InventoryItemTypes.Host, "host-4", "esxi-04.example.internal", ParentMoref: null,
			Build: InventedBuildNumber, MaintenanceMode: false, Version: "   ");

		IReadOnlyList<DiscoveredComponent> components = DiscoverJobHandler.MapToComponents([host]);

		DiscoveredComponent hostComponent = components.Single(c => c.VendorIdentity == "host-4");
		Assert.Null(hostComponent.ExactVersion);
	}

	[Fact]
	public void Host_WithBuildReported_CarriesBuildOntoTheComponentFact()
	{
		// Issue #1081: the ESXi component fact previously kept only ExactVersion and
		// silently dropped the Build discovery DID observe.
		DiscoveredInventoryItem host = new(
			InventoryItemTypes.Host, "host-5", "esxi-05.example.internal", ParentMoref: null,
			Build: InventedBuildNumber, MaintenanceMode: false, Version: InventedSemanticVersion);

		IReadOnlyList<DiscoveredComponent> components = DiscoverJobHandler.MapToComponents([host]);

		DiscoveredComponent hostComponent = components.Single(c => c.VendorIdentity == "host-5");
		Assert.Equal(InventedBuildNumber, hostComponent.Build);
	}

	[Fact]
	public void Vm_AlwaysResolvesExactVersionToNull_RegardlessOfBuildOrVersion()
	{
		// A VM's Build is never carried onto the component fact (issue #1081: it used
		// to carry the VMware Tools version, never a product-version fact for the VM
		// itself); VMs have no analogous semantic Version field either.
		DiscoveredInventoryItem vm = new(
			InventoryItemTypes.Vm, "vm-1", "stub-vm-01", ParentMoref: "host-1",
			Build: "12345", MaintenanceMode: null, Version: null);

		IReadOnlyList<DiscoveredComponent> components = DiscoverJobHandler.MapToComponents([vm]);

		DiscoveredComponent vmComponent = components.Single(c => c.VendorIdentity == "vm-1");
		Assert.Null(vmComponent.ExactVersion);
		Assert.Null(vmComponent.Build);
	}

	[Fact]
	public void Cluster_NeverBecomesAComponent_RegardlessOfVersionField()
	{
		DiscoveredInventoryItem cluster = new(
			InventoryItemTypes.Cluster, "domain-c1", "stub-cluster-01", ParentMoref: null,
			Build: null, MaintenanceMode: null, Version: null);

		IReadOnlyList<DiscoveredComponent> components = DiscoverJobHandler.MapToComponents([cluster]);

		// Only the vcenter root -- no component for the cluster grouping row.
		Assert.DoesNotContain(components, c => c.VendorIdentity == "domain-c1");
	}

	/// <summary>
	/// Issue #1081's core acceptance: a `vcenter`-type row (the appliance's own
	/// `content.about`-derived identity/version/build) becomes the ROOT component's
	/// own facts -- never a sibling component, and never dropped.
	/// </summary>
	[Fact]
	public void VCenterItem_BecomesRootIdentityAndVersionFact_NeverASiblingComponent()
	{
		DiscoveredInventoryItem vcenterItem = new(
			InventoryItemTypes.VCenter, "vcenter-instance-abc123", "vcsa-01.example.internal", ParentMoref: null,
			Build: InventedBuildNumber, MaintenanceMode: null, Version: InventedSemanticVersion);
		DiscoveredInventoryItem host = new(
			InventoryItemTypes.Host, "host-6", "esxi-06.example.internal", ParentMoref: null,
			Build: InventedBuildNumber, MaintenanceMode: false, Version: InventedSemanticVersion);

		IReadOnlyList<DiscoveredComponent> components = DiscoverJobHandler.MapToComponents([vcenterItem, host]);

		DiscoveredComponent root = Assert.Single(components, c => c.CatalogComponentKey == Waypoint.Core.ComplianceContent.CatalogSelectorKinds.VCenter);
		Assert.Equal("vcenter-instance-abc123", root.VendorIdentity);
		Assert.Equal("vcsa-01.example.internal", root.DisplayName);
		Assert.Equal(InventedSemanticVersion, root.ExactVersion);
		Assert.Equal(InventedBuildNumber, root.Build);
		Assert.Null(root.ParentVendorIdentity);

		// Never also materialized as a discovered sibling component.
		Assert.DoesNotContain(components, c => c.VendorIdentity == "vcenter-instance-abc123" && c.CatalogComponentKey != Waypoint.Core.ComplianceContent.CatalogSelectorKinds.VCenter);
		Assert.Equal(2, components.Count); // root + host-6 only.
	}

	/// <summary>
	/// Issue #1081 fail-closed fallback: a pass that reports no `vcenter` row at all
	/// (a pre-#1081 module/stub, or a boundary that genuinely could not observe the
	/// appliance's own identity) leaves the root exactly as before -- no vendor
	/// identity, no version/build -- honestly absent, never guessed from the target's
	/// connection host/FQDN/display name.
	/// </summary>
	[Fact]
	public void NoVCenterItemReported_RootStaysIdentityAndVersionAbsent_FailClosed()
	{
		DiscoveredInventoryItem host = new(
			InventoryItemTypes.Host, "host-7", "esxi-07.example.internal", ParentMoref: null,
			Build: InventedBuildNumber, MaintenanceMode: false, Version: InventedSemanticVersion);

		IReadOnlyList<DiscoveredComponent> components = DiscoverJobHandler.MapToComponents([host]);

		DiscoveredComponent root = Assert.Single(components, c => c.CatalogComponentKey == Waypoint.Core.ComplianceContent.CatalogSelectorKinds.VCenter);
		Assert.Null(root.VendorIdentity);
		Assert.Null(root.ExactVersion);
		Assert.Null(root.Build);
		Assert.Equal("vCenter Server", root.DisplayName);
	}

	/// <summary>
	/// Issue #995's empty-string normalization precedent, proven for the vcenter row
	/// too: a genuinely blank (never $null) Version/Name must not leak a "" fact or
	/// display name through to the root component.
	/// </summary>
	[Fact]
	public void VCenterItem_WithBlankVersionAndName_NormalizesToNull_AndDefaultDisplayName()
	{
		DiscoveredInventoryItem vcenterItem = new(
			InventoryItemTypes.VCenter, "vcenter-instance-blank", "   ", ParentMoref: null,
			Build: null, MaintenanceMode: null, Version: "");

		IReadOnlyList<DiscoveredComponent> components = DiscoverJobHandler.MapToComponents([vcenterItem]);

		DiscoveredComponent root = Assert.Single(components, c => c.CatalogComponentKey == Waypoint.Core.ComplianceContent.CatalogSelectorKinds.VCenter);
		Assert.Equal("vcenter-instance-blank", root.VendorIdentity); // MoRef itself is never blank-normalized (TryParseItem already guarantees non-blank).
		Assert.Null(root.ExactVersion);
		Assert.Null(root.Build);
		Assert.Equal("vCenter Server", root.DisplayName);
	}
}
