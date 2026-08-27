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
using Waypoint.Core.Sites;
using Xunit;

namespace Waypoint.Tests.Core.Secrets;

/// <summary>
/// Completeness tests for ADR-0021's credential-purpose matrix (issue #583). This is a
/// design/contracts slice -- these tests assert the DATA is internally consistent and
/// covers every current target kind/operation; nothing here exercises runtime
/// consumption, because nothing outside this matrix consumes it yet.
/// </summary>
public class CredentialPurposeMatrixTests
{
	[Fact]
	public void VSphereApi_And_VcsaSsh_Are_Distinct_Purposes()
	{
		// ADR-0021's headline acceptance criterion (issue #583): vSphere API and VCSA
		// SSH must never collapse into one purpose.
		Assert.NotEqual(CredentialPurposes.VSphereApi, CredentialPurposes.VcsaSsh);
	}

	[Fact]
	public void Discovery_Requires_Only_VSphereApi_Never_VcsaSsh()
	{
		CredentialPurposeMatrixEntry discovery = Assert.Single(
			CredentialPurposeMatrix.Entries,
			e => e.TargetKind == TargetKinds.VSphere && e.Operation == CredentialPurposeOperations.Discovery);

		Assert.Equal([CredentialPurposes.VSphereApi], discovery.RequiredPurposes);
		Assert.DoesNotContain(CredentialPurposes.VcsaSsh, discovery.RequiredPurposes);
		Assert.DoesNotContain(CredentialPurposes.VcsaSsh, discovery.OptionalPurposes);
	}

	[Fact]
	public void Only_VSphere_Has_A_Discovery_Entry()
	{
		// nsx-api and ssh targets have no discovery operation today -- only vsphere is
		// inventory-capable (mirrors frontend's INVENTORY_CAPABLE_TARGET_KINDS).
		var discoveryKinds = CredentialPurposeMatrix.Entries
			.Where(e => e.Operation == CredentialPurposeOperations.Discovery)
			.Select(e => e.TargetKind)
			.Distinct()
			.ToArray();

		Assert.Equal([TargetKinds.VSphere], discoveryKinds);
	}

	[Fact]
	public void Every_Target_Kind_Has_At_Least_One_Matrix_Entry()
	{
		var coveredKinds = CredentialPurposeMatrix.Entries.Select(e => e.TargetKind).ToHashSet();

		foreach (string kind in TargetKinds.All)
		{
			Assert.Contains(kind, coveredKinds);
		}
	}

	[Theory]
	[InlineData(TargetKinds.VSphere)]
	[InlineData(TargetKinds.NsxApi)]
	[InlineData(TargetKinds.Ssh)]
	public void Every_Target_Kind_Has_CredentialTest_Scan_And_RemediationPlanning_Entries(string kind)
	{
		var operationsForKind = CredentialPurposeMatrix.Entries
			.Where(e => e.TargetKind == kind)
			.Select(e => e.Operation)
			.ToHashSet();

		Assert.Contains(CredentialPurposeOperations.CredentialTest, operationsForKind);
		Assert.Contains(CredentialPurposeOperations.Scan, operationsForKind);
		Assert.Contains(CredentialPurposeOperations.RemediationReadyPlanning, operationsForKind);
	}

	[Fact]
	public void VSphere_Scan_Has_One_Entry_Per_Component()
	{
		var components = CredentialPurposeMatrix.Entries
			.Where(e => e.TargetKind == TargetKinds.VSphere && e.Operation == CredentialPurposeOperations.Scan)
			.Select(e => e.Component)
			.ToHashSet();

		Assert.Equal(new HashSet<string?> { "vcenter", "esxi", "vm", "vcsa" }, components);
	}

	[Fact]
	public void VSphere_Vcsa_Scan_Component_Requires_Both_VSphereApi_And_VcsaSsh()
	{
		CredentialPurposeMatrixEntry vcsaScan = Assert.Single(
			CredentialPurposeMatrix.Entries,
			e => e.TargetKind == TargetKinds.VSphere && e.Operation == CredentialPurposeOperations.Scan && e.Component == "vcsa");

		Assert.Equal(
			new HashSet<string> { CredentialPurposes.VSphereApi, CredentialPurposes.VcsaSsh },
			vcsaScan.RequiredPurposes.ToHashSet());
	}

	[Fact]
	public void Every_Purpose_Referenced_By_The_Matrix_Is_In_The_Closed_Set()
	{
		var referencedPurposes = CredentialPurposeMatrix.Entries
			.SelectMany(e => e.RequiredPurposes.Concat(e.OptionalPurposes))
			.Distinct();

		foreach (string purpose in referencedPurposes)
		{
			Assert.True(CredentialPurposes.IsValid(purpose), $"'{purpose}' is referenced by the matrix but not in CredentialPurposes.All.");
		}
	}

	[Fact]
	public void Every_Closed_Set_Purpose_Is_Referenced_By_At_Least_One_Matrix_Entry()
	{
		var referencedPurposes = CredentialPurposeMatrix.Entries
			.SelectMany(e => e.RequiredPurposes.Concat(e.OptionalPurposes))
			.ToHashSet();

		foreach (string purpose in CredentialPurposes.All)
		{
			// Issue #977: VcfApi is catalog-only today (ADR-0024's resolved vcf-api
			// credential purpose) -- no Waypoint.Core.Sites.TargetKinds value exists for
			// the vcf-api transport yet, so this target-kind x operation matrix has no
			// entry to reference it. It is exempted here, not added with a fabricated
			// matrix row.
			if (purpose == CredentialPurposes.VcfApi)
			{
				continue;
			}

			Assert.Contains(purpose, referencedPurposes);
		}
	}

	[Fact]
	public void Every_Purpose_Has_At_Least_One_Satisfying_Credential_Type()
	{
		foreach (string purpose in CredentialPurposes.All)
		{
			// Issue #977: see the exemption note above -- VcfApi has no target kind and
			// therefore no credential-type binding in this matrix yet.
			if (purpose == CredentialPurposes.VcfApi)
			{
				continue;
			}

			Assert.True(
				CredentialPurposeMatrix.SatisfyingCredentialTypes.TryGetValue(purpose, out IReadOnlyCollection<string>? types) && types.Count > 0,
				$"Purpose '{purpose}' has no satisfying credential type(s) recorded.");
		}
	}

	[Fact]
	public void VcfApi_Is_A_Distinct_Purpose_From_Every_Other_Purpose()
	{
		// ADR-0024: "a distinct compatible purpose for catalog-declared vcf-api work" --
		// mirrors VSphereApi_And_VcsaSsh_Are_Distinct_Purposes' headline shape for the
		// new purpose issue #977 adds.
		Assert.DoesNotContain(CredentialPurposes.VcfApi, new[] { CredentialPurposes.VSphereApi, CredentialPurposes.VcsaSsh, CredentialPurposes.NsxApi, CredentialPurposes.SrgSsh });
	}

	[Fact]
	public void Every_Satisfying_Credential_Type_Is_In_The_Closed_CredentialTypes_Set()
	{
		foreach (IReadOnlyCollection<string> types in CredentialPurposeMatrix.SatisfyingCredentialTypes.Values)
		{
			foreach (string type in types)
			{
				Assert.True(CredentialTypes.IsValid(type), $"'{type}' is recorded as a satisfying credential type but is not in CredentialTypes.All.");
			}
		}
	}

	[Fact]
	public void Every_Matrix_Entry_Has_At_Least_One_Required_Purpose()
	{
		// Every operation this matrix covers has always needed at least one purpose so
		// far -- an entry with zero required purposes would be a no-op row and likely a
		// typo, not a legitimate "nothing needed" state.
		foreach (CredentialPurposeMatrixEntry entry in CredentialPurposeMatrix.Entries)
		{
			Assert.True(entry.RequiredPurposes.Count > 0, $"{entry.TargetKind}/{entry.Operation}/{entry.Component} has no required purposes.");
		}
	}

	[Fact]
	public void CredentialPurposes_IsValid_Rejects_Unknown_And_Null()
	{
		Assert.False(CredentialPurposes.IsValid(null));
		Assert.False(CredentialPurposes.IsValid("slot-1"));
		Assert.False(CredentialPurposes.IsValid(string.Empty));
	}

	[Theory]
	[InlineData(CredentialPurposes.VSphereApi)]
	[InlineData(CredentialPurposes.VcsaSsh)]
	[InlineData(CredentialPurposes.NsxApi)]
	[InlineData(CredentialPurposes.SrgSsh)]
	[InlineData(CredentialPurposes.VcfApi)]
	public void CredentialPurposes_IsValid_Accepts_Every_Closed_Set_Value(string purpose)
	{
		Assert.True(CredentialPurposes.IsValid(purpose));
	}

	/// <summary>
	/// Issue #584: every target kind maps to exactly one default purpose -- the one
	/// migration 0043's data-migration/dual-write logic mirrors
	/// <c>targets.credential_id</c> into. This is the purpose required
	/// unconditionally by every operation row for that kind (never the VCSA-only
	/// <c>vcsa-ssh</c> purpose, which is optional-until-selected).
	/// </summary>
	[Theory]
	[InlineData(TargetKinds.VSphere, CredentialPurposes.VSphereApi)]
	[InlineData(TargetKinds.NsxApi, CredentialPurposes.NsxApi)]
	[InlineData(TargetKinds.Ssh, CredentialPurposes.SrgSsh)]
	public void DefaultPurposeByTargetKind_MapsEachKindToItsUnconditionallyRequiredPurpose(string kind, string expectedDefault)
	{
		Assert.True(CredentialPurposeMatrix.DefaultPurposeByTargetKind.TryGetValue(kind, out string? actual));
		Assert.Equal(expectedDefault, actual);
	}

	[Fact]
	public void DefaultPurposeByTargetKind_CoversEveryTargetKind()
	{
		foreach (string kind in TargetKinds.All)
		{
			Assert.True(CredentialPurposeMatrix.DefaultPurposeByTargetKind.ContainsKey(kind), $"'{kind}' has no default purpose mapping.");
		}
	}

	[Fact]
	public void ApplicablePurposes_VSphere_IncludesBothVSphereApiAndVcsaSsh()
	{
		IReadOnlyCollection<string> applicable = CredentialPurposeMatrix.ApplicablePurposes(TargetKinds.VSphere);

		Assert.Contains(CredentialPurposes.VSphereApi, applicable);
		Assert.Contains(CredentialPurposes.VcsaSsh, applicable);
		Assert.DoesNotContain(CredentialPurposes.NsxApi, applicable);
		Assert.DoesNotContain(CredentialPurposes.SrgSsh, applicable);
	}

	[Fact]
	public void ApplicablePurposes_NsxApi_IsExactlyNsxApi()
	{
		Assert.Equal([CredentialPurposes.NsxApi], CredentialPurposeMatrix.ApplicablePurposes(TargetKinds.NsxApi));
	}

	[Fact]
	public void ApplicablePurposes_Ssh_IsExactlySrgSsh()
	{
		Assert.Equal([CredentialPurposes.SrgSsh], CredentialPurposeMatrix.ApplicablePurposes(TargetKinds.Ssh));
	}
}
