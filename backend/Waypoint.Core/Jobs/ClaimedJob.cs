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

/// <summary>A <c>jobs</c> row as returned by a successful claim -- the fields a handler or the dispatcher needs, not every column.</summary>
public sealed record ClaimedJob(
	Guid Id,
	Guid? RunId,
	string JobType,
	Guid? TargetId,
	string? TargetName,
	Guid? CredentialId,
	short Priority,
	string Payload,
	int AttemptCount,
	int MaxAttempts);
