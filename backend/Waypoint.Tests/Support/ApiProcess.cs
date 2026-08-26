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

namespace Waypoint.Tests.Support;

/// <summary>
/// Launches <c>Waypoint.Api.dll</c> as a real child process. Two behaviours in this
/// scaffold are only observable from outside the process — the exit code a fatal startup
/// failure produces, and the container health probe's verdict — so
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// cannot be used to test them.
///
/// The mechanics live in <see cref="HostProcess"/>, which the runner hosts share; this
/// type is the API-specific binding of it (entry assembly + the historical call shape its
/// existing callers use). See <see cref="HostProcess"/>'s doc comment for why callers must
/// pass the configuration they depend on through <c>environment</c> instead of relying on
/// the inherited working directory's <c>appsettings.json</c>.
/// </summary>
public static class ApiProcess
{
	/// <summary>Assembly-metadata key set by <c>Waypoint.Tests.csproj</c>.</summary>
	private const string EntryAssemblyMetadataKey = "WaypointApiEntryAssembly";

	/// <summary>Absolute path of the API's runnable output for the current build configuration.</summary>
	public static string EntryAssemblyPath { get; } = HostProcess.ResolveEntryAssemblyPath(EntryAssemblyMetadataKey);

	/// <summary>Runs the API to completion and returns its exit code.</summary>
	/// <param name="arguments">Arguments passed after the assembly path (e.g. <c>--health-check</c>).</param>
	/// <param name="environment">Environment variables layered onto the child process.</param>
	/// <param name="timeout">How long to wait before treating the run as hung.</param>
	/// <param name="output">Optional sink capturing the child's stdout+stderr.</param>
	public static int Run(
		IEnumerable<string>? arguments = null,
		IDictionary<string, string>? environment = null,
		TimeSpan? timeout = null,
		ChildOutput? output = null)
		=> HostProcess.Run(EntryAssemblyPath, arguments, environment, timeout, output);

	/// <summary>Starts the API without waiting for it — the caller owns the returned process.</summary>
	/// <param name="arguments">Arguments passed after the assembly path.</param>
	/// <param name="environment">Environment variables layered onto the child process.</param>
	/// <param name="output">Optional sink capturing the child's stdout+stderr.</param>
	public static Process Start(
		IEnumerable<string>? arguments = null,
		IDictionary<string, string>? environment = null,
		ChildOutput? output = null)
		=> HostProcess.Start(EntryAssemblyPath, arguments, environment, output);

	/// <summary>Terminates a still-running child process, ignoring an already-exited one.</summary>
	/// <param name="process">The process to stop.</param>
	public static void Kill(Process process) => HostProcess.Kill(process);

	/// <summary>Reserves an unused loopback TCP port for a child process to bind.</summary>
	public static int GetFreePort() => HostProcess.GetFreePort();
}
