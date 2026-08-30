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

namespace Waypoint.Core.Jobs;

/// <summary>
/// The exact string values of <c>runs.run_type</c>, matching the authoritative
/// <c>runs_run_type_check</c> as of <c>0048_depot_enrollment_state.sql</c> (the latest
/// migration to touch it -- 0001 declared 11 values, 0042 added <c>credential-test</c>,
/// <c>tool-install</c>, and <c>purge</c>, 0048 added <c>depot-enrollment</c>, 0099 added
/// <c>binaries-download</c>) verbatim -- this is the closed set. Issue
/// #708's <c>GET /runs/history</c> validates its <c>run_type</c> filter against this set
/// (400 on an unknown value, same "never silently match zero rows and look like empty
/// history" posture <see cref="JobEventTypes"/>'s <c>kind</c> filter established).
/// <see cref="Waypoint.Tests.Core.Jobs.RunTypesConstraintDriftTests"/> asserts this list
/// stays byte-identical to the CHECK constraint the migrations produce.
/// </summary>
public static class RunTypes
{
	public const string Scan = "scan";
	public const string Remediate = "remediate";
	public const string Discover = "discover";
	public const string Download = "download";
	public const string CatalogIndex = "catalog-index";
	public const string BundleExport = "bundle-export";
	public const string BundleImport = "bundle-import";
	public const string ContentLibrarySync = "content-library-sync";
	public const string ContentPull = "content-pull";
	public const string ContentImport = "content-import";
	public const string Update = "update";
	public const string CredentialTest = "credential-test";
	public const string ToolInstall = "tool-install";
	public const string Purge = "purge";
	public const string DepotEnrollment = "depot-enrollment";
	public const string CatalogPull = "catalog-pull";
	public const string BinariesDownload = "binaries-download";

	public static readonly IReadOnlyList<string> All =
	[
		Scan, Remediate, Discover, Download, CatalogIndex, BundleExport, BundleImport,
		ContentLibrarySync, ContentPull, ContentImport, Update, CredentialTest, ToolInstall, Purge, DepotEnrollment, CatalogPull,
		BinariesDownload,
	];

	public static bool IsValid(string runType) => All.Contains(runType, StringComparer.Ordinal);
}
