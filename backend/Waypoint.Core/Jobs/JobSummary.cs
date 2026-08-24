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
/// Job row projected for the REST surface (GET /runs/{id}/jobs).
/// Matches docs/api-contract.md "Runs &amp; jobs" — job detail fields.
/// <see cref="UploadStatus"/>/<see cref="UploadDetail"/> are issue #311's
/// <c>jobs.upload_status</c>/<c>upload_detail</c> columns (migration 0018) -- null on
/// every job type except <c>scan</c>, and null on a <c>scan</c> job until its convert
/// stage has attempted an upload (see <c>ScanJobHandler</c>).
/// </summary>
public sealed record JobSummary(
	Guid Id,
	Guid? RunId,
	string JobType,
	string? TargetId,
	string? TargetName,
	string State,
	string? Stage,
	short Priority,
	int AttemptCount,
	string? CreatedAt,
	string? StartedAt,
	string? FinishedAt,
	string? UploadStatus = null,
	string? UploadDetail = null,
	/// <summary>Issue #593 (migration 0041): see <see cref="RunSummary.CredentialName"/> -- same snapshot, job-scoped.</summary>
	string? CredentialName = null,
	string? CredentialType = null,
	string? CredentialUsername = null);
