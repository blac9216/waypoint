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

namespace Waypoint.Core.Catalog;

/// <summary>
/// Configuration for the <c>catalog-index</c> handler (issue #194, epic #9 slice 2)
/// and the depot-fetch tool-install path (<c>ManagedToolInstallJobHandler</c>).
/// </summary>
public sealed class CatalogOptions
{
	public const string SectionName = "Catalog";

	/// <summary>Root directory of the offline depot share to index.</summary>
	public string DepotPath { get; set; } = "/vcf";

	/// <summary>
	/// Issue #690: the well-known <c>credentials.credential_type</c> value identifying
	/// the VCF 9.1 Software Depot Activation Code (<see cref="CredentialTypes.DepotActivationCode"/>)
	/// that the connected-mode depot-fetch tool-install path
	/// (<c>ManagedToolInstallJobHandler.ExecuteDepotFetchAsync</c>) resolves to
	/// authenticate <c>vcf-download-tool</c> commands. Local catalog re-index
	/// (<c>CatalogIndexJobHandler</c>) no longer resolves or decrypts any credential
	/// at all (issue #690 AC) -- the offline indexing walk is a pure filesystem read
	/// (docs/domain-model.md open question 4) that never needed one.
	/// </summary>
	public string DepotActivationCodeCredentialType { get; set; } = CredentialTypes.DepotActivationCode;
}
