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

using Waypoint.Runner.Readiness;

namespace Waypoint.DownloadRunner;

/// <summary>
/// The download-runner's container health probe (issue #441), the same
/// self-probing-binary convention <c>Waypoint.Api.Diagnostics.HealthCheckProbe</c>
/// documents ("the image owns how it reports health; the orchestrator merely invokes
/// it"), adapted for a process with no HTTP listener: ADR-0013 §1 gives this runner no
/// REST/SSE surface, so <c>dotnet Waypoint.DownloadRunner.dll --health-check</c> reads
/// the readiness marker file <see cref="ReadinessReportingHostedService"/> writes,
/// rather than probing loopback, and exits <c>0</c> (ready) or <c>1</c> (not ready).
///
/// A thin domain-specific wrapper around the shared
/// <see cref="RunnerHealthCheckProbe{TReport}"/> (issue #461): it supplies only the
/// <see cref="ReadinessSnapshot"/> type argument and how "ready"/"generated at"/"not
/// ready reason" are extracted from it.
/// </summary>
public static class HealthCheckProbe
{
	/// <summary>The command-line argument that switches the entry point into probe mode.</summary>
	public const string Argument = Waypoint.Runner.Readiness.RunnerHealthCheckProbe.Argument;

	/// <summary>True when the process was started to probe health rather than to run the dispatcher.</summary>
	public static bool IsHealthCheckInvocation(string[] args) =>
		Waypoint.Runner.Readiness.RunnerHealthCheckProbe.IsHealthCheckInvocation(args);

	/// <summary>
	/// Reads the readiness marker at <paramref name="readinessFilePath"/> and reports the
	/// verdict as an exit code. Never throws -- a missing/unparseable/stale file, or one
	/// reporting <c>Ready: false</c>, is unhealthy; nothing here can hang the probe.
	/// </summary>
	public static int Run(string readinessFilePath, TimeSpan maxAge) => RunAsync(readinessFilePath, maxAge, DateTimeOffset.UtcNow)
		.ConfigureAwait(false).GetAwaiter().GetResult();

	/// <summary>
	/// Public (not internal, unlike <c>Waypoint.Api.Diagnostics.HealthCheckProbe</c>'s
	/// equivalent) so tests do not need <c>InternalsVisibleTo</c> -- granting that here
	/// would make an unqualified <c>Program</c> reference in <c>Waypoint.Tests</c>
	/// ambiguous with <c>Waypoint.Api.Program</c> (see this project's .csproj comment).
	/// </summary>
	public static Task<int> RunAsync(string readinessFilePath, TimeSpan maxAge, DateTimeOffset now) => Task.FromResult(
		RunnerHealthCheckProbe<ReadinessSnapshot>.Run(
			readinessFilePath,
			maxAge,
			isReady: snapshot => snapshot.Ready,
			timestamp: snapshot => snapshot.GeneratedAt,
			describeNotReady: snapshot =>
				$"artifactStoreWritable={snapshot.ArtifactStoreWritable}, depotPathReadable={snapshot.DepotPathReadable}",
			now));
}
