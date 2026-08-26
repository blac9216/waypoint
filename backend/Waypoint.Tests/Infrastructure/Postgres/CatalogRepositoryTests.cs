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

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.ComplianceContent.SemanticImport;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #728 (epic #726, Wave 1): the normalized compliance catalog schema and
/// closed capability vocabulary, against a real PostgreSQL 16 container (migration
/// 0050). Fixtures below are INVENTED and only shaped like the sibling
/// <c>settings/catalog.json</c> rows documented in docs/compliance-parity.md's
/// "Sibling source-capability provenance matrix" -- they are not exported from any
/// real system (CLAUDE.md sanitization policy).
///
/// Covers every issue #728 AC:
/// <list type="bullet">
/// <item><description>Faithful representation of documented sibling scan/remediation row shapes (vSphere 8.0 STIG w/ named VCSA services, vSphere 9.0 SRG).</description></item>
/// <item><description>STIG and SRG as distinct first-class kinds.</description></item>
/// <item><description>Multi-release components (two execution profiles on one component).</description></item>
/// <item><description>Repeated leaf names across different parents (two components share a component_key under different parents).</description></item>
/// <item><description>Historical retention: a content release/component referenced by an execution profile cannot be deleted (FK RESTRICT).</description></item>
/// <item><description>Unknown capability vocabulary fails closed with an actionable message.</description></item>
/// </list>
/// </summary>
[Collection("Postgres")]
public sealed class CatalogRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private CatalogRepository _repository = null!;

	public CatalogRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();
		_repository = new CatalogRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			TRUNCATE TABLE
				catalog_import_report_entries, catalog_import_reports, catalog_declared_inputs,
				catalog_remediation_definitions, catalog_benchmark_references, catalog_credential_requirements,
				catalog_execution_profiles, catalog_report_groups, catalog_components, catalog_content_releases,
				catalog_product_versions, catalog_products, catalog_source_revisions
			RESTART IDENTITY CASCADE
			""", connection);
		await command.ExecuteNonQueryAsync();
	}

	/// <summary>One invented source revision every fixture below hangs off.</summary>
	private async Task<Guid> SeedSourceRevisionAsync(string key = "test-revision-1") =>
		(await _repository.UpsertSourceRevisionAsync(key, "invented fixture revision", CancellationToken.None)).Id;

	[Fact]
	public async Task FaithfullyRepresents_VSphere80Stig_WithNamedVcsaServiceComponents()
	{
		// Invented fixture shaped like docs/compliance-parity.md's "vSphere 8-0 / STIG /
		// v2r3-stig" rows: vmware-transport object-kind components (vCenter/ESXi/VM) plus
		// ssh-transport named-VCSA-service components (EAM, PostgreSQL), all under one
		// exact product version, each with its own execution profile, credential
		// requirements, and STIG benchmark reference -- never lossily inferred from a path.
		Guid sourceRevisionId = await SeedSourceRevisionAsync();
		CatalogProduct product = await _repository.UpsertProductAsync(sourceRevisionId, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion version = await _repository.UpsertProductVersionAsync(product.Id, "8.0.3", "vSphere 8.0 Update 3", CancellationToken.None);
		CatalogContentRelease release = await _repository.UpsertContentReleaseAsync(sourceRevisionId, CatalogKinds.Stig, "v2r3-stig", "VMware vSphere 8.0 STIG v2r3", CancellationToken.None);
		CatalogReportGroup vcenterGroup = await _repository.UpsertReportGroupAsync("vcenter-stig", "vCenter STIG", 3, CancellationToken.None);
		CatalogReportGroup vcsaGroup = await _repository.UpsertReportGroupAsync("vcsa-stig", "VCSA STIG", 2, CancellationToken.None);

		CatalogComponent vcenter = await _repository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("vcenter", "vCenter Server", CatalogTransports.VMware, CatalogSelectorKinds.VCenter, null, null), CancellationToken.None);
		CatalogComponent eam = await _repository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("eam", "VCSA EAM Service", CatalogTransports.Ssh, CatalogSelectorKinds.Service, "eam", null), CancellationToken.None);
		CatalogComponent postgres = await _repository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("postgresql", "VCSA PostgreSQL Service", CatalogTransports.Ssh, CatalogSelectorKinds.Service, "postgresql", null), CancellationToken.None);

		CatalogExecutionProfile vcenterProfile = await _repository.CreateExecutionProfileAsync(vcenter.Id, release.Id, vcenterGroup.Id, "v2r3", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);
		await _repository.AddCredentialRequirementAsync(vcenterProfile.Id, "vsphere-api", true, CancellationToken.None);
		await _repository.SetBenchmarkReferenceAsync(vcenterProfile.Id, "VMW_vSphere_8-0_vCenter_STIG", "v2r3", CancellationToken.None);

		CatalogExecutionProfile eamProfile = await _repository.CreateExecutionProfileAsync(eam.Id, release.Id, vcsaGroup.Id, "v2r3", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);
		await _repository.AddCredentialRequirementAsync(eamProfile.Id, "vsphere-api", true, CancellationToken.None);
		await _repository.AddCredentialRequirementAsync(eamProfile.Id, "vcsa-ssh", true, CancellationToken.None);
		await _repository.SetBenchmarkReferenceAsync(eamProfile.Id, "VMW_vSphere_8-0_VCSA_EAM_STIG", "v2r3", CancellationToken.None);

		CatalogExecutionProfile postgresProfile = await _repository.CreateExecutionProfileAsync(postgres.Id, release.Id, vcsaGroup.Id, "v2r3", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);
		await _repository.AddCredentialRequirementAsync(postgresProfile.Id, "vsphere-api", true, CancellationToken.None);
		await _repository.AddCredentialRequirementAsync(postgresProfile.Id, "vcsa-ssh", true, CancellationToken.None);

		CatalogExecutionProfileDetail? detail = await _repository.GetExecutionProfileAsync(eamProfile.Id, CancellationToken.None);
		Assert.NotNull(detail);
		Assert.Equal("vsphere", detail!.Product.ProductKey);
		Assert.Equal("8.0.3", detail.ProductVersion.VersionKey);
		Assert.Equal(CatalogTransports.Ssh, detail.Component.Transport);
		Assert.Equal(CatalogSelectorKinds.Service, detail.Component.SelectorKind);
		Assert.Equal("eam", detail.Component.SelectorName);
		Assert.Equal(CatalogKinds.Stig, detail.ContentRelease.Kind);
		Assert.Equal(CatalogOutputKinds.HdfAndCkl, detail.ExecutionProfile.OutputKind);
		Assert.Equal(2, detail.CredentialRequirements.Count);
		Assert.Contains(detail.CredentialRequirements, r => r.Purpose == "vcsa-ssh");
		Assert.NotNull(detail.BenchmarkReference);
		Assert.Equal("VMW_vSphere_8-0_VCSA_EAM_STIG", detail.BenchmarkReference!.BenchmarkKey);

		IReadOnlyList<CatalogComponent> components = await _repository.ListComponentsAsync(version.Id, CancellationToken.None);
		Assert.Equal(3, components.Count);
	}

	[Fact]
	public async Task StigAndSrg_AreDistinctFirstClassKinds_ForTheSameComponent()
	{
		// vSphere 9-0 SRG shape (Y26M05-srg) -- HDF only, no benchmark reference, no CKL.
		Guid sourceRevisionId = await SeedSourceRevisionAsync();
		CatalogProduct product = await _repository.UpsertProductAsync(sourceRevisionId, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion version = await _repository.UpsertProductVersionAsync(product.Id, "9.0.0", "vSphere 9.0", CancellationToken.None);
		CatalogReportGroup srgGroup = await _repository.UpsertReportGroupAsync("srg", "Every SRG", 6, CancellationToken.None);
		CatalogComponent vcenter = await _repository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("vcenter", "vCenter Server", CatalogTransports.VMware, CatalogSelectorKinds.VCenter, null, null), CancellationToken.None);

		CatalogContentRelease srgRelease = await _repository.UpsertContentReleaseAsync(sourceRevisionId, CatalogKinds.Srg, "Y26M05-srg", "vSphere 9.0 SRG Y26M05", CancellationToken.None);
		CatalogExecutionProfile srgProfile = await _repository.CreateExecutionProfileAsync(vcenter.Id, srgRelease.Id, srgGroup.Id, "Y26M05", CatalogOutputKinds.Hdf, CancellationToken.None);

		CatalogExecutionProfileDetail? detail = await _repository.GetExecutionProfileAsync(srgProfile.Id, CancellationToken.None);
		Assert.NotNull(detail);
		Assert.Equal(CatalogKinds.Srg, detail!.ContentRelease.Kind);
		Assert.Equal(CatalogOutputKinds.Hdf, detail.ExecutionProfile.OutputKind);
		Assert.Null(detail.BenchmarkReference);
	}

	[Fact]
	public async Task MultiReleaseComponent_HasIndependentExecutionProfilesPerContentRelease()
	{
		// Issue #728 AC "multi-release components": the same catalog component may be
		// bound to more than one exact content release over time (e.g. a STIG revision
		// bump), and both remain independently queryable -- no in-place mutation.
		Guid sourceRevisionId = await SeedSourceRevisionAsync();
		CatalogProduct product = await _repository.UpsertProductAsync(sourceRevisionId, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion version = await _repository.UpsertProductVersionAsync(product.Id, "8.0.3", "vSphere 8.0 Update 3", CancellationToken.None);
		CatalogReportGroup group = await _repository.UpsertReportGroupAsync("vcenter-stig", "vCenter STIG", 3, CancellationToken.None);
		CatalogComponent vcenter = await _repository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("vcenter", "vCenter Server", CatalogTransports.VMware, CatalogSelectorKinds.VCenter, null, null), CancellationToken.None);

		CatalogContentRelease releaseV2R2 = await _repository.UpsertContentReleaseAsync(sourceRevisionId, CatalogKinds.Stig, "v2r2-stig", "vSphere 8.0 STIG v2r2", CancellationToken.None);
		CatalogContentRelease releaseV2R3 = await _repository.UpsertContentReleaseAsync(sourceRevisionId, CatalogKinds.Stig, "v2r3-stig", "vSphere 8.0 STIG v2r3", CancellationToken.None);

		await _repository.CreateExecutionProfileAsync(vcenter.Id, releaseV2R2.Id, group.Id, "v2r2", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);
		await _repository.CreateExecutionProfileAsync(vcenter.Id, releaseV2R3.Id, group.Id, "v2r3", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);

		IReadOnlyList<CatalogExecutionProfileDetail> profiles = await _repository.ListExecutionProfilesByComponentAsync(vcenter.Id, CancellationToken.None);
		Assert.Equal(2, profiles.Count);
		Assert.Contains(profiles, p => p.ContentRelease.ReleaseKey == "v2r2-stig");
		Assert.Contains(profiles, p => p.ContentRelease.ReleaseKey == "v2r3-stig");
	}

	[Fact]
	public async Task RepeatedLeafNames_AcrossDifferentParents_AreDistinguishedByParentAndProductVersion()
	{
		// Issue #728 AC "repeated leaf names": two different top-level product-version
		// nodes each declare a component with component_key "postgresql" (a VCSA service
		// and, separately, an SDDC Manager service) -- identity must not collide.
		Guid sourceRevisionId = await SeedSourceRevisionAsync();
		CatalogProduct vsphere = await _repository.UpsertProductAsync(sourceRevisionId, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion vsphereVersion = await _repository.UpsertProductVersionAsync(vsphere.Id, "8.0.3", "vSphere 8.0 Update 3", CancellationToken.None);
		CatalogComponent vcsaPostgres = await _repository.UpsertComponentAsync(
			vsphereVersion.Id, new CatalogComponentDefinition("postgresql", "VCSA PostgreSQL Service", CatalogTransports.Ssh, CatalogSelectorKinds.Service, "postgresql", null), CancellationToken.None);

		CatalogProduct vcf = await _repository.UpsertProductAsync(sourceRevisionId, "vmware", "vcf", "VMware Cloud Foundation", CancellationToken.None);
		CatalogProductVersion vcfVersion = await _repository.UpsertProductVersionAsync(vcf.Id, "9.0.0", "VCF 9.0", CancellationToken.None);
		CatalogComponent sddcPostgres = await _repository.UpsertComponentAsync(
			vcfVersion.Id, new CatalogComponentDefinition("postgresql", "SDDC Manager PostgreSQL Service", CatalogTransports.Ssh, CatalogSelectorKinds.Service, "postgresql", null), CancellationToken.None);

		Assert.NotEqual(vcsaPostgres.Id, sddcPostgres.Id);
		Assert.Equal("postgresql", vcsaPostgres.ComponentKey);
		Assert.Equal("postgresql", sddcPostgres.ComponentKey);
		Assert.NotEqual(vcsaPostgres.ProductVersionId, sddcPostgres.ProductVersionId);
	}

	[Fact]
	public async Task RepeatedLeafNames_UnderSameProductVersion_DistinguishedByParentComponent()
	{
		// Two named sub-services with the same leaf key nested under two different parent
		// components in the same product version (a hypothetical nested-service shape) --
		// the (product_version_id, parent_component_id, component_key) unique constraint
		// is what actually prevents collision, not component_key alone.
		Guid sourceRevisionId = await SeedSourceRevisionAsync();
		CatalogProduct product = await _repository.UpsertProductAsync(sourceRevisionId, "vmware", "vcf", "VMware Cloud Foundation", CancellationToken.None);
		CatalogProductVersion version = await _repository.UpsertProductVersionAsync(product.Id, "9.0.0", "VCF 9.0", CancellationToken.None);

		CatalogComponent sddcManager = await _repository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("sddc-manager", "SDDC Manager", CatalogTransports.VcfApi, CatalogSelectorKinds.Service, "sddc-manager", null), CancellationToken.None);
		CatalogComponent operations = await _repository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("operations", "Operations", CatalogTransports.VcfApi, CatalogSelectorKinds.Service, "operations", null), CancellationToken.None);

		CatalogComponent sddcNginx = await _repository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("nginx", "SDDC Manager nginx", CatalogTransports.Ssh, CatalogSelectorKinds.Service, "nginx", sddcManager.Id), CancellationToken.None);
		CatalogComponent operationsNginx = await _repository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("nginx", "Operations Networks nginx platform", CatalogTransports.Ssh, CatalogSelectorKinds.Service, "nginx", operations.Id), CancellationToken.None);

		Assert.NotEqual(sddcNginx.Id, operationsNginx.Id);
		Assert.Equal(sddcManager.Id, sddcNginx.ParentComponentId);
		Assert.Equal(operations.Id, operationsNginx.ParentComponentId);
	}

	[Fact]
	public async Task HistoricalRevisions_ReferencedByExecutionProfile_CannotBeDeleted()
	{
		// Issue #728 AC "referenced historical revisions cannot be deleted accidentally":
		// FK RESTRICT semantics on catalog_content_releases and catalog_components must
		// reject a delete while any execution profile still references them.
		Guid sourceRevisionId = await SeedSourceRevisionAsync();
		CatalogProduct product = await _repository.UpsertProductAsync(sourceRevisionId, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion version = await _repository.UpsertProductVersionAsync(product.Id, "8.0.3", "vSphere 8.0 Update 3", CancellationToken.None);
		CatalogReportGroup group = await _repository.UpsertReportGroupAsync("vcenter-stig", "vCenter STIG", 3, CancellationToken.None);
		CatalogComponent vcenter = await _repository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("vcenter", "vCenter Server", CatalogTransports.VMware, CatalogSelectorKinds.VCenter, null, null), CancellationToken.None);
		CatalogContentRelease release = await _repository.UpsertContentReleaseAsync(sourceRevisionId, CatalogKinds.Stig, "v2r3-stig", "vSphere 8.0 STIG v2r3", CancellationToken.None);
		await _repository.CreateExecutionProfileAsync(vcenter.Id, release.Id, group.Id, "v2r3", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand deleteRelease = new("DELETE FROM catalog_content_releases WHERE id = $1", connection))
		{
			deleteRelease.Parameters.AddWithValue(release.Id);
			await Assert.ThrowsAsync<PostgresException>(() => deleteRelease.ExecuteNonQueryAsync());
		}

		await using (NpgsqlCommand deleteComponent = new("DELETE FROM catalog_components WHERE id = $1", connection))
		{
			deleteComponent.Parameters.AddWithValue(vcenter.Id);
			await Assert.ThrowsAsync<PostgresException>(() => deleteComponent.ExecuteNonQueryAsync());
		}
	}

	[Fact]
	public async Task CreateExecutionProfile_DuplicateComponentAndRelease_ThrowsInsteadOfOverwriting()
	{
		// Execution profiles are immutable identity -- a second attempt to bind the same
		// (component, content release) pair must fail loudly, not silently update history.
		Guid sourceRevisionId = await SeedSourceRevisionAsync();
		CatalogProduct product = await _repository.UpsertProductAsync(sourceRevisionId, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion version = await _repository.UpsertProductVersionAsync(product.Id, "8.0.3", "vSphere 8.0 Update 3", CancellationToken.None);
		CatalogReportGroup group = await _repository.UpsertReportGroupAsync("vcenter-stig", "vCenter STIG", 3, CancellationToken.None);
		CatalogComponent vcenter = await _repository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("vcenter", "vCenter Server", CatalogTransports.VMware, CatalogSelectorKinds.VCenter, null, null), CancellationToken.None);
		CatalogContentRelease release = await _repository.UpsertContentReleaseAsync(sourceRevisionId, CatalogKinds.Stig, "v2r3-stig", "vSphere 8.0 STIG v2r3", CancellationToken.None);
		await _repository.CreateExecutionProfileAsync(vcenter.Id, release.Id, group.Id, "v2r3", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => _repository.CreateExecutionProfileAsync(vcenter.Id, release.Id, group.Id, "v2r3", CatalogOutputKinds.HdfAndCkl, CancellationToken.None));
	}

	[Theory]
	[InlineData("powershell-remoting", "vcenter", null)]
	[InlineData("vmware", "cluster", null)]
	public async Task UnknownTransportOrSelectorVocabulary_FailsClosed_WithActionableError(string transport, string selectorKind, string? selectorName)
	{
		// Issue #728 AC "unknown capability vocabulary fails closed with actionable
		// validation errors" -- caught by CatalogVocabularyValidator before any SQL runs,
		// not surfaced as an opaque Postgres CHECK-constraint violation.
		Guid sourceRevisionId = await SeedSourceRevisionAsync();
		CatalogProduct product = await _repository.UpsertProductAsync(sourceRevisionId, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion version = await _repository.UpsertProductVersionAsync(product.Id, "8.0.3", "vSphere 8.0 Update 3", CancellationToken.None);

		ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => _repository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("bad-component", "Bad Component", transport, selectorKind, selectorName, null), CancellationToken.None));

		Assert.Contains("not in the closed catalog vocabulary", exception.Message);
	}

	[Fact]
	public async Task ServiceSelector_WithoutSelectorName_FailsClosed()
	{
		Guid sourceRevisionId = await SeedSourceRevisionAsync();
		CatalogProduct product = await _repository.UpsertProductAsync(sourceRevisionId, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion version = await _repository.UpsertProductVersionAsync(product.Id, "8.0.3", "vSphere 8.0 Update 3", CancellationToken.None);

		ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => _repository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("eam", "VCSA EAM Service", CatalogTransports.Ssh, CatalogSelectorKinds.Service, null, null), CancellationToken.None));

		Assert.Contains("requires a non-empty selector_name", exception.Message);
	}

	[Fact]
	public async Task UnknownContentKind_FailsClosed()
	{
		Guid sourceRevisionId = await SeedSourceRevisionAsync();

		ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
			() => _repository.UpsertContentReleaseAsync(sourceRevisionId, "checklist", "bogus-release", "Bogus Release", CancellationToken.None));

		Assert.Contains("not in the closed catalog vocabulary", exception.Message);
	}

	[Fact]
	public async Task UnknownCredentialPurpose_FailsClosed()
	{
		Guid sourceRevisionId = await SeedSourceRevisionAsync();
		CatalogProduct product = await _repository.UpsertProductAsync(sourceRevisionId, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion version = await _repository.UpsertProductVersionAsync(product.Id, "8.0.3", "vSphere 8.0 Update 3", CancellationToken.None);
		CatalogReportGroup group = await _repository.UpsertReportGroupAsync("vcenter-stig", "vCenter STIG", 3, CancellationToken.None);
		CatalogComponent vcenter = await _repository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("vcenter", "vCenter Server", CatalogTransports.VMware, CatalogSelectorKinds.VCenter, null, null), CancellationToken.None);
		CatalogContentRelease release = await _repository.UpsertContentReleaseAsync(sourceRevisionId, CatalogKinds.Stig, "v2r3-stig", "vSphere 8.0 STIG v2r3", CancellationToken.None);
		CatalogExecutionProfile profile = await _repository.CreateExecutionProfileAsync(vcenter.Id, release.Id, group.Id, "v2r3", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);

		ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
			() => _repository.AddCredentialRequirementAsync(profile.Id, "domain-admin", true, CancellationToken.None));

		Assert.Contains("not in the closed vocabulary", exception.Message);
	}

	[Fact]
	public async Task RemediationDefinition_RecordsCapabilityWithoutExecuting()
	{
		// Issue #728 AC "remediation ... capability are queryable" -- metadata only, no
		// executable reference (ADR-0013); execution remains epic #15's scope.
		Guid sourceRevisionId = await SeedSourceRevisionAsync();
		CatalogProduct product = await _repository.UpsertProductAsync(sourceRevisionId, "vmware", "photon", "VMware Photon OS", CancellationToken.None);
		CatalogProductVersion version = await _repository.UpsertProductVersionAsync(product.Id, "5.0.0", "Photon OS 5.0", CancellationToken.None);
		CatalogReportGroup group = await _repository.UpsertReportGroupAsync("srg", "Every SRG", 6, CancellationToken.None);
		CatalogComponent photon = await _repository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("photon-os", "Photon OS", CatalogTransports.Ssh, CatalogSelectorKinds.Target, null, null), CancellationToken.None);
		CatalogContentRelease release = await _repository.UpsertContentReleaseAsync(sourceRevisionId, CatalogKinds.Srg, "v3r3-srg", "Photon OS 5.0 SRG v3r3", CancellationToken.None);
		CatalogExecutionProfile profile = await _repository.CreateExecutionProfileAsync(photon.Id, release.Id, group.Id, "v3r3", CatalogOutputKinds.Hdf, CancellationToken.None);

		await _repository.SetRemediationDefinitionAsync(profile.Id, true, "invented fixture: reversible sshd_config remediation", CancellationToken.None);

		CatalogExecutionProfileDetail? detail = await _repository.GetExecutionProfileAsync(profile.Id, CancellationToken.None);
		Assert.NotNull(detail);
		Assert.NotNull(detail!.RemediationDefinition);
		Assert.True(detail.RemediationDefinition!.IsSupported);
	}

	[Fact]
	public async Task WholeApplianceTargetSelector_RoundTripsWithNullSelectorName()
	{
		// Invented fixture shaped like docs/compliance-parity.md's "Aria Operations 8-x /
		// SRG / v1r4-srg" whole-appliance row (`ssh / target`): the component IS the
		// appliance reached over SSH, with NO fabricated sub-service identity -- exactly
		// the "no lossy target-kind inference" the migration header and issue #728 AC
		// require. selector_kind = 'target' therefore carries a NULL selector_name (the
		// selector_name CHECK enforces this: only 'service' may name a sub-service).
		Guid sourceRevisionId = await SeedSourceRevisionAsync();
		CatalogProduct product = await _repository.UpsertProductAsync(sourceRevisionId, "vmware", "aria-operations", "VMware Aria Operations", CancellationToken.None);
		CatalogProductVersion version = await _repository.UpsertProductVersionAsync(product.Id, "8.18.0", "Aria Operations 8.18", CancellationToken.None);
		CatalogReportGroup group = await _repository.UpsertReportGroupAsync("srg", "Every SRG", 6, CancellationToken.None);

		CatalogComponent appliance = await _repository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("aria-operations", "Aria Operations", CatalogTransports.Ssh, CatalogSelectorKinds.Target, null, null), CancellationToken.None);

		Assert.Equal(CatalogSelectorKinds.Target, appliance.SelectorKind);
		Assert.Null(appliance.SelectorName);

		CatalogContentRelease release = await _repository.UpsertContentReleaseAsync(sourceRevisionId, CatalogKinds.Srg, "v1r4-srg", "Aria Operations SRG v1r4", CancellationToken.None);
		CatalogExecutionProfile profile = await _repository.CreateExecutionProfileAsync(appliance.Id, release.Id, group.Id, "v1r4", CatalogOutputKinds.Hdf, CancellationToken.None);
		await _repository.AddCredentialRequirementAsync(profile.Id, "srg-ssh", true, CancellationToken.None);

		CatalogExecutionProfileDetail? detail = await _repository.GetExecutionProfileAsync(profile.Id, CancellationToken.None);
		Assert.NotNull(detail);
		Assert.Equal(CatalogTransports.Ssh, detail!.Component.Transport);
		Assert.Equal(CatalogSelectorKinds.Target, detail.Component.SelectorKind);
		Assert.Null(detail.Component.SelectorName);
		Assert.Equal(CatalogKinds.Srg, detail.ContentRelease.Kind);
		Assert.Equal(CatalogOutputKinds.Hdf, detail.ExecutionProfile.OutputKind);
		Assert.Contains(detail.CredentialRequirements, r => r.Purpose == "srg-ssh");
	}

	[Fact]
	public async Task TargetSelector_WithSelectorName_FailsClosed()
	{
		// Fidelity guard: a whole-appliance 'target' selector must never carry a
		// sub-service name -- inventing one is the "lossy target-kind inference" the
		// migration header and issue #728 AC forbid. Rejected before any SQL runs.
		Guid sourceRevisionId = await SeedSourceRevisionAsync();
		CatalogProduct product = await _repository.UpsertProductAsync(sourceRevisionId, "vmware", "photon", "VMware Photon OS", CancellationToken.None);
		CatalogProductVersion version = await _repository.UpsertProductVersionAsync(product.Id, "5.0.0", "Photon OS 5.0", CancellationToken.None);

		ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => _repository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("photon-os", "Photon OS", CatalogTransports.Ssh, CatalogSelectorKinds.Target, "photon-os", null), CancellationToken.None));

		Assert.Contains("must not carry a selector_name", exception.Message);
	}

	[Fact]
	public async Task ListAllExecutionProfiles_ReturnsEveryProfileAcrossProducts()
	{
		Guid sourceRevisionId = await SeedSourceRevisionAsync();
		CatalogProduct product = await _repository.UpsertProductAsync(sourceRevisionId, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion version = await _repository.UpsertProductVersionAsync(product.Id, "8.0.3", "vSphere 8.0 Update 3", CancellationToken.None);
		CatalogReportGroup group = await _repository.UpsertReportGroupAsync("vcenter-stig", "vCenter STIG", 3, CancellationToken.None);
		CatalogComponent vcenter = await _repository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("vcenter", "vCenter Server", CatalogTransports.VMware, CatalogSelectorKinds.VCenter, null, null), CancellationToken.None);
		CatalogContentRelease release = await _repository.UpsertContentReleaseAsync(sourceRevisionId, CatalogKinds.Stig, "v2r3-stig", "vSphere 8.0 STIG v2r3", CancellationToken.None);
		await _repository.CreateExecutionProfileAsync(vcenter.Id, release.Id, group.Id, "v2r3", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);

		IReadOnlyList<CatalogExecutionProfileDetail> all = await _repository.ListAllExecutionProfilesAsync(CancellationToken.None);
		Assert.Single(all);
	}

	// --- issue #729 persistence slice: declared inputs, import reports, candidate promotion ---

	[Fact]
	public async Task UpsertDeclaredInput_ThenListDeclaredInputs_RoundTrips()
	{
		Guid sourceRevisionId = await SeedSourceRevisionAsync();
		CatalogProduct product = await _repository.UpsertProductAsync(sourceRevisionId, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion version = await _repository.UpsertProductVersionAsync(product.Id, "8.0.3", "vSphere 8.0 Update 3", CancellationToken.None);
		CatalogReportGroup group = await _repository.UpsertReportGroupAsync("vcenter-stig", "vCenter STIG", 3, CancellationToken.None);
		CatalogComponent vcenter = await _repository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("vcenter", "vCenter Server", CatalogTransports.VMware, CatalogSelectorKinds.VCenter, null, null), CancellationToken.None);
		CatalogContentRelease release = await _repository.UpsertContentReleaseAsync(sourceRevisionId, CatalogKinds.Stig, "v2r3-stig", "vSphere 8.0 STIG v2r3", CancellationToken.None);
		CatalogExecutionProfile profile = await _repository.CreateExecutionProfileAsync(vcenter.Id, release.Id, group.Id, "v2r3", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);

		await _repository.UpsertDeclaredInputAsync(profile.Id, "vcenter_host", "string", true, CancellationToken.None);
		await _repository.UpsertDeclaredInputAsync(profile.Id, "vcenter_port", "numeric", false, CancellationToken.None);

		IReadOnlyList<CatalogDeclaredInput> inputs = await _repository.ListDeclaredInputsAsync(profile.Id, CancellationToken.None);
		Assert.Equal(2, inputs.Count);
		Assert.Collection(inputs,
			i => { Assert.Equal("vcenter_host", i.Name); Assert.Equal("string", i.InputType); Assert.True(i.IsRequired); },
			i => { Assert.Equal("vcenter_port", i.Name); Assert.Equal("numeric", i.InputType); Assert.False(i.IsRequired); });

		// Also proves through the full CatalogExecutionProfileDetail join (the wire shape's source).
		CatalogExecutionProfileDetail? detail = await _repository.GetExecutionProfileAsync(profile.Id, CancellationToken.None);
		Assert.Equal(2, detail!.DeclaredInputs.Count);
	}

	[Fact]
	public async Task UpsertDeclaredInput_SameNameTwice_UpdatesInPlace_DoesNotDuplicate()
	{
		Guid sourceRevisionId = await SeedSourceRevisionAsync();
		CatalogProduct product = await _repository.UpsertProductAsync(sourceRevisionId, "vmware", "vsphere", "VMware vSphere", CancellationToken.None);
		CatalogProductVersion version = await _repository.UpsertProductVersionAsync(product.Id, "8.0.3", "vSphere 8.0 Update 3", CancellationToken.None);
		CatalogReportGroup group = await _repository.UpsertReportGroupAsync("vcenter-stig", "vCenter STIG", 3, CancellationToken.None);
		CatalogComponent vcenter = await _repository.UpsertComponentAsync(
			version.Id, new CatalogComponentDefinition("vcenter", "vCenter Server", CatalogTransports.VMware, CatalogSelectorKinds.VCenter, null, null), CancellationToken.None);
		CatalogContentRelease release = await _repository.UpsertContentReleaseAsync(sourceRevisionId, CatalogKinds.Stig, "v2r3-stig", "vSphere 8.0 STIG v2r3", CancellationToken.None);
		CatalogExecutionProfile profile = await _repository.CreateExecutionProfileAsync(vcenter.Id, release.Id, group.Id, "v2r3", CatalogOutputKinds.HdfAndCkl, CancellationToken.None);

		await _repository.UpsertDeclaredInputAsync(profile.Id, "vcenter_host", "string", true, CancellationToken.None);
		await _repository.UpsertDeclaredInputAsync(profile.Id, "vcenter_host", "string", false, CancellationToken.None);

		CatalogDeclaredInput input = Assert.Single(await _repository.ListDeclaredInputsAsync(profile.Id, CancellationToken.None));
		Assert.False(input.IsRequired);
	}

	[Fact]
	public async Task RecordImportReport_ThenRecordEntries_RoundTrips()
	{
		CatalogImportReport report = await _repository.RecordImportReportAsync("commit-a", "digest-a", 2, 1, 1, CancellationToken.None);
		Assert.Equal("commit-a", report.SourceCommit);
		Assert.Equal(2, report.AcceptedCount);

		await _repository.RecordImportReportEntryAsync(report.Id, CatalogImportEntryDispositions.Accepted, "vsphere/8.0/v2r3-stig/inspec/baseline/vcenter", reason: null, executionProfileId: null, CancellationToken.None);
		await _repository.RecordImportReportEntryAsync(report.Id, CatalogImportEntryDispositions.Warning, "vsphere/8.0/v2r3-stig/inspec/baseline/esxi", "no declared inputs", executionProfileId: null, CancellationToken.None);
		await _repository.RecordImportReportEntryAsync(report.Id, CatalogImportEntryDispositions.Rejected, "unknown/1.0/v1-stig/inspec/baseline", "unrecognized vendor family", executionProfileId: null, CancellationToken.None);

		IReadOnlyList<CatalogImportReportEntry> entries = await _repository.ListImportReportEntriesAsync(report.Id, CancellationToken.None);
		Assert.Equal(3, entries.Count);
		Assert.Contains(entries, e => e.Disposition == CatalogImportEntryDispositions.Accepted);
		Assert.Contains(entries, e => e.Disposition == CatalogImportEntryDispositions.Warning && e.Reason == "no declared inputs");
		Assert.Contains(entries, e => e.Disposition == CatalogImportEntryDispositions.Rejected && e.Reason == "unrecognized vendor family");

		IReadOnlyList<CatalogImportReport> reports = await _repository.ListImportReportsAsync(10, CancellationToken.None);
		Assert.Contains(reports, r => r.Id == report.Id);
	}

	[Fact]
	public async Task RecordImportReportEntry_InvalidDisposition_ThrowsBeforeAnySql()
	{
		CatalogImportReport report = await _repository.RecordImportReportAsync("commit-b", "digest-b", 0, 0, 0, CancellationToken.None);

		await Assert.ThrowsAsync<ArgumentException>(() => _repository.RecordImportReportEntryAsync(
			report.Id, "bogus-disposition", "some/profile", null, null, CancellationToken.None));
	}

	/// <summary>
	/// Two distinct pull attempts over byte-identical content are two distinct
	/// provenance events (ADR-0022 "immutable source observations ... will be
	/// retained") -- report headers are never deduplicated even when source_digest
	/// repeats.
	/// </summary>
	[Fact]
	public async Task RecordImportReport_SameDigestTwice_CreatesTwoDistinctReports()
	{
		CatalogImportReport first = await _repository.RecordImportReportAsync("commit-c", "same-digest", 1, 0, 0, CancellationToken.None);
		CatalogImportReport second = await _repository.RecordImportReportAsync("commit-c", "same-digest", 1, 0, 0, CancellationToken.None);

		Assert.NotEqual(first.Id, second.Id);
	}

	private static SemanticCandidate ExecutableLeafCandidate(
		string profileKey = "vsphere/8.0.3/v2r3-stig/inspec/baseline/vcenter",
		string productVersionKey = "8.0.3",
		string kind = "stig",
		string componentKey = "vcenter",
		string transport = "vmware",
		string selectorKind = "vcenter",
		string? manifestVersion = "2.3.0",
		IReadOnlyList<InspecManifestInput>? inputs = null) =>
		new(
			profileKey, "vsphere", productVersionKey, kind, componentKey, "vCenter Server", transport, selectorKind, null,
			IsAggregate: false, Title: "vCenter STIG", ManifestVersion: manifestVersion,
			Inputs: inputs ?? [new InspecManifestInput("vcenter_host", "string", true)],
			Supports: [], Depends: [], ContentDigest: "deadbeef00000000000000000000000000000000000000000000000000000");

	private static CatalogPromotionRequest PromotionRequest() => new(
		SourceRevisionKey: "compliance-content",
		Vendor: "VMware vSphere",
		ProductDisplayName: "VMware vSphere",
		ProductVersionDisplayName: "vSphere 8.0 Update 3",
		ContentReleaseDisplayName: "stig 8.0.3",
		ReportGroupKey: "vcenter-stig",
		ReportGroupDisplayName: "vCenter STIG",
		ReportGroupPriority: 3,
		OutputKind: CatalogOutputKinds.HdfAndCkl);

	[Fact]
	public async Task PromoteCandidate_ExecutableLeaf_CreatesFullIdentityTreeAndDeclaredInputs()
	{
		SemanticCandidate candidate = ExecutableLeafCandidate();

		CatalogPromotionOutcome outcome = await _repository.PromoteCandidateAsync(candidate, PromotionRequest(), CancellationToken.None);

		Assert.NotNull(outcome.ExecutionProfileId);
		Assert.Null(outcome.RejectionReason);

		CatalogExecutionProfileDetail? detail = await _repository.GetExecutionProfileAsync(outcome.ExecutionProfileId!.Value, CancellationToken.None);
		Assert.NotNull(detail);
		Assert.Equal("vsphere", detail!.Product.ProductKey);
		Assert.Equal("8.0.3", detail.ProductVersion.VersionKey);
		Assert.Equal("vcenter", detail.Component.ComponentKey);
		Assert.Equal(CatalogKinds.Stig, detail.ContentRelease.Kind);
		Assert.Equal("2.3.0", detail.ExecutionProfile.ProfileVersion);
		Assert.Single(detail.DeclaredInputs);
		Assert.Equal("vcenter_host", detail.DeclaredInputs[0].Name);
	}

	/// <summary>
	/// Additive-ingestion guarantee (ADR-0022/epic #726 §2, issue #729 deliverable 4):
	/// promoting the SAME candidate identity twice (e.g. a re-import of unchanged
	/// content) deduplicates to the SAME execution profile row rather than creating a
	/// sibling -- active content is never mutated, and re-promotion is a no-op at the
	/// terminal execution-profile level.
	/// </summary>
	[Fact]
	public async Task PromoteCandidate_SameIdentityTwice_DedupesToSameExecutionProfile()
	{
		SemanticCandidate candidate = ExecutableLeafCandidate();
		CatalogPromotionRequest request = PromotionRequest();

		CatalogPromotionOutcome first = await _repository.PromoteCandidateAsync(candidate, request, CancellationToken.None);
		CatalogPromotionOutcome second = await _repository.PromoteCandidateAsync(candidate, request, CancellationToken.None);

		Assert.Equal(first.ExecutionProfileId, second.ExecutionProfileId);

		IReadOnlyList<CatalogExecutionProfileDetail> all = await _repository.ListAllExecutionProfilesAsync(CancellationToken.None);
		Assert.Single(all, d => d.Component.ComponentKey == "vcenter" && d.ContentRelease.Kind == CatalogKinds.Stig);
	}

	/// <summary>
	/// Additive ingestion never mutates active content: promoting a second, DIFFERENT
	/// component under the same product version leaves the first execution profile's
	/// identity/declared inputs completely untouched.
	/// </summary>
	[Fact]
	public async Task PromoteCandidate_SecondDifferentComponent_DoesNotMutateFirstProfile()
	{
		SemanticCandidate first = ExecutableLeafCandidate();
		CatalogPromotionOutcome firstOutcome = await _repository.PromoteCandidateAsync(first, PromotionRequest(), CancellationToken.None);

		SemanticCandidate second = ExecutableLeafCandidate(
			profileKey: "vsphere/8.0.3/v2r3-stig/inspec/baseline/esxi", componentKey: "esxi", selectorKind: "esxi",
			inputs: [new InspecManifestInput("esxi_host", "string", true)]);
		await _repository.PromoteCandidateAsync(second, PromotionRequest() with { ReportGroupKey = "esxi-stig", ReportGroupDisplayName = "ESXi STIG", ReportGroupPriority = 4 }, CancellationToken.None);

		CatalogExecutionProfileDetail? firstDetail = await _repository.GetExecutionProfileAsync(firstOutcome.ExecutionProfileId!.Value, CancellationToken.None);
		Assert.NotNull(firstDetail);
		Assert.Equal("vcenter", firstDetail!.Component.ComponentKey);
		Assert.Single(firstDetail.DeclaredInputs);
		Assert.Equal("vcenter_host", firstDetail.DeclaredInputs[0].Name);

		IReadOnlyList<CatalogExecutionProfileDetail> all = await _repository.ListAllExecutionProfilesAsync(CancellationToken.None);
		Assert.Equal(2, all.Count);
	}

	[Fact]
	public async Task PromoteCandidate_AggregateCandidate_IsRejected_NeverPromoted()
	{
		SemanticCandidate aggregate = ExecutableLeafCandidate() with { ComponentKey = "aggregate", IsAggregate = true };

		CatalogPromotionOutcome outcome = await _repository.PromoteCandidateAsync(aggregate, PromotionRequest(), CancellationToken.None);

		Assert.Null(outcome.ExecutionProfileId);
		Assert.NotNull(outcome.RejectionReason);
		Assert.Contains("aggregate", outcome.RejectionReason, StringComparison.OrdinalIgnoreCase);

		IReadOnlyList<CatalogExecutionProfileDetail> all = await _repository.ListAllExecutionProfilesAsync(CancellationToken.None);
		Assert.Empty(all);
	}

	[Fact]
	public async Task PromoteCandidate_UnknownVocabulary_FailsClosed_WithoutPromoting()
	{
		SemanticCandidate badTransport = ExecutableLeafCandidate() with { Transport = "bogus-transport" };

		CatalogPromotionOutcome outcome = await _repository.PromoteCandidateAsync(badTransport, PromotionRequest(), CancellationToken.None);

		Assert.Null(outcome.ExecutionProfileId);
		Assert.NotNull(outcome.RejectionReason);
		Assert.Contains("bogus-transport", outcome.RejectionReason);

		IReadOnlyList<CatalogExecutionProfileDetail> all = await _repository.ListAllExecutionProfilesAsync(CancellationToken.None);
		Assert.Empty(all);
	}
}
