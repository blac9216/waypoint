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

namespace Waypoint.Core.Scheduling;

/// <summary>
/// Tuning for <c>ScheduleDispatchHostedService</c> (issue #31), bound from the
/// <c>ScheduleDispatch</c> configuration section -- same shape as
/// <see cref="Waypoint.Core.Secrets.RunSecretOptions"/>'s periodic-sweep options.
/// </summary>
public sealed class ScheduleDispatchOptions
{
	public const string SectionName = "ScheduleDispatch";

	/// <summary>
	/// How often the dispatcher scans for due schedules. One minute -- cron's own
	/// resolution floor (<see cref="CronExpression"/> has no sub-minute grammar), so
	/// polling faster buys nothing.
	/// </summary>
	public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);
}
