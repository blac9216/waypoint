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

namespace Waypoint.Core.Capacity;

/// <summary>
/// Issue #1531 (epic #1180, split from #1042): the store-agnostic disk-admission
/// decision built on top of #1529's <see cref="DiskAdmissionPolicy"/> singleton and
/// the existing <see cref="Waypoint.Core.SystemState.IArtifactStoreDiskUsageProvider"/>.
/// Deliberately takes a store name (rather than assuming the single M1 store) so a
/// later multi-store provider (#1534) and #1046's future per-lane evaluation can both
/// call this without a second interface appearing.
/// </summary>
public interface IDiskAdmissionService
{
	/// <summary>
	/// Decides whether <paramref name="projectedBytes"/> may be admitted against
	/// <paramref name="storeName"/>'s current free space and the configured reserve.
	/// Pure decision logic against injected collaborators -- no live filesystem or
	/// Postgres dependency of its own, so it is unit-testable with fakes for both
	/// (issue #1531 AC4).
	/// </summary>
	Task<DiskAdmissionResult> AdmitAsync(long projectedBytes, string storeName, CancellationToken cancellationToken);
}

/// <summary>
/// The outcome of one <see cref="IDiskAdmissionService.AdmitAsync"/> call.
/// <see cref="ShortfallBytes"/> is zero when <see cref="Allowed"/> is <c>true</c>;
/// otherwise it is the number of bytes by which the request would breach the reserve
/// (<c>projectedBytes - (FreeBytes - ReserveBytes)</c>), the exact number an "actionable
/// alert" (this issue's AC) needs to explain the denial without the caller
/// re-deriving it.
/// </summary>
public sealed record DiskAdmissionResult(bool Allowed, long ProjectedBytes, long FreeBytes, long ReserveBytes, long ShortfallBytes);
