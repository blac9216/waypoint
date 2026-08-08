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
/// Which state-machine shape a <c>jobs.job_type</c> follows (see
/// <see cref="JobStateMachine"/>). <c>docs/api-contract.md</c> "State machines" defines
/// three: the full STIG scan pipeline (<c>queued -> running -> attesting -> converting
/// -> uploaded</c>), the SRG scan pipeline that attests but skips converting/uploaded
/// ("SRG jobs skip converting/uploaded (HDF-only) -> done", <see cref="Srg"/>), and the
/// shorter shape everything else uses. Per this issue's scope, the last is generalized
/// to the M1 download vertical slice's job types too: a <c>download</c> job's
/// <c>queued -> downloading -> verifying -> verified|failed</c> detail lives on its own
/// <c>downloads</c> row (a job can 1:1-own many downloads across retries), while the
/// owning <c>jobs.state</c> itself only needs <c>queued -> running -> done|failed</c>
/// -- there is no attest/convert/upload pipeline stage for a file transfer.
/// <c>remediate</c>, <c>discover</c>, <c>catalog-index</c>, the bundle/content-* types,
/// and <c>update</c> all reduce to the same shape for the same reason: none of them
/// produce a CKL to attest and convert. Only <c>scan</c> is <see cref="Standard"/> --
/// except for an <c>ssh</c>-kind target (SRG products: Photon/Aria/vIDM), which is
/// <see cref="Srg"/> (issue #309, second sub-issue of the #24 split). There is no
/// separate SRG <c>job_type</c> in <c>jobs_job_type_check</c> -- every scan fans out as
/// <c>job_type = 'scan'</c> regardless of target kind (<c>ScanTargetPriority</c> already
/// scores <c>ssh</c> at priority 6) -- so shape selection for a <c>scan</c> job needs one
/// more signal than <c>job_type</c> alone: <see cref="JobShapes.ForJob"/> reads the
/// fanned-out job's <c>target_kind</c> (written into <c>jobs.payload</c> at fan-out,
/// <c>RunsController.CreateScanRunAsync</c>) to tell an <c>ssh</c> scan from a
/// <c>vsphere</c>/<c>nsx-api</c> one.
/// </summary>
public enum JobShape
{
	/// <summary><c>queued -> running -> attesting -> converting -> uploaded</c>.</summary>
	Standard,

	/// <summary><c>queued -> running -> done</c>.</summary>
	Simple,

	/// <summary>
	/// <c>queued -> running -> attesting -> done</c>. The SRG scan pipeline: it
	/// attests (produces an HDF, optionally applies SAF attestations) but skips
	/// <c>converting</c>/<c>uploaded</c> -- see #136. No <c>job_type</c> maps here yet
	/// (see #24).
	/// </summary>
	Srg
}

/// <summary>Maps a <c>jobs.job_type</c> value to its <see cref="JobShape"/>.</summary>
public static class JobShapes
{
	private const string TargetKindPropertyName = "target_kind";

	/// <summary>
	/// The job_type-only mapping: correct for every job_type except <c>scan</c>, whose
	/// shape additionally depends on target kind -- see <see cref="ForJob"/>. Kept for
	/// callers (e.g. non-scan job types) that have no payload to inspect and for
	/// backward-compatible tests exercising the job_type-only rule directly.
	/// </summary>
	public static JobShape ForJobType(string jobType)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jobType);

		return string.Equals(jobType, "scan", StringComparison.Ordinal) ? JobShape.Standard : JobShape.Simple;
	}

	/// <summary>
	/// The full mapping (issue #309): a <c>scan</c> job whose payload carries
	/// <c>target_kind: "ssh"</c> (written at fan-out by <c>RunsController.CreateScanRunAsync</c>)
	/// routes to <see cref="JobShape.Srg"/>; every other <c>scan</c> job (no
	/// <c>target_kind</c>, or any kind other than <c>ssh</c>) is <see cref="JobShape.Standard"/>,
	/// matching <see cref="ForJobType"/>'s prior behavior exactly -- so a payload that
	/// predates this field, or is malformed, degrades to the pre-#309 mapping rather than
	/// throwing. Non-<c>scan</c> job types never inspect the payload at all.
	/// </summary>
	public static JobShape ForJob(string jobType, string payloadJson)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jobType);

		if (!string.Equals(jobType, "scan", StringComparison.Ordinal))
		{
			return JobShape.Simple;
		}

		return IsSshTargetKind(payloadJson) ? JobShape.Srg : JobShape.Standard;
	}

	private static bool IsSshTargetKind(string payloadJson)
	{
		if (string.IsNullOrWhiteSpace(payloadJson))
		{
			return false;
		}

		try
		{
			using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(payloadJson);
			return document.RootElement.TryGetProperty(TargetKindPropertyName, out System.Text.Json.JsonElement kindElement)
				&& kindElement.ValueKind == System.Text.Json.JsonValueKind.String
				&& string.Equals(kindElement.GetString(), "ssh", StringComparison.Ordinal);
		}
		catch (System.Text.Json.JsonException)
		{
			// Malformed payload JSON: degrade to Standard rather than throwing out of a
			// shape lookup the dispatcher calls on every claimed job.
			return false;
		}
	}
}
