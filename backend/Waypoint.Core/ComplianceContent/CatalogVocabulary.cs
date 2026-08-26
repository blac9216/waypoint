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

namespace Waypoint.Core.ComplianceContent;

/// <summary>
/// The closed content-kind vocabulary (migration 0050's <c>catalog_content_releases</c>
/// CHECK constraint). Issue #728 AC: "STIG and SRG content are distinct first-class
/// kinds" -- never inferred from a path or leaf name.
/// </summary>
public static class CatalogKinds
{
	public const string Stig = "stig";
	public const string Srg = "srg";

	public static readonly IReadOnlyCollection<string> All = [Stig, Srg];

	public static bool IsValid(string? kind) => kind is not null && All.Contains(kind);
}

/// <summary>
/// The closed transport vocabulary (migration 0050's <c>catalog_components</c> CHECK
/// constraint; docs/compliance-parity.md "Closed capability vocabulary" table).
/// </summary>
public static class CatalogTransports
{
	public const string VMware = "vmware";
	public const string Ssh = "ssh";
	public const string NsxApi = "nsx-api";
	public const string VcfApi = "vcf-api";

	public static readonly IReadOnlyCollection<string> All = [VMware, Ssh, NsxApi, VcfApi];

	public static bool IsValid(string? transport) => transport is not null && All.Contains(transport);
}

/// <summary>
/// The closed selector-kind vocabulary (migration 0050's <c>catalog_components</c>
/// CHECK constraint). <c>Service</c> is the named-sub-service selector (VCSA EAM,
/// SDDC Manager nginx, NSX Manager, ...) and requires a non-null
/// <see cref="CatalogComponent.SelectorName"/>; the other three are the generic
/// vSphere object-kind selectors and never carry a selector name.
/// </summary>
public static class CatalogSelectorKinds
{
	public const string VCenter = "vcenter";
	public const string Esxi = "esxi";
	public const string Vm = "vm";
	public const string Service = "service";

	public static readonly IReadOnlyCollection<string> All = [VCenter, Esxi, Vm, Service];

	public static bool IsValid(string? selectorKind) => selectorKind is not null && All.Contains(selectorKind);
}

/// <summary>
/// The closed execution-profile output-semantics vocabulary (migration 0050's
/// <c>catalog_execution_profiles</c> CHECK constraint; docs/compliance-parity.md
/// "Output" row): STIG profiles produce complete HDF + CKL (upload-eligible); SRG
/// profiles produce HDF only and are never CKL/upload-eligible.
/// </summary>
public static class CatalogOutputKinds
{
	public const string Hdf = "hdf";
	public const string HdfAndCkl = "hdf_ckl";

	public static readonly IReadOnlyCollection<string> All = [Hdf, HdfAndCkl];

	public static bool IsValid(string? outputKind) => outputKind is not null && All.Contains(outputKind);
}

/// <summary>
/// Fail-closed validation for the catalog's closed capability vocabulary (issue #728
/// AC: "Unknown capability vocabulary fails closed with actionable validation
/// errors"). Every catalog write path (seeding, and any future #729/#730 candidate
/// promotion) must run values through this validator before they reach storage --
/// the database CHECK constraints in migration 0050 are the last line of defense, not
/// the primary one, since a CHECK violation is a generic Postgres error, not an
/// actionable message naming which field/value failed.
/// </summary>
public static class CatalogVocabularyValidator
{
	/// <summary>
	/// Validates one candidate <see cref="CatalogComponentDefinition"/> against the
	/// closed vocabulary. Returns every violation found (not just the first) so a
	/// caller can report all problems in one pass, mirroring how validation errors are
	/// typically surfaced as a list rather than one-at-a-time.
	/// </summary>
	public static IReadOnlyList<string> ValidateComponent(string transport, string selectorKind, string? selectorName)
	{
		List<string> errors = [];

		if (!CatalogTransports.IsValid(transport))
		{
			errors.Add($"transport '{transport}' is not in the closed catalog vocabulary ({string.Join(", ", CatalogTransports.All)})");
		}

		if (!CatalogSelectorKinds.IsValid(selectorKind))
		{
			errors.Add($"selector_kind '{selectorKind}' is not in the closed catalog vocabulary ({string.Join(", ", CatalogSelectorKinds.All)})");
		}
		else if (selectorKind == CatalogSelectorKinds.Service && string.IsNullOrWhiteSpace(selectorName))
		{
			errors.Add("selector_kind 'service' requires a non-empty selector_name");
		}
		else if (selectorKind != CatalogSelectorKinds.Service && !string.IsNullOrWhiteSpace(selectorName))
		{
			errors.Add($"selector_kind '{selectorKind}' must not carry a selector_name");
		}

		return errors;
	}

	/// <summary>Validates a candidate content <paramref name="kind"/> (stig|srg).</summary>
	public static IReadOnlyList<string> ValidateKind(string kind) =>
		CatalogKinds.IsValid(kind)
			? []
			: [$"kind '{kind}' is not in the closed catalog vocabulary ({string.Join(", ", CatalogKinds.All)})"];

	/// <summary>Validates a candidate execution-profile <paramref name="outputKind"/>.</summary>
	public static IReadOnlyList<string> ValidateOutputKind(string outputKind) =>
		CatalogOutputKinds.IsValid(outputKind)
			? []
			: [$"output_kind '{outputKind}' is not in the closed catalog vocabulary ({string.Join(", ", CatalogOutputKinds.All)})"];

	/// <summary>
	/// Validates a candidate credential <paramref name="purpose"/> against the shared
	/// <see cref="Waypoint.Core.Secrets.CredentialPurposes"/> closed set -- the catalog
	/// does not maintain a parallel purpose vocabulary (migration 0050's
	/// <c>catalog_credential_requirements</c> CHECK constraint mirrors the same set).
	/// </summary>
	public static IReadOnlyList<string> ValidateCredentialPurpose(string purpose) =>
		Waypoint.Core.Secrets.CredentialPurposes.IsValid(purpose)
			? []
			: [$"credential purpose '{purpose}' is not in the closed vocabulary ({string.Join(", ", Waypoint.Core.Secrets.CredentialPurposes.All)})"];
}
