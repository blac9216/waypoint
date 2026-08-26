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
using System.Text;

namespace Waypoint.Tests.Support;

/// <summary>
/// Thread-safe sink for a child process's stdout+stderr. Both
/// <see cref="Process.OutputDataReceived"/> and <see cref="Process.ErrorDataReceived"/>
/// raise on pool threads, so appends are locked. Exists because a startup failure that
/// only reproduces on CI is otherwise completely undiagnosable: the pipes must be drained
/// (a full buffer blocks the child mid-startup), and draining into no-op handlers throws
/// the only evidence away.
/// </summary>
public sealed class ChildOutput
{
	private readonly StringBuilder _builder = new();
	private readonly object _gate = new();

	/// <summary>Everything the child has written to stdout or stderr so far.</summary>
	public string Text
	{
		get
		{
			lock (_gate)
			{
				return _builder.ToString();
			}
		}
	}

	internal void Append(string? line)
	{
		if (line is null)
		{
			return;
		}

		lock (_gate)
		{
			_builder.AppendLine(line);
		}
	}
}

/// <summary>
/// Launches one of this solution's host executables (<c>Waypoint.Api.dll</c>,
/// <c>Waypoint.ComplianceRunner.dll</c>, <c>Waypoint.DownloadRunner.dll</c>) as a real
/// child process. Some behaviours are only observable from outside the process — the exit
/// code a fatal startup failure produces, the message it prints on the way out, and the
/// container health probe's verdict — so an in-process
/// <c>WebApplicationFactory</c>/<c>HostApplicationBuilder</c> test cannot cover them.
///
/// The child's working directory is inherited from the test runner, which is the
/// <c>Waypoint.Tests</c> output directory — and that directory's <c>appsettings.json</c>
/// is whichever referenced host project's file won the copy race, NOT necessarily the one
/// belonging to the host under test. Callers must therefore pass every configuration
/// value their assertion depends on through <c>environment</c> (environment variables
/// outrank JSON files in the default configuration order) rather than relying on ambient
/// file discovery: that ambient assumption is exactly what made
/// <c>DatabasePasswordFileStartupTests</c> pass locally and fail on GitHub Actions.
/// </summary>
public static class HostProcess
{
	/// <summary>Starts a host without waiting for it — the caller owns the returned process.</summary>
	/// <param name="entryAssemblyPath">Absolute path of the host's runnable <c>.dll</c>.</param>
	/// <param name="arguments">Arguments passed after the assembly path.</param>
	/// <param name="environment">Environment variables layered onto the child process.</param>
	/// <param name="output">Optional sink capturing the child's stdout+stderr.</param>
	public static Process Start(
		string entryAssemblyPath,
		IEnumerable<string>? arguments = null,
		IDictionary<string, string>? environment = null,
		ChildOutput? output = null)
	{
		ProcessStartInfo startInfo = new("dotnet")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		startInfo.ArgumentList.Add(entryAssemblyPath);
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
			?? throw new InvalidOperationException(
				$"Failed to start the {Path.GetFileName(entryAssemblyPath)} child process.");

		// Drain both pipes: a full stdout buffer would block the child mid-startup. When a
		// sink is supplied the lines are kept instead of dropped (see ChildOutput).
		process.OutputDataReceived += (_, eventArgs) => output?.Append(eventArgs.Data);
		process.ErrorDataReceived += (_, eventArgs) => output?.Append(eventArgs.Data);
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		return process;
	}

	/// <summary>Runs a host to completion and returns its exit code.</summary>
	/// <param name="entryAssemblyPath">Absolute path of the host's runnable <c>.dll</c>.</param>
	/// <param name="arguments">Arguments passed after the assembly path.</param>
	/// <param name="environment">Environment variables layered onto the child process.</param>
	/// <param name="timeout">How long to wait before treating the run as hung.</param>
	/// <param name="output">Optional sink capturing the child's stdout+stderr.</param>
	public static int Run(
		string entryAssemblyPath,
		IEnumerable<string>? arguments = null,
		IDictionary<string, string>? environment = null,
		TimeSpan? timeout = null,
		ChildOutput? output = null)
	{
		using Process process = Start(entryAssemblyPath, arguments, environment, output);

		if (!process.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(60)).TotalMilliseconds))
		{
			Kill(process);
			throw new TimeoutException($"{Path.GetFileName(entryAssemblyPath)} did not exit within the allotted time.");
		}

		// The timed overload above returns as soon as the process is gone; the parameterless
		// one additionally waits for the async output handlers to flush, so a caller reading
		// ChildOutput.Text right after this sees the child's last lines.
		process.WaitForExit();
		return process.ExitCode;
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

	/// <summary>
	/// Resolves a host's runnable output path from the assembly metadata
	/// <c>Waypoint.Tests.csproj</c> emits for it.
	/// </summary>
	/// <param name="metadataKey">The <c>AssemblyMetadata</c> key naming the host.</param>
	public static string ResolveEntryAssemblyPath(string metadataKey)
	{
		string? configured = typeof(HostProcess).Assembly
			.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(attribute => attribute.Key == metadataKey)
			?.Value;

		if (string.IsNullOrWhiteSpace(configured))
		{
			throw new InvalidOperationException(
				$"Waypoint.Tests.csproj must define the \"{metadataKey}\" assembly metadata.");
		}

		string fullPath = Path.GetFullPath(configured);
		if (!File.Exists(fullPath))
		{
			throw new FileNotFoundException(
				"The host has not been built at the expected path. Run `dotnet build Waypoint.sln` first.",
				fullPath);
		}

		return fullPath;
	}
}
