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
/// PROVISIONAL per-job-type <see cref="JobResourceProfile"/> weights (issue #437). These
/// numbers exist so mixed compliance/download workloads cannot oversubscribe a runner's
/// discovered budget on day one; they are engineering estimates, not measured limits, and
/// must be revisited once real InSpec/PowerCLI/download workloads have been profiled on
/// representative hardware. Every entry below is deliberately conservative (rounds up,
/// not down) so the untuned default errs toward admitting fewer concurrent jobs rather
/// than more.
///
/// <para>
/// Covers every value in <c>jobs_job_type_check</c> (0001_initial_schema.sql +
/// 0019_jobs_credential_test_type.sql) -- exactly <see cref="JobCapabilities.All"/> -- so
/// a claimed job's type always resolves to a profile; <see cref="Default"/> exists only
/// as a defensive fallback for a job type this dictionary has not been extended to cover
/// (would indicate <see cref="JobCapabilities"/> and this file have drifted).
/// </para>
/// </summary>
public static class JobResourceProfiles
{
	/// <summary>
	/// Fallback profile for a job type with no explicit entry below. Deliberately the
	/// heaviest of the "later"/unimplemented profiles' rough shape so an unprofiled
	/// future job type is conservative-by-default rather than free-by-default.
	/// </summary>
	public static readonly JobResourceProfile Default = new(CpuCores: 1.0, MemoryBytes: 512L * 1024 * 1024);

	/// <summary>
	/// PROVISIONAL weights keyed by <c>jobs.job_type</c>. See this file's remarks --
	/// none of these are tuned against a measured workload yet.
	/// </summary>
	private static readonly Dictionary<string, JobResourceProfile> Profiles = new(StringComparer.Ordinal)
	{
		// Compliance domain (ADR-0013 §2).
		// discover/credential-test: short-lived network probes, negligible CPU/memory.
		["discover"] = new JobResourceProfile(CpuCores: 0.25, MemoryBytes: 128L * 1024 * 1024),
		["credential-test"] = new JobResourceProfile(CpuCores: 0.25, MemoryBytes: 128L * 1024 * 1024),
		// scan: parallel InSpec against a target list -- the heaviest compliance job
		// type; PowerShell runspace + InSpec process fan-out per target.
		["scan"] = new JobResourceProfile(CpuCores: 2.0, MemoryBytes: 1024L * 1024 * 1024),
		// remediate: PowerCLI/Ansible against a target list; no handler yet (ADR-0013
		// "later"), profiled alongside scan's shape since it walks the same target list.
		["remediate"] = new JobResourceProfile(CpuCores: 1.5, MemoryBytes: 768L * 1024 * 1024),
		// content-pull/content-import: compliance-runner job types per ADR-0017 (moved
		// from the download domain grouping below) -- a git clone/fetch plus a
		// filesystem walk for profile discovery, similar shape to catalog-index.
		["content-pull"] = new JobResourceProfile(CpuCores: 0.5, MemoryBytes: 256L * 1024 * 1024),
		["content-import"] = Default,
		// purge: deletes up to three small files per job on local disk -- pure I/O,
		// negligible CPU/memory, similar shape to discover/credential-test.
		["purge"] = new JobResourceProfile(CpuCores: 0.25, MemoryBytes: 128L * 1024 * 1024),

		// Download domain (ADR-0013 §2).
		// catalog-index: reads/parses depot metadata on disk -- I/O-bound, light CPU/memory.
		["catalog-index"] = new JobResourceProfile(CpuCores: 0.5, MemoryBytes: 256L * 1024 * 1024),
		// download: vcf-download-tool artifact transfer -- network/disk-bound, moderate
		// memory for buffering, low CPU.
		["download"] = new JobResourceProfile(CpuCores: 0.5, MemoryBytes: 512L * 1024 * 1024),
		// The remaining "later" download job types (ADR-0013's reserved-but-unimplemented
		// set): profiled at Default's shape until each lands with its own handler and
		// can be measured.
		["bundle-export"] = Default,
		["bundle-import"] = Default,
		["content-library-sync"] = Default,
		["update"] = Default,
		// tool-install: copies/verifies a single artifact (local repository or staged
		// upload) into the managed-tool volume -- disk I/O plus a signature
		// verification pass, similar shape to catalog-index.
		["tool-install"] = new JobResourceProfile(CpuCores: 0.5, MemoryBytes: 256L * 1024 * 1024),
	};

	/// <summary>
	/// Resolves <paramref name="jobType"/>'s profile, falling back to <see cref="Default"/>
	/// for a job type not (yet) covered above rather than throwing -- admission must never
	/// crash the dispatcher over a resource-accounting lookup miss.
	/// </summary>
	public static JobResourceProfile ForJobType(string jobType) =>
		Profiles.TryGetValue(jobType, out JobResourceProfile profile) ? profile : Default;

	/// <summary>
	/// Issue #737 (epic #726 Wave 2 capstone, ADR-0024 "Resource admission applies to
	/// real component jobs"): PROVISIONAL per-transport weights for a component-
	/// granular <c>scan</c> job (one fanned out from a single accepted
	/// <c>scan_plan_items</c> row, carrying <c>scan_plan_item_id</c> -- see
	/// <see cref="Waypoint.Infrastructure.Runs.RunCreationService"/>), keyed by
	/// <see cref="Waypoint.Core.ComplianceContent.CatalogTransports"/> rather than by
	/// the flat <c>scan</c> job type <see cref="Profiles"/> uses above. A single
	/// component (one ESXi host, one VM, one VCSA service) is a materially lighter unit
	/// of work than the legacy per-target fan-out's whole-target InSpec run, so these
	/// are deliberately lighter than <see cref="Profiles"/>'s flat <c>scan</c> entry --
	/// still engineering estimates, not measured limits, per this file's own remarks.
	/// </summary>
	private static readonly Dictionary<string, JobResourceProfile> ScanComponentProfilesByTransport = new(StringComparer.Ordinal)
	{
		// vmware: one vCenter/ESXi/VM/VCSA-service InSpec run against a single
		// discovered object -- a fraction of the whole-target fan-out's shape.
		["vmware"] = new JobResourceProfile(CpuCores: 0.5, MemoryBytes: 256L * 1024 * 1024),
		// ssh: one SRG appliance scan (Photon/Aria/vIDM) -- already single-target
		// shaped even under the legacy model, unchanged weight.
		["ssh"] = new JobResourceProfile(CpuCores: 0.5, MemoryBytes: 256L * 1024 * 1024),
		// nsx-api: one NSX Manager API-driven scan -- network-bound, light CPU/memory,
		// same shape as discover/credential-test's network-probe profile.
		["nsx-api"] = new JobResourceProfile(CpuCores: 0.25, MemoryBytes: 128L * 1024 * 1024),
		// vcf-api: catalog-declared SDDC Manager/VCF API work -- no handler yet
		// (issue #737's stated remainder keeps this transport on the legacy path),
		// profiled defensively at the heavier vmware shape until it is measured.
		["vcf-api"] = new JobResourceProfile(CpuCores: 0.5, MemoryBytes: 256L * 1024 * 1024),
	};

	/// <summary>
	/// Resolves a component-granular <c>scan</c> job's profile by transport, falling
	/// back to <see cref="Profiles"/>'s flat <c>scan</c> entry for a transport this
	/// dictionary has not been extended to cover -- same "conservative fallback, never
	/// throw" contract as <see cref="ForJobType"/>. <paramref name="transport"/> is
	/// null for a job with no <c>scan_plan_item_id</c> (the legacy per-target fan-out
	/// path); callers pass null there and get the ordinary flat <c>scan</c> weight
	/// unchanged.
	/// </summary>
	public static JobResourceProfile ResolveScanComponentProfile(string? transport)
	{
		if (transport is not null && ScanComponentProfilesByTransport.TryGetValue(transport, out JobResourceProfile profile))
		{
			return profile;
		}

		return ForJobType("scan");
	}
}
