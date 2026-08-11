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
/// <param name="HasRunSecret">
/// True when this job's caller-supplied "my credentials" secret (ADR-0011 ad hoc flow,
/// issue #276/#434) is stored, envelope-encrypted, in <c>run_secrets</c> under the
/// job's own run id (<see cref="Waypoint.Core.Secrets.IRunSecretStore"/>) -- never
/// carried on this record, never a stored <c>credentials</c> row. Persisted onto
/// <c>jobs.has_run_secret</c> by <see cref="IJobControlRepository.FanOutJobsAsync"/>
/// (migration 0023). Mutually exclusive with <paramref name="CredentialId"/>; the
/// caller (<c>RunsController</c>) enforces that at the API layer and
/// <c>jobs_credential_exclusivity_check</c> backstops it at the schema layer.
/// </param>
public sealed record JobSpec(
	string JobType,
	short Priority,
	Guid? TargetId = null,
	string? TargetName = null,
	Guid? CredentialId = null,
	string Payload = "{}",
	bool HasRunSecret = false);
