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
/// The <c>disk_admission_policy</c> singleton row (migration 20260913060100, issue
/// #1529, split from #1042; epic #1180). <see cref="ReserveBytes"/> is the byte count
/// the future admission check (#1531) compares an artifact store's free space
/// against before admitting a download/store write. <see cref="UpdatedBy"/> is
/// <c>null</c> when the policy still holds the seeded default and has never been
/// changed by an Admin -- same shape as <see cref="Waypoint.Core.Runs.RetentionPolicy"/>.
/// </summary>
public sealed record DiskAdmissionPolicy(long ReserveBytes, string? UpdatedBy, DateTimeOffset UpdatedAt);
