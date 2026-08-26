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

namespace Waypoint.Tests.Parity;

/// <summary>
/// One documented component that a <see cref="CatalogParityRow"/> covers -- one
/// vendor-content leaf profile with its expected derived catalog identity. A "capability
/// group" row in docs/compliance-parity.md's provenance matrix (e.g. "vSphere 8-0 STIG,
/// vmware transport") often groups several distinct components (vCenter, ESXi, VM); each
/// becomes its own <see cref="CatalogParityComponent"/> so the contract test asserts the
/// full tuple for every one, not just a representative sample.
/// </summary>
public sealed record CatalogParityComponent(
	string ComponentKey,
	string DisplayName,
	string? SelectorName,
	string[] CredentialPurposes,
	string? ReportGroupKeyOverride = null,
	int? ReportGroupPriorityOverride = null,
	string? SelectorKindOverride = null)
{
	/// <summary>Effective report-group key: the component's override if set, else the row's.</summary>
	public string ReportGroupKey(CatalogParityRow row) => ReportGroupKeyOverride ?? row.ReportGroupKey;

	/// <summary>Effective report-group priority: the component's override if set, else the row's.</summary>
	public int ReportGroupPriority(CatalogParityRow row) => ReportGroupPriorityOverride ?? row.ReportGroupPriority;

	/// <summary>Effective selector kind: the component's override if set, else the row's.</summary>
	public string SelectorKind(CatalogParityRow row) => SelectorKindOverride ?? row.SelectorKind;
}

/// <summary>
/// One row of docs/compliance-parity.md's "Sibling source-capability provenance matrix"
/// table, made machine-readable for <see cref="CatalogParityContractTests"/>. Field names
/// mirror the matrix's own columns. <see cref="MatrixRowId"/> is a stable identifier used
/// only by this test suite (never persisted, never catalog authority) so
/// <see cref="ParityMatrixCompletenessTests"/> can prove every documented row is covered.
/// </summary>
public sealed record CatalogParityRow(
	string MatrixRowId,
	string ProductVersionKey,
	string VendorFamily,
	string Kind,
	string ReleaseKey,
	string Transport,
	string SelectorKind,
	int ReportGroupPriority,
	string ReportGroupKey,
	bool HasBenchmark,
	string OutputKind,
	bool RemediationSupported,
	IReadOnlyList<CatalogParityComponent> Components);
