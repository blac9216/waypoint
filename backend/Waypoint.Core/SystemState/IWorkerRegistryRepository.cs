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

namespace Waypoint.Core.SystemState;

/// <summary>
/// Storage for <c>worker_registry</c> (migration 0026, issue #443). One implementation
/// (<c>Waypoint.Infrastructure.SystemState.WorkerRegistryRepository</c>, plain Npgsql,
/// same "no ORM for this layer" convention as <c>ApplianceStateRepository</c>).
///
/// Deliberately two separate interfaces rather than one, mirroring the
/// <c>IJobControlRepository</c>/<c>IJobRunnerRepository</c> split ADR-0013/0014
/// already established for the job queue: <see cref="IWorkerRegistryWriter"/> is what a
/// runner process needs (upsert its own heartbeat), <see cref="IWorkerRegistryReader"/>
/// is what the control plane needs (list every known worker for <c>GET /system</c>).
/// Neither runner role is granted read access on this table (see migration 0027's
/// comment) -- only the reader interface's implementation is wired into
/// <c>Waypoint.Api</c>.
/// </summary>
public interface IWorkerRegistryWriter
{
	/// <summary>
	/// Upserts this worker's row: same shape as a job lease heartbeat (0016's
	/// "no update in N seconds means the process is gone" reasoning), but independent
	/// of any claimed job -- an idle worker between jobs still heartbeats.
	/// </summary>
	/// <param name="starvedJobTypes">
	/// Issue #467 (migration 0029): job types this worker is currently denying resource
	/// admission to. Empty (not null) when nothing is starved.
	/// </param>
	/// <param name="toolPresent">
	/// Issue #560 (migration 0035): the download-runner's managed-tool presence check,
	/// or null for a compliance-runner (or any caller with nothing to report here).
	/// </param>
	Task HeartbeatAsync(string workerId, IReadOnlyList<string> jobTypes, bool ready, IReadOnlyList<StarvedWorkerJobType> starvedJobTypes, CancellationToken cancellationToken, bool? toolPresent = null);
}

/// <summary>See <see cref="IWorkerRegistryWriter"/> for why this is a separate interface.</summary>
public interface IWorkerRegistryReader
{
	/// <summary>Every known worker row, most-recently-seen first. Small, unpaged: one row per live runner process.</summary>
	Task<IReadOnlyList<WorkerHeartbeat>> ListAsync(CancellationToken cancellationToken);
}
