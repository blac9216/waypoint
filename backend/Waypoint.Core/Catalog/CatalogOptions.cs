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

namespace Waypoint.Core.Catalog;

/// <summary>
/// Configuration for the <c>catalog-index</c> handler (issue #194, epic #9 slice 2).
/// </summary>
public sealed class CatalogOptions
{
	public const string SectionName = "Catalog";

	/// <summary>Root directory of the offline depot share to index.</summary>
	public string DepotPath { get; set; } = "/vcf";

	/// <summary>
	/// The well-known <c>credentials.credential_type</c> value identifying the single
	/// stored Broadcom depot token (docs/roadmap.md: the M1 secrets store "initially
	/// hold[s] just the Broadcom depot token" -- one credential row, looked up by type
	/// rather than carried on the job, since <c>POST /catalog/sync</c> fans the
	/// <c>catalog-index</c> job out with <c>CredentialId: null</c> -- catalog-index has
	/// no per-target credential the way a scan or download job does).
	/// </summary>
	public string DepotTokenCredentialType { get; set; } = "depot-token";
}
