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
/// How long the API process has been running (issue #241, follow-up to #226): the
/// api-contract.md "uptime" field, deliberately deferred out of #240 until something
/// consumed it. Kept behind an interface (rather than reading
/// <see cref="Environment.TickCount64"/>/<c>Process.StartTime</c> directly in the
/// controller) so <c>SystemEndpointTests</c> can pin an exact value instead of
/// asserting against a moving wall clock.
/// </summary>
public interface IApplianceUptimeProvider
{
	/// <summary>Wall-clock time elapsed since this process started.</summary>
	TimeSpan GetUptime();
}
