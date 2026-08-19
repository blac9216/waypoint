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
/// Read-only query surface over <c>audit_log</c> (migration 0001/0020, issue #512).
/// One implementation (<c>Waypoint.Infrastructure.Audit.AuditRepository</c>, plain
/// Npgsql). Every writer stays exactly where it already lives (<c>JobQueueRepository</c>,
/// <c>CredentialSecretStore</c>, <c>CredentialRepository</c>, ...) -- this interface adds
/// nothing on the write side, only the query <c>AuditController</c> needs.
/// </summary>
public interface IAuditRepository
{
	/// <summary>
	/// Stable ordering: <c>occurred_at DESC, id DESC</c> -- the tie-break on <c>id</c>
	/// (rather than relying on <c>occurred_at</c> alone) is what keeps paging stable
	/// when several rows share a timestamp, mirroring every other paged list query in
	/// this codebase's "DESC, id DESC" idiom.
	/// </summary>
	Task<AuditListResult> ListAsync(AuditQuery query, int limit, int offset, CancellationToken cancellationToken);
}
