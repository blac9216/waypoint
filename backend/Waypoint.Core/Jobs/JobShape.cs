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
/// produce a CKL to attest and convert. Only <c>scan</c> is <see cref="Standard"/>.
/// There is no SRG <c>job_type</c> in <c>jobs_job_type_check</c> yet -- adding one is
/// out of scope here (see #24); <see cref="Srg"/> exists so the shape is ready and
/// tested, and <see cref="JobShapes.ForJobType"/> must route that job type to it when
/// #24 lands. This mapping is a deliberate scope call for epic #5, not yet exercised by
/// a real handler (#8/#9 land the job types themselves) -- revisit here first if a
/// future job type needs its own shape.
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
	public static JobShape ForJobType(string jobType)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jobType);

		return string.Equals(jobType, "scan", StringComparison.Ordinal) ? JobShape.Standard : JobShape.Simple;
	}
}
