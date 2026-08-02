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
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace Waypoint.Tests.Support;

/// <summary>
/// Launches <c>Waypoint.Api.dll</c> as a real child process. Two behaviours in this
/// scaffold are only observable from outside the process — the exit code a fatal startup
/// failure produces, and the container health probe's verdict — so
/// <see cref="WebApplicationFactory{TEntryPoint}"/> cannot be used to test them.
/// </summary>
public static class ApiProcess
{
	/// <summary>Assembly-metadata key set by <c>Waypoint.Tests.csproj</c>.</summary>
	private const string EntryAssemblyMetadataKey = "WaypointApiEntryAssembly";

	/// <summary>Absolute path of the API's runnable output for the current build configuration.</summary>
	public static string EntryAssemblyPath { get; } = ResolveEntryAssemblyPath();

	/// <summary>Runs the API to completion and returns its exit code.</summary>
	/// <param name="arguments">Arguments passed after the assembly path (e.g. <c>--health-check</c>).</param>
	/// <param name="environment">Environment variables layered onto the child process.</param>
	/// <param name="timeout">How long to wait before treating the run as hung.</param>
	public static int Run(
		IEnumerable<string>? arguments = null,
		IDictionary<string, string>? environment = null,
		TimeSpan? timeout = null)
	{
		using Process process = Start(arguments, environment);

		if (!process.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(60)).TotalMilliseconds))
		{
			Kill(process);
			throw new TimeoutException($"{Path.GetFileName(EntryAssemblyPath)} did not exit within the allotted time.");
		}

		return process.ExitCode;
	}

	/// <summary>Starts the API without waiting for it — the caller owns the returned process.</summary>
	/// <param name="arguments">Arguments passed after the assembly path.</param>
	/// <param name="environment">Environment variables layered onto the child process.</param>
	public static Process Start(
		IEnumerable<string>? arguments = null,
		IDictionary<string, string>? environment = null)
	{
		ProcessStartInfo startInfo = new("dotnet")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		startInfo.ArgumentList.Add(EntryAssemblyPath);
		foreach (string argument in arguments ?? Array.Empty<string>())
		{
			startInfo.ArgumentList.Add(argument);
		}

		// A stale value inherited from the test runner would silently change what the child
		// binds or probes, so the caller's map is the whole truth for these keys.
		startInfo.Environment.Remove("ASPNETCORE_URLS");
		startInfo.Environment.Remove("WAYPOINT_HEALTHCHECK_URL");
		foreach (KeyValuePair<string, string> variable in environment ?? new Dictionary<string, string>())
		{
			startInfo.Environment[variable.Key] = variable.Value;
		}

		Process process = Process.Start(startInfo)
			?? throw new InvalidOperationException("Failed to start the Waypoint.Api child process.");

		// Drain both pipes: a full stdout buffer would block the child mid-startup.
		process.OutputDataReceived += (_, _) => { };
		process.ErrorDataReceived += (_, _) => { };
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		return process;
	}

	/// <summary>Terminates a still-running child process, ignoring an already-exited one.</summary>
	/// <param name="process">The process to stop.</param>
	public static void Kill(Process process)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
				process.WaitForExit(10_000);
			}
		}
		catch (InvalidOperationException)
		{
			// Already exited between the check and the kill — nothing to do.
		}
	}

	/// <summary>Reserves an unused loopback TCP port for a child process to bind.</summary>
	public static int GetFreePort()
	{
		using TcpListener listener = new(IPAddress.Loopback, 0);
		listener.Start();
		int port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}

	private static string ResolveEntryAssemblyPath()
	{
		string? configured = typeof(ApiProcess).Assembly
			.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(attribute => attribute.Key == EntryAssemblyMetadataKey)
			?.Value;

		if (string.IsNullOrWhiteSpace(configured))
		{
			throw new InvalidOperationException(
				$"Waypoint.Tests.csproj must define the \"{EntryAssemblyMetadataKey}\" assembly metadata.");
		}

		string fullPath = Path.GetFullPath(configured);
		if (!File.Exists(fullPath))
		{
			throw new FileNotFoundException(
				$"Waypoint.Api has not been built at the expected path. Run `dotnet build Waypoint.sln` first.",
				fullPath);
		}

		return fullPath;
	}
}
