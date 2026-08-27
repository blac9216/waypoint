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
/// One immutable row of migration 0062's <c>upload_attempts</c> table (issue #744,
/// epic #726 Wave 4): a single STIG Manager CKL upload attempt for a scan job, either
/// the first convert-stage attempt or a later <c>stigman-upload-retry</c> call.
/// <see cref="Endpoint"/>/<see cref="Collection"/> are null only when no STIG Manager
/// connection was resolved at all (nothing to attribute the attempt to besides the
/// failure itself). <see cref="Status"/> is one of <see cref="JobUploadStatuses"/>.
/// </summary>
public sealed record UploadAttemptRecord(
	Guid Id,
	Guid JobId,
	int AttemptNumber,
	string? Endpoint,
	string? Collection,
	string Status,
	string? ErrorDetail,
	DateTimeOffset AttemptedAt);
