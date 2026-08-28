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
using Waypoint.Core.Components;
using Xunit;

namespace Waypoint.Tests.Core.Components;

/// <summary>
/// Issue #741: the pure selection rule for catalog-declared service expansion
/// (<see cref="CatalogDeclaredServiceComponents"/>) and the shared fact-inheritance
/// rule for the resulting child components (<see cref="ComponentFactInheritance"/>).
/// All fixtures are invented catalog shapes -- no real content, path, or lab value.
/// </summary>
public sealed class CatalogDeclaredServiceComponentsTests
{
	private static readonly Guid ProductVersionId = Guid.NewGuid();

	private static CatalogComponent Catalog(
		string key, string transport, string selectorKind, string? selectorName = null, Guid? id = null) =>
		new(id ?? Guid.NewGuid(), ProductVersionId, null, key, $"Display {key}", transport, selectorKind, selectorName, DateTimeOffset.UtcNow);

	[Fact]
	public void SelectDeclaredServiceChildren_SelectsOnlySshServiceComponents()
	{
		Guid linkedId = Guid.NewGuid();
		CatalogComponent linkedVCenter = Catalog("vcenter", CatalogTransports.VMware, CatalogSelectorKinds.VCenter, id: linkedId);
		List<CatalogComponent> versionComponents =
		[
			linkedVCenter,
			Catalog("esxi", CatalogTransports.VMware, CatalogSelectorKinds.Esxi),
			Catalog("vm", CatalogTransports.VMware, CatalogSelectorKinds.Vm),
			Catalog("postgresql", CatalogTransports.Ssh, CatalogSelectorKinds.Service, "postgresql"),
			Catalog("eam", CatalogTransports.Ssh, CatalogSelectorKinds.Service, "eam"),
			// nsx-api/vcf-api service selectors and whole-appliance ssh/target rows are
			// NOT OS-level services of this appliance -- excluded by the closed rule.
			Catalog("manager", CatalogTransports.NsxApi, CatalogSelectorKinds.Service, "manager"),
			Catalog("sddc-app", CatalogTransports.VcfApi, CatalogSelectorKinds.Service, "sddc-app"),
			Catalog("photon", CatalogTransports.Ssh, CatalogSelectorKinds.Target),
		];

		IReadOnlyList<CatalogDeclaredChild> declared =
			CatalogDeclaredServiceComponents.SelectDeclaredServiceChildren(versionComponents, linkedId);

		Assert.Equal(2, declared.Count);

		// Deterministic ordinal ordering by component key.
		Assert.Equal("eam", declared[0].CatalogComponentKey);
		Assert.Equal("postgresql", declared[1].CatalogComponentKey);
		Assert.All(declared, c => Assert.NotEqual(Guid.Empty, c.CatalogComponentId));
		Assert.Equal("Display eam", declared[0].DisplayName);
	}

	[Fact]
	public void SelectDeclaredServiceChildren_ExcludesTheLinkedComponentItself()
	{
		// Defensive: even if the linking root's own catalog component were somehow
		// ssh/service-shaped, it must never become its own child.
		Guid linkedId = Guid.NewGuid();
		List<CatalogComponent> versionComponents =
		[
			Catalog("weird-root", CatalogTransports.Ssh, CatalogSelectorKinds.Service, "weird-root", id: linkedId),
			Catalog("vami", CatalogTransports.Ssh, CatalogSelectorKinds.Service, "vami"),
		];

		IReadOnlyList<CatalogDeclaredChild> declared =
			CatalogDeclaredServiceComponents.SelectDeclaredServiceChildren(versionComponents, linkedId);

		CatalogDeclaredChild only = Assert.Single(declared);
		Assert.Equal("vami", only.CatalogComponentKey);
	}

	[Fact]
	public void SelectDeclaredServiceChildren_EmptyOrNoServices_ReturnsEmpty()
	{
		Guid linkedId = Guid.NewGuid();
		Assert.Empty(CatalogDeclaredServiceComponents.SelectDeclaredServiceChildren([], linkedId));
		Assert.Empty(CatalogDeclaredServiceComponents.SelectDeclaredServiceChildren(
			[Catalog("esxi", CatalogTransports.VMware, CatalogSelectorKinds.Esxi)], linkedId));
	}

	[Fact]
	public void SelectDeclaredServiceChildren_DuplicateKey_KeepsFirstOnly()
	{
		// Catalog natural keys forbid this, but the selection must still never yield
		// two children with one (parent, key) identity.
		Guid linkedId = Guid.NewGuid();
		CatalogComponent first = Catalog("lookup", CatalogTransports.Ssh, CatalogSelectorKinds.Service, "lookup");
		CatalogComponent duplicate = Catalog("lookup", CatalogTransports.Ssh, CatalogSelectorKinds.Service, "lookup");

		IReadOnlyList<CatalogDeclaredChild> declared =
			CatalogDeclaredServiceComponents.SelectDeclaredServiceChildren([first, duplicate], linkedId);

		CatalogDeclaredChild only = Assert.Single(declared);
		Assert.Equal(first.Id, only.CatalogComponentId);
	}

	// -- fact inheritance ----------------------------------------------------------

	private static Component Inventory(
		Guid? parentComponentId, string? vendorIdentity, ComponentFact? configured = null, ComponentFact? discovered = null, bool factConflict = false) =>
		new(
			Guid.NewGuid(), Guid.NewGuid(), parentComponentId, Guid.NewGuid(), "eam", vendorIdentity, "VCSA EAM",
			ComponentLifecycleStates.Active, configured, discovered, factConflict,
			DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

	[Fact]
	public void IsCatalogDeclaredChild_TrueOnlyForParentedNoVendorIdentityRows()
	{
		Assert.True(ComponentFactInheritance.IsCatalogDeclaredChild(Inventory(Guid.NewGuid(), null)));

		// A root connection component (both null) and a discovered object (vendor
		// identity non-null) never inherit.
		Assert.False(ComponentFactInheritance.IsCatalogDeclaredChild(Inventory(null, null)));
		Assert.False(ComponentFactInheritance.IsCatalogDeclaredChild(Inventory(null, "host-1001")));
		Assert.False(ComponentFactInheritance.IsCatalogDeclaredChild(Inventory(Guid.NewGuid(), "host-1001")));
	}

	[Fact]
	public void WithInheritedFacts_SubstitutesParentFactsAndConflict_KeepsChildIdentity()
	{
		ComponentFact parentConfigured = new("8.0.3", DateTimeOffset.UtcNow, null);
		ComponentFact parentDiscovered = new("8.0.3", DateTimeOffset.UtcNow.AddMinutes(-5), "obs-1");
		Component parent = Inventory(null, null, parentConfigured, parentDiscovered, factConflict: true);
		Component child = Inventory(parent.Id, null);

		Component inherited = ComponentFactInheritance.WithInheritedFacts(child, parent);

		Assert.Equal(child.Id, inherited.Id);
		Assert.Equal(child.CatalogComponentId, inherited.CatalogComponentId);
		Assert.Equal(child.CatalogComponentKey, inherited.CatalogComponentKey);
		Assert.Equal(child.Lifecycle, inherited.Lifecycle);
		Assert.Same(parentConfigured, inherited.ConfiguredFact);
		Assert.Same(parentDiscovered, inherited.DiscoveredFact);
		Assert.True(inherited.FactConflict);
	}
}
