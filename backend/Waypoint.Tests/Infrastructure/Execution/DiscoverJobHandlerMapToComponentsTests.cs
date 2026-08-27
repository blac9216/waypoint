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

	[Fact]
	public void Vm_AlwaysResolvesExactVersionToNull_RegardlessOfBuildOrVersion()
	{
		// A VM's Build carries VMware Tools version (never a product-version fact for
		// the VM itself); VMs have no analogous semantic Version field either.
		DiscoveredInventoryItem vm = new(
			InventoryItemTypes.Vm, "vm-1", "stub-vm-01", ParentMoref: "host-1",
			Build: "12345", MaintenanceMode: null, Version: null);

		IReadOnlyList<DiscoveredComponent> components = DiscoverJobHandler.MapToComponents([vm]);

		DiscoveredComponent vmComponent = components.Single(c => c.VendorIdentity == "vm-1");
		Assert.Null(vmComponent.ExactVersion);
	}

	[Fact]
	public void Cluster_NeverBecomesAComponent_RegardlessOfVersionField()
	{
		DiscoveredInventoryItem cluster = new(
			InventoryItemTypes.Cluster, "domain-c1", "stub-cluster-01", ParentMoref: null,
			Build: null, MaintenanceMode: null, Version: null);

		IReadOnlyList<DiscoveredComponent> components = DiscoverJobHandler.MapToComponents([cluster]);

		// Only the synthetic vcenter root -- no component for the cluster grouping row.
		Assert.DoesNotContain(components, c => c.VendorIdentity == "domain-c1");
	}
}
