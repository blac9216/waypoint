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

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Waypoint.Runner.Readiness;

/// <summary>
/// The command-line argument every runner host's <c>--health-check</c> probe mode
/// recognizes (issue #461). Kept on a non-generic type -- unlike
/// <see cref="RunnerHealthCheckProbe{TReport}"/>'s <c>Run</c>/<c>IsHealthCheckInvocation</c>,
/// this constant and helper do not depend on the report shape, and CA1000 ("do not
/// declare static members on generic types") flags static members on a generic type
/// even when, as here, the type argument is never involved in resolving them.
/// </summary>
public static class RunnerHealthCheckProbe
{
	/// <summary>The command-line argument that switches the entry point into probe mode.</summary>
	public const string Argument = "--health-check";

	/// <summary>True when the process was started to probe health rather than to run the dispatcher.</summary>
	public static bool IsHealthCheckInvocation(string[] args) =>
		args.Any(argument => string.Equals(argument, Argument, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The container health probe shared by every runner host (issue #461: unifies what
/// were previously two independently-invented, near-identical implementations -- the
/// download-runner's <c>HealthCheckProbe</c> and the compliance-runner's
/// <c>RunnerHealthCheckProbe</c>), mirroring <c>Waypoint.Api.Diagnostics.HealthCheckProbe</c>'s
/// "the image owns how it reports health; the orchestrator merely invokes it"
/// convention, adapted for a process with no HTTP listener: ADR-0013 §1 gives a runner
/// host no REST/SSE surface, so <c>--health-check</c> reads the sentinel file
/// <see cref="RunnerReadinessReportingHostedService{TReport}"/> writes rather than
/// probing loopback, and exits <c>0</c> (ready) or <c>1</c> (not ready).
/// </summary>
/// <typeparam name="TReport">The host-specific, JSON-deserializable readiness report shape.</typeparam>
public static class RunnerHealthCheckProbe<TReport>
{
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNameCaseInsensitive = true
	};

	/// <summary>
	/// Reads the report at <paramref name="reportFilePath"/> and reports the verdict as
	/// an exit code. Never throws -- a missing/unparseable/stale file, or one whose
	/// <paramref name="isReady"/> extraction says not-ready, is unhealthy; nothing here
	/// can hang the probe.
	/// </summary>
	/// <param name="reportFilePath">Where <see cref="RunnerReadinessReportingHostedService{TReport}"/> writes its sentinel.</param>
	/// <param name="maxReportAge">How stale the report may be before it is treated as unhealthy regardless of content.</param>
	/// <param name="isReady">Extracts the report's ready/not-ready verdict.</param>
	/// <param name="timestamp">Extracts when the report was generated, for staleness comparison.</param>
	/// <param name="describeNotReady">Formats a human-readable reason for a not-ready or stale verdict, written to stderr.</param>
	[SuppressMessage(
		"Design",
		"CA1000:Do not declare static members on generic types",
		Justification = "Deliberate generic static probe factory (issue #461): callers always know TReport at the call site " +
			"(each runner host's own domain wrapper fixes it), and splitting the type-parameterless members onto a separate " +
			"class already happened for Argument/IsHealthCheckInvocation above -- Run's signature genuinely needs TReport.")]
	public static int Run(
		string reportFilePath,
		TimeSpan maxReportAge,
		Func<TReport, bool> isReady,
		Func<TReport, DateTimeOffset> timestamp,
		Func<TReport, string> describeNotReady) =>
		Run(reportFilePath, maxReportAge, isReady, timestamp, describeNotReady, DateTimeOffset.UtcNow);

	/// <summary>Testable overload taking an explicit "now" instead of reading the clock.</summary>
	[SuppressMessage(
		"Design",
		"CA1000:Do not declare static members on generic types",
		Justification = "Deliberate generic static probe factory (issue #461); see the other Run overload's justification.")]
	public static int Run(
		string reportFilePath,
		TimeSpan maxReportAge,
		Func<TReport, bool> isReady,
		Func<TReport, DateTimeOffset> timestamp,
		Func<TReport, string> describeNotReady,
		DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(isReady);
		ArgumentNullException.ThrowIfNull(timestamp);
		ArgumentNullException.ThrowIfNull(describeNotReady);

		try
		{
			if (!File.Exists(reportFilePath))
			{
				Console.Error.WriteLine($"health-check: report file '{reportFilePath}' does not exist yet");
				return 1;
			}

			string json = File.ReadAllText(reportFilePath);
			TReport? report = JsonSerializer.Deserialize<TReport>(json, SerializerOptions);
			if (report is null)
			{
				Console.Error.WriteLine($"health-check: report file '{reportFilePath}' did not parse");
				return 1;
			}

			TimeSpan age = now - timestamp(report);
			if (age > maxReportAge)
			{
				Console.Error.WriteLine(
					$"health-check: report file '{reportFilePath}' is stale ({age.TotalSeconds:F0}s old, max {maxReportAge.TotalSeconds:F0}s)");
				return 1;
			}

			if (!isReady(report))
			{
				Console.Error.WriteLine($"health-check: not ready ({describeNotReady(report)})");
				return 1;
			}

			return 0;
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine($"health-check: {exception.Message}");
			return 1;
		}
	}
}
