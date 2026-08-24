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
public sealed record JobCredentialBindingSpec(string Purpose, Guid CredentialId);

/// <summary>
/// A job's persisted per-purpose credential snapshot row (migration 0044), as read by
/// the executing runner (<see cref="IJobRunnerRepository.GetJobCredentialBindingsAsync"/>).
/// <see cref="CredentialId"/> is null only after issue #593's terminal-history detach
/// (the credential was deleted while every reference to it was terminal); the three
/// attribution fields are the non-secret snapshot captured at that detach, null while
/// <see cref="CredentialId"/> still names a live credential.
/// </summary>
public sealed record JobCredentialBinding(
	Guid JobId,
	string Purpose,
	Guid? CredentialId,
	string? CredentialName,
	string? CredentialType,
	string? CredentialUsername);
