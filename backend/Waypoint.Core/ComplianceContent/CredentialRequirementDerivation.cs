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

using Waypoint.Core.Secrets;

namespace Waypoint.Core.ComplianceContent;

/// <summary>
/// Issue #1012: derives the closed <see cref="CredentialPurposes"/> set an execution
/// profile's (product family, transport, selector kind) shape implies, so an
/// importer-promoted profile (<see cref="ICatalogRepository.PromoteCandidateAsync"/>)
/// carries the same <c>catalog_credential_requirements</c> a hand-curated seed row for
/// the identical shape would carry -- root cause: seed migrations 0064/0067/0069 are
/// the ONLY writers of that table, so an imported profile was created with ZERO
/// requirements, the planner derived an empty <c>RequiredPurposes</c>, and every scan
/// job fanned out with no credential and no preview-time gap.
///
/// This is a literal C# port of 0064/0067/0069's own
/// <c>catalog_credential_requirements</c> <c>CROSS JOIN LATERAL</c> derivation --
/// docs/compliance-parity.md's "Sibling source-capability provenance matrix" Purpose
/// column is the single documented authority both the seed SQL and this class must
/// agree with; <see cref="Waypoint.Tests.Core.ComplianceContent.CredentialRequirementDerivationDriftGuardTests"/>
/// parses the doc directly and proves this method's output for each documented
/// (product, transport, selector) shape matches the doc-derived expectation, and a
/// separate real-Postgres test proves it matches what the seed migrations actually
/// wrote for the SAME shape -- so seed SQL, this derivation, and the doc cannot drift
/// apart from each other again the way 0064's credential rows silently diverged from
/// the importer's promotion path before this issue.
///
/// Fail-closed (issue #1012 AC): an unmapped (product family, transport, selector kind)
/// combination derives NO purposes at all -- this class never invents a purpose for a
/// shape the doc does not document. A caller (<see cref="ICatalogRepository.PromoteCandidateAsync"/>)
/// that gets an empty result for a transport docs/compliance-parity.md's closed
/// vocabulary says SHOULD need a credential (<see cref="CatalogTransports.VMware"/>,
/// <see cref="CatalogTransports.NsxApi"/>, or an <see cref="CatalogTransports.Ssh"/>
/// component whose selector is not a bare unmapped shape) is a promotion-time
/// vocabulary gap, not silently promotable -- see that method's own handling.
/// </summary>
public static class CredentialRequirementDerivation
{
	/// <summary>
	/// Derives the required credential purposes for one execution profile's shape,
	/// mirroring migration 0067's (the most complete, most recently authored) exact
	/// <c>CROSS JOIN LATERAL</c> rule set:
	/// <list type="bullet">
	/// <item><description><see cref="CatalogTransports.VMware"/> (any product) -&gt; <see cref="CredentialPurposes.VSphereApi"/> (vCenter/ESXi/VM object-kind rows).</description></item>
	/// <item><description><c>vsphere</c> family, <see cref="CatalogTransports.Ssh"/>, <see cref="CatalogSelectorKinds.Service"/> (VCSA named sub-services) -&gt; BOTH <see cref="CredentialPurposes.VSphereApi"/> AND <see cref="CredentialPurposes.VcsaSsh"/>.</description></item>
	/// <item><description><see cref="CatalogTransports.NsxApi"/> (any product) -&gt; <see cref="CredentialPurposes.NsxApi"/>.</description></item>
	/// <item><description><see cref="CatalogTransports.Ssh"/> + <see cref="CatalogSelectorKinds.Target"/> (whole-appliance: Aria/vIDM/Photon) -&gt; <see cref="CredentialPurposes.SrgSsh"/>.</description></item>
	/// <item><description><c>vcf</c> family, <see cref="CatalogTransports.Ssh"/>, <see cref="CatalogSelectorKinds.Service"/> (VCF named appliance services, NOT vCenter-managed) -&gt; <see cref="CredentialPurposes.SrgSsh"/> only (no <see cref="CredentialPurposes.VSphereApi"/>, unlike the vsphere VCSA row above).</description></item>
	/// <item><description><see cref="CatalogTransports.VcfApi"/> (any product) -&gt; <see cref="CredentialPurposes.VcfApi"/> (ADR-0024 / issue #977).</description></item>
	/// </list>
	/// Every other (family, transport, selector) combination -- including an
	/// <see cref="CatalogTransports.Ssh"/> + <see cref="CatalogSelectorKinds.Service"/>
	/// component under a family that is neither <c>vsphere</c> nor <c>vcf</c> -- derives
	/// an EMPTY set: fail-closed, never a guessed purpose (issue #1012 AC).
	/// </summary>
	public static IReadOnlyList<string> DeriveRequiredPurposes(string productFamily, string transport, string selectorKind)
	{
		ArgumentNullException.ThrowIfNull(productFamily);
		ArgumentNullException.ThrowIfNull(transport);
		ArgumentNullException.ThrowIfNull(selectorKind);

		List<string> purposes = [];

		if (string.Equals(transport, CatalogTransports.VMware, StringComparison.Ordinal))
		{
			purposes.Add(CredentialPurposes.VSphereApi);
		}

		if (string.Equals(transport, CatalogTransports.Ssh, StringComparison.Ordinal)
			&& string.Equals(selectorKind, CatalogSelectorKinds.Service, StringComparison.Ordinal)
			&& string.Equals(productFamily, "vsphere", StringComparison.Ordinal))
		{
			purposes.Add(CredentialPurposes.VSphereApi);
			purposes.Add(CredentialPurposes.VcsaSsh);
		}

		if (string.Equals(transport, CatalogTransports.NsxApi, StringComparison.Ordinal))
		{
			purposes.Add(CredentialPurposes.NsxApi);
		}

		if (string.Equals(transport, CatalogTransports.Ssh, StringComparison.Ordinal)
			&& string.Equals(selectorKind, CatalogSelectorKinds.Target, StringComparison.Ordinal))
		{
			purposes.Add(CredentialPurposes.SrgSsh);
		}

		if (string.Equals(transport, CatalogTransports.Ssh, StringComparison.Ordinal)
			&& string.Equals(selectorKind, CatalogSelectorKinds.Service, StringComparison.Ordinal)
			&& string.Equals(productFamily, "vcf", StringComparison.Ordinal))
		{
			purposes.Add(CredentialPurposes.SrgSsh);
		}

		if (string.Equals(transport, CatalogTransports.VcfApi, StringComparison.Ordinal))
		{
			purposes.Add(CredentialPurposes.VcfApi);
		}

		return purposes.Count == 0 ? [] : [.. purposes.Distinct(StringComparer.Ordinal)];
	}
}
