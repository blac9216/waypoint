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

namespace Waypoint.Core.Downloads;

/// <summary>
/// Storage for the <c>managed_tool_installs</c> append-only ledger (issue #39, migration
/// 0037). One implementation (<c>Waypoint.Infrastructure.Downloads.ManagedToolInstallRepository</c>,
/// plain Npgsql -- same "no ORM for this layer" convention as <c>DepotArtifactRepository</c>).
/// </summary>
public interface IManagedToolInstallRepository
{
	/// <summary>Appends one install attempt (installed, rejected, or failed) to the ledger. Returns the new row's id.</summary>
	Task<Guid> RecordAsync(ManagedToolInstallAttempt attempt, CancellationToken cancellationToken);

	/// <summary>Newest-first install history, for the Depot &amp; Tokens screen and <c>GET /downloads/tool/installs</c>.</summary>
	Task<IReadOnlyList<ManagedToolInstall>> ListAsync(int limit, CancellationToken cancellationToken);

	/// <summary>
	/// The most recent <c>outcome = installed</c> row, or <c>null</c> if the tool has
	/// never been successfully installed. This is the ledger's derived "current install"
	/// view (migration 0037's doc comment: there is deliberately no separate current-
	/// tool row to keep in sync).
	/// </summary>
	Task<ManagedToolInstall?> GetCurrentAsync(CancellationToken cancellationToken);
}
