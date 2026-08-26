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

namespace Waypoint.Tests.Support;

/// <summary>
/// The two dedicated runner hosts' runnable outputs, resolved the same way
/// <see cref="ApiProcess"/> resolves the API's. Unlike the API, neither runner has a
/// top-level <c>catch (Exception)</c>, so a fatal startup misconfiguration surfaces as an
/// unhandled exception: a non-zero exit code plus the exception text on stderr. That pair
/// is only observable from outside the process, which is why these tests run the real
/// binary through <see cref="HostProcess"/>.
/// </summary>
public static class RunnerProcess
{
	/// <summary>Absolute path of <c>Waypoint.ComplianceRunner.dll</c> for the current build configuration.</summary>
	public static string ComplianceEntryAssemblyPath { get; } =
		HostProcess.ResolveEntryAssemblyPath("WaypointComplianceRunnerEntryAssembly");

	/// <summary>Absolute path of <c>Waypoint.DownloadRunner.dll</c> for the current build configuration.</summary>
	public static string DownloadEntryAssemblyPath { get; } =
		HostProcess.ResolveEntryAssemblyPath("WaypointDownloadRunnerEntryAssembly");
}
