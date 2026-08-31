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

namespace Waypoint.Core.Downloads;

/// <summary>
/// The exact string values of <c>oci_bundles.status</c>, matching
/// <c>oci_bundles_status_check</c> (migration 0118). Issue #1403: the OCI bundle store
/// schema is model-only -- no acquisition (#1413) or push (#1441) logic lives here, but
/// the status vocabulary those two children drive an <see cref="OciBundle"/> row through
/// is settled now so both build against the same closed set.
/// </summary>
public static class OciBundleStatuses
{
	/// <summary>An <c>imgpkg</c>-shaped bundle tar has been acquired and verified onto local disk (#1413), not yet pushed anywhere.</summary>
	public const string Staged = "staged";

	/// <summary>The staged tar was pushed into the operator's depot-registry push target (#1441) and the operation succeeded.</summary>
	public const string Pushed = "pushed";

	/// <summary>The last push attempt against a push target failed; the staged tar is retained for a retry.</summary>
	public const string PushFailed = "push_failed";

	public static readonly IReadOnlyCollection<string> All = [Staged, Pushed, PushFailed];
}

/// <summary>
/// One staged OCI image bundle (<c>oci_bundles</c>, migration 0118) -- issue #1403,
/// split from the design record #1161: a locally acquired <c>imgpkg</c>-shaped tar
/// (Supervisor services, VKS standard packages, VKR, VCF CLI plugins; see #1157's
/// findings) awaiting an operator-triggered push into a <see cref="PushTargetConsumer"/>.
/// Waypoint never hosts a registry itself (#1157 Q3) -- this row only tracks the tar on
/// local disk and where it is destined, never the OCI transfer itself.
/// </summary>
public sealed record OciBundle(
	Guid Id,
	string ComponentKey,
	string SourceVersion,
	string TargetRepoPath,
	string TarFilePath,
	string Sha256,
	string Status,
	DateTimeOffset StagedAt,
	string? PushFailureReason);

/// <summary>
/// The deterministic component-key to depot-registry repo-path map (#1157's Q3
/// findings: "their per-component depot registry repo path is a fixed, published
/// prefix -- the vendor's own tooling hard-codes the map"). Modeled as data, not an
/// enum (#1403's stated risk: the map grows with later vendor releases and must not
/// need a migration to extend) -- these are the components #1157 confirmed a path for;
/// an unmapped component key is a data gap to fill when encountered, not a coding
/// error.
/// </summary>
public static class OciBundleComponentRepoPaths
{
	public static readonly IReadOnlyDictionary<string, string> ByComponentKey = new Dictionary<string, string>(StringComparer.Ordinal)
	{
		["SUPERVISOR_SERVICE_HARBOR"] = "/supervisor-service-harbor/ga",
		["VKS_STANDARD_PACKAGES"] = "/vks-standard-packages/ga",
		["VKR"] = "/vsphere-kubernetes-release/ga",
		["VCF_CONSUMPTION_CLI_PLUGINS"] = "/vcf-cli-plugins/ga",
	};

	/// <summary>The published depot-registry repo path for <paramref name="componentKey"/>, or <c>null</c> if this component's path has not been documented yet (#1157's map is not exhaustive).</summary>
	public static string? TryGetRepoPath(string componentKey) =>
		ByComponentKey.TryGetValue(componentKey, out string? path) ? path : null;
}
