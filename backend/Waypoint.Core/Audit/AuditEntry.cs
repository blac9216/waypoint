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

namespace Waypoint.Core.Audit;

/// <summary>
/// A read-side projection of one <c>audit_log</c> row (migration 0001/0020's
/// append-only table). Every writer across the codebase (<c>CredentialSecretStore</c>,
/// <c>JobQueueRepository</c>, <c>CredentialRepository</c>, ...) already inserts through
/// this exact column set; this record is the read side <see cref="IAuditRepository"/>
/// projects, added by issue #512 -- there was previously no query surface over this
/// table at all, only writers.
/// </summary>
public sealed record AuditEntry(
	Guid Id,
	string EventType,
	string Actor,
	Guid? CredentialId,
	Guid? JobId,
	Guid? RunId,
	string DetailJson,
	DateTimeOffset OccurredAt);

/// <summary>Filter parameters for <see cref="IAuditRepository.ListAsync"/>, mirroring <c>docs/api-contract.md</c> `/audit`'s "filterable by kind/actor/time window".</summary>
public sealed record AuditQuery(
	string? EventType,
	string? Actor,
	DateTimeOffset? From,
	DateTimeOffset? To);

public sealed record AuditListResult(IReadOnlyList<AuditEntry> Items, long TotalCount);
