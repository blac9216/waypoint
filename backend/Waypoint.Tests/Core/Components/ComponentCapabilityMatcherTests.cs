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
/// Issue #732 AC: "capability matching against catalog selectors and product/build/
/// version facts with EXACT incompatibility reasons (fail closed)." Pure domain logic,
/// no database -- fixtures are hand-built <see cref="CatalogExecutionProfileDetail"/>
/// records, same convention as <c>CatalogVocabularyValidatorTests</c>.
/// </summary>
public sealed class ComponentCapabilityMatcherTests
{
	private static readonly Guid CatalogComponentId = Guid.NewGuid();
	private static readonly Guid ProductVersionId = Guid.NewGuid();

	// Issue #998's CORRECTED owner decision: the catalog product-version key is the
	// vendor's declared version scope, VERBATIM (e.g. minor-scoped "8.0"), never a
	// patch-level byte-for-byte identity -- ExactVersion is the fuller observed/
	// configured fact a host actually reports, and CatalogVersionKey is the declared
	// scope it is matched against via VersionScopeMatcher (never plain string equality).
	private const string ExactVersion = "8.0.3";
	private const string CatalogVersionKey = "8.0";

	private static Component MakeComponent(
		string? configuredVersion = null,
		string? discoveredVersion = null,
		bool factConflict = false,
		Guid? catalogComponentId = null)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		return new Component(
			Guid.NewGuid(),
			Guid.NewGuid(),
			null,
			catalogComponentId,
			"esxi",
			"host-1",
			"esxi-01.example.internal",
			ComponentLifecycleStates.Active,
			configuredVersion is null ? null : new ComponentFact(configuredVersion, now, null),
			discoveredVersion is null ? null : new ComponentFact(discoveredVersion, now, null),
			factConflict,
			now,
			now,
			null,
			null,
			now,
			now);
	}

	private static CatalogExecutionProfileDetail MakeProfile(Guid componentId, Guid productVersionId, string versionKey)
	{
		CatalogProduct product = new(Guid.NewGuid(), Guid.NewGuid(), "vmware", "vsphere", "VMware vSphere", DateTimeOffset.UtcNow);
		CatalogProductVersion productVersion = new(productVersionId, product.Id, versionKey, versionKey, DateTimeOffset.UtcNow);
		CatalogComponent component = new(componentId, productVersionId, null, "esxi", "ESXi Host", CatalogTransports.VMware, CatalogSelectorKinds.Esxi, null, DateTimeOffset.UtcNow);
		CatalogContentRelease release = new(Guid.NewGuid(), Guid.NewGuid(), CatalogKinds.Stig, "v2r3-stig", "VMware vSphere STIG v2r3", DateTimeOffset.UtcNow);
		CatalogReportGroup reportGroup = new(Guid.NewGuid(), "esxi-stig", "ESXi STIG", 4, DateTimeOffset.UtcNow);
		CatalogExecutionProfile profile = new(Guid.NewGuid(), componentId, release.Id, reportGroup.Id, "v2r3", false, CatalogOutputKinds.HdfAndCkl, DateTimeOffset.UtcNow);

		return new CatalogExecutionProfileDetail(profile, component, productVersion, product, release, reportGroup, [], null, null, []);
	}

	[Fact]
	public void Match_ExactVersionWithinLinkedCatalogScope_IsCompatible()
	{
		Component component = MakeComponent(discoveredVersion: ExactVersion, catalogComponentId: CatalogComponentId);
		CatalogExecutionProfileDetail profile = MakeProfile(CatalogComponentId, ProductVersionId, CatalogVersionKey);

		ComponentCapabilityMatch match = ComponentCapabilityMatcher.Match(component, ProductVersionId, CatalogVersionKey, [profile]);

		Assert.True(match.IsCompatible);
		Assert.Single(match.CompatibleProfiles);
		Assert.Empty(match.IncompatibilityReasons);
	}

	[Fact]
	public void Match_NoConfiguredOrDiscoveredFact_FailsClosedWithExactReason()
	{
		Component component = MakeComponent(catalogComponentId: CatalogComponentId);

		ComponentCapabilityMatch match = ComponentCapabilityMatcher.Match(component, ProductVersionId, CatalogVersionKey, []);

		Assert.False(match.IsCompatible);
		Assert.Empty(match.CompatibleProfiles);
		Assert.Contains(match.IncompatibilityReasons, r => r.Contains("no configured or discovered exact product version", StringComparison.Ordinal));
	}

	[Fact]
	public void Match_FactConflict_FailsClosedBeforeEvaluatingCatalog()
	{
		Component component = MakeComponent(configuredVersion: "8.0.2", discoveredVersion: ExactVersion, factConflict: true, catalogComponentId: CatalogComponentId);
		CatalogExecutionProfileDetail profile = MakeProfile(CatalogComponentId, ProductVersionId, CatalogVersionKey);

		ComponentCapabilityMatch match = ComponentCapabilityMatcher.Match(component, ProductVersionId, CatalogVersionKey, [profile]);

		Assert.False(match.IsCompatible);
		Assert.Contains(match.IncompatibilityReasons, r => r.Contains("configured/discovered product-version conflict", StringComparison.Ordinal));
	}

	[Fact]
	public void Match_NotLinkedToCatalogComponent_FailsClosedWithExactReason()
	{
		Component component = MakeComponent(discoveredVersion: ExactVersion, catalogComponentId: null);

		ComponentCapabilityMatch match = ComponentCapabilityMatcher.Match(component, null, null, []);

		Assert.False(match.IsCompatible);
		Assert.Contains(match.IncompatibilityReasons, r => r.Contains("not linked to a known catalog component", StringComparison.Ordinal));
	}

	[Fact]
	public void Match_ExactVersionOutsideLinkedCatalogScope_FailsClosedWithExactReason()
	{
		// The component resolved a version outside the linked catalog scope's declared
		// major.minor -- never substitutes the nearest baseline.
		Component component = MakeComponent(discoveredVersion: "8.1.2", catalogComponentId: CatalogComponentId);
		CatalogExecutionProfileDetail profile = MakeProfile(CatalogComponentId, ProductVersionId, CatalogVersionKey);

		ComponentCapabilityMatch match = ComponentCapabilityMatcher.Match(component, ProductVersionId, CatalogVersionKey, [profile]);

		Assert.False(match.IsCompatible);
		Assert.Contains(match.IncompatibilityReasons, r => r.Contains("is not within the linked catalog product version", StringComparison.Ordinal));
	}

	[Fact]
	public void Match_LinkedButNoExecutionProfileForVersion_FailsClosedWithExactReason()
	{
		Component component = MakeComponent(discoveredVersion: ExactVersion, catalogComponentId: CatalogComponentId);

		ComponentCapabilityMatch match = ComponentCapabilityMatcher.Match(component, ProductVersionId, CatalogVersionKey, []);

		Assert.False(match.IsCompatible);
		Assert.Contains(match.IncompatibilityReasons, r => r.Contains("no catalog execution profile", StringComparison.Ordinal));
	}

	[Fact]
	public void Match_ConfiguredAndDiscoveredFactsAgree_UsesAgreedValue()
	{
		Component component = MakeComponent(configuredVersion: ExactVersion, discoveredVersion: ExactVersion, catalogComponentId: CatalogComponentId);
		CatalogExecutionProfileDetail profile = MakeProfile(CatalogComponentId, ProductVersionId, CatalogVersionKey);

		ComponentCapabilityMatch match = ComponentCapabilityMatcher.Match(component, ProductVersionId, CatalogVersionKey, [profile]);

		Assert.True(match.IsCompatible);
	}

	/// <summary>
	/// Issue #998's declared-scope-key semantics also apply to a major-line-scoped
	/// catalog key ("9.x") -- proves the matcher is not implicitly assuming minor-scoped
	/// keys everywhere it is wired.
	/// </summary>
	[Fact]
	public void Match_MajorLineScopedCatalogKey_MatchesAnyMinorUnderThatMajor()
	{
		Component component = MakeComponent(discoveredVersion: "9.0.0", catalogComponentId: CatalogComponentId);
		CatalogExecutionProfileDetail profile = MakeProfile(CatalogComponentId, ProductVersionId, "9.x");

		ComponentCapabilityMatch match = ComponentCapabilityMatcher.Match(component, ProductVersionId, "9.x", [profile]);

		Assert.True(match.IsCompatible);
	}
}
