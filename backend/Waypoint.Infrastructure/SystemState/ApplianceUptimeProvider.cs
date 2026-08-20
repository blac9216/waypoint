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

using System.Diagnostics;
using Waypoint.Core.SystemState;

namespace Waypoint.Infrastructure.SystemState;

/// <inheritdoc cref="IApplianceUptimeProvider"/>
/// <remarks>
/// Uses <see cref="Process.StartTime"/> of the current process rather than a clock
/// captured at DI-registration time -- both land within the same startup window in
/// practice, but pinning to the OS-reported process start avoids a second "when did
/// the appliance actually start" definition drifting from what <c>ps</c>/<c>docker
/// stats</c> would show an operator.
/// </remarks>
public sealed class ApplianceUptimeProvider : IApplianceUptimeProvider
{
	public TimeSpan GetUptime() => DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();
}
