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
using Xunit;

namespace Waypoint.Tests.Core.ComplianceContent;

/// <summary>
/// Pure unit coverage for the closed capability vocabulary (issue #728 AC: "Unknown
/// capability vocabulary fails closed with actionable validation errors") -- no
/// Postgres dependency; <c>CatalogRepositoryTests</c> covers the same fail-closed
/// behavior end to end through the repository.
/// </summary>
public sealed class CatalogVocabularyValidatorTests
{
	[Theory]
	[InlineData(CatalogTransports.VMware)]
	[InlineData(CatalogTransports.Ssh)]
	[InlineData(CatalogTransports.NsxApi)]
	[InlineData(CatalogTransports.VcfApi)]
	public void ValidateComponent_KnownTransport_WithCompatibleSelector_NoErrors(string transport)
	{
		Assert.Empty(CatalogVocabularyValidator.ValidateComponent(transport, CatalogSelectorKinds.VCenter, null));
	}

	[Fact]
	public void ValidateComponent_UnknownTransport_ReturnsActionableError()
	{
		IReadOnlyList<string> errors = CatalogVocabularyValidator.ValidateComponent("winrm", CatalogSelectorKinds.VCenter, null);

		Assert.Single(errors);
		Assert.Contains("winrm", errors[0]);
		Assert.Contains("not in the closed catalog vocabulary", errors[0]);
	}

	[Fact]
	public void ValidateComponent_UnknownSelectorKind_ReturnsActionableError()
	{
		IReadOnlyList<string> errors = CatalogVocabularyValidator.ValidateComponent(CatalogTransports.VMware, "datacenter", null);

		Assert.Single(errors);
		Assert.Contains("datacenter", errors[0]);
	}

	[Fact]
	public void ValidateComponent_BothUnknown_ReturnsBothErrors()
	{
		IReadOnlyList<string> errors = CatalogVocabularyValidator.ValidateComponent("winrm", "datacenter", null);

		Assert.Equal(2, errors.Count);
	}

	[Fact]
	public void ValidateComponent_ServiceSelector_RequiresSelectorName()
	{
		IReadOnlyList<string> errors = CatalogVocabularyValidator.ValidateComponent(CatalogTransports.Ssh, CatalogSelectorKinds.Service, null);

		Assert.Single(errors);
		Assert.Contains("requires a non-empty selector_name", errors[0]);
	}

	[Fact]
	public void ValidateComponent_ServiceSelector_WithSelectorName_NoErrors()
	{
		Assert.Empty(CatalogVocabularyValidator.ValidateComponent(CatalogTransports.Ssh, CatalogSelectorKinds.Service, "eam"));
	}

	[Fact]
	public void ValidateComponent_NonServiceSelector_MustNotCarrySelectorName()
	{
		IReadOnlyList<string> errors = CatalogVocabularyValidator.ValidateComponent(CatalogTransports.VMware, CatalogSelectorKinds.VCenter, "unexpected");

		Assert.Single(errors);
		Assert.Contains("must not carry a selector_name", errors[0]);
	}

	[Theory]
	[InlineData(CatalogKinds.Stig)]
	[InlineData(CatalogKinds.Srg)]
	public void ValidateKind_KnownKinds_NoErrors(string kind)
	{
		Assert.Empty(CatalogVocabularyValidator.ValidateKind(kind));
	}

	[Fact]
	public void ValidateKind_UnknownKind_FailsClosedWithActionableError()
	{
		IReadOnlyList<string> errors = CatalogVocabularyValidator.ValidateKind("checklist");

		Assert.Single(errors);
		Assert.Contains("checklist", errors[0]);
		Assert.Contains(CatalogKinds.Stig, errors[0]);
		Assert.Contains(CatalogKinds.Srg, errors[0]);
	}

	[Fact]
	public void ValidateOutputKind_UnknownValue_FailsClosed()
	{
		Assert.Single(CatalogVocabularyValidator.ValidateOutputKind("ckl_only"));
	}

	[Fact]
	public void ValidateCredentialPurpose_UnknownValue_FailsClosed()
	{
		IReadOnlyList<string> errors = CatalogVocabularyValidator.ValidateCredentialPurpose("domain-admin");

		Assert.Single(errors);
		Assert.Contains("domain-admin", errors[0]);
	}

	[Theory]
	[InlineData("vsphere-api")]
	[InlineData("vcsa-ssh")]
	[InlineData("nsx-api")]
	[InlineData("srg-ssh")]
	[InlineData("vcf-api")]
	public void ValidateCredentialPurpose_KnownValues_NoErrors(string purpose)
	{
		Assert.Empty(CatalogVocabularyValidator.ValidateCredentialPurpose(purpose));
	}
}
