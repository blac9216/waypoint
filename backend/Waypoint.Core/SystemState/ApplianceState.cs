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

namespace Waypoint.Core.SystemState;

/// <summary>
/// The <c>appliance_state</c> singleton row (migration 0001; api-contract.md's
/// "Postgres schema sketch"): version, mode, and update status for the running
/// instance. Read-only from this slice's perspective -- nothing writes this table yet
/// (mode is operator-configured at deploy time; update status is M7/ADR-0009).
/// </summary>
public sealed record ApplianceState(
	string Version,
	string? BuildSha,
	string Mode,
	string UpdateStatus,
	string? UpdateAvailableVersion);
