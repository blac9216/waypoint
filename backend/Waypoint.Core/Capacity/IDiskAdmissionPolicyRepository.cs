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
/// Storage for <c>disk_admission_policy</c> (migration 20260913060100). One
/// implementation (<c>Waypoint.Infrastructure.Capacity.DiskAdmissionPolicyRepository</c>,
/// plain Npgsql -- same "no ORM for this layer" convention as
/// <c>Waypoint.Infrastructure.Runs.RetentionPolicyRepository</c>). No admission
/// decision logic lives here -- that is issue #1531's surface; this interface only
/// makes the reserve-bytes setting readable and writable.
/// </summary>
public interface IDiskAdmissionPolicyRepository
{
	/// <summary>
	/// Reads the singleton row. The migration seeds it unconditionally (<c>id = 1</c>
	/// with the default reserve), so a null return means the row was deleted out of
	/// band rather than "not yet configured" -- callers should treat that as a
	/// server error, matching <see cref="Waypoint.Core.Runs.IRetentionPolicyRepository.GetAsync"/>'s
	/// own contract for its sibling singleton.
	/// </summary>
	Task<DiskAdmissionPolicy?> GetAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Updates the singleton row's reserve, actor, and timestamp in one statement.
	/// </summary>
	Task<DiskAdmissionPolicy> SetAsync(long reserveBytes, string actor, CancellationToken cancellationToken);
}
