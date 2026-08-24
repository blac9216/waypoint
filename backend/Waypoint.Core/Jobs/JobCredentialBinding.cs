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
/// One purpose-resolved credential reference to snapshot onto a fanned-out job
/// (issue #585, epic #582, ADR-0021 §5). Produced by
/// <c>RunCreationService.CreateScanRunAsync</c>'s resolution step (target-assigned
/// <c>target_credential_bindings</c> plus validated per-target/per-purpose overrides)
/// and persisted as a <c>job_credential_bindings</c> row (migration 0044) inside
/// <see cref="IJobControlRepository.FanOutJobsAsync"/>'s transaction. Identity only --
/// never secret material; decryption happens per claimed job, per purpose, inside the
/// executing runner (ADR-0014 §6).
/// </summary>
/// <param name="IsRunSecret">
/// Issue #586 (epic #582): true when this purpose is satisfied by a per-target/per-purpose
/// AD HOC credential rather than a saved one -- <paramref name="CredentialId"/> is
/// <c>null</c> in that case (migration 0045's <c>run_secrets_binding_shape_check</c>
/// backstop), and the executing runner instead decrypts
/// <c>run_secrets</c> keyed by <c>(job.RunId, job.TargetId, Purpose)</c>
/// (<see cref="Waypoint.Core.Secrets.IRunSecretStore"/>, <see cref="Waypoint.Core.Secrets.RunSecretKey.For"/>).
/// </param>
public sealed record JobCredentialBindingSpec(string Purpose, Guid? CredentialId, bool IsRunSecret = false);

/// <summary>
/// A job's persisted per-purpose credential snapshot row (migration 0044), as read by
/// the executing runner (<see cref="IJobRunnerRepository.GetJobCredentialBindingsAsync"/>).
/// <see cref="CredentialId"/> is null in two cases distinguished by
/// <see cref="IsRunSecret"/>: an ad hoc purpose (issue #586, <see cref="IsRunSecret"/>
/// true -- the secret lives in <c>run_secrets</c>, not <c>credentials</c>, and never had a
/// credential id to begin with), or issue #593's terminal-history detach (the credential
/// was deleted while every reference to it was terminal, <see cref="IsRunSecret"/> false);
/// the three attribution fields are the non-secret snapshot captured at THAT detach, null
/// while <see cref="CredentialId"/> still names a live credential or whenever
/// <see cref="IsRunSecret"/> is true (an ad hoc purpose was never a <c>credentials</c> row
/// and so has no name/type/username to snapshot here).
/// </summary>
public sealed record JobCredentialBinding(
	Guid JobId,
	string Purpose,
	Guid? CredentialId,
	string? CredentialName,
	string? CredentialType,
	string? CredentialUsername,
	bool IsRunSecret = false);
