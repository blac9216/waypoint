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
/// One job to create as part of a run's fan-out (ADR-0008 "Run -&gt; Jobs fan-out"). A
/// caller (a future scan/remediate initiator -- #6/#8/#9, not built here) produces one
/// of these per target/component; <see cref="IJobControlRepository.FanOutJobsAsync"/>
/// inserts them all as one run.
/// </summary>
/// <param name="HasEphemeralCredential">
/// True when this job's caller-supplied "my credentials" secret (ADR-0011 ad hoc flow,
/// issue #276) is held in <see cref="Waypoint.Core.Secrets.IEphemeralCredentialCache"/>
/// keyed by the job id <see cref="IJobControlRepository.FanOutJobsAsync"/> assigns --
/// never carried on this record or any persisted column. Mutually exclusive with
/// <paramref name="CredentialId"/>; the caller (<c>RunsController</c>) enforces that.
/// </param>
public sealed record JobSpec(
	string JobType,
	short Priority,
	Guid? TargetId = null,
	string? TargetName = null,
	Guid? CredentialId = null,
	string Payload = "{}",
	bool HasEphemeralCredential = false);
