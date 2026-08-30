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

using System.ComponentModel.DataAnnotations;

namespace Waypoint.Core.PowerShell;

/// <summary>Configuration for the in-process PowerShell host (ADR-0006, epic #6 slice 2).</summary>
public sealed class PowerShellOptions
{
	public const string SectionName = "PowerShell";

	/// <summary>
	/// Maximum concurrently-executing runspaces. Default matches
	/// <see cref="Jobs.JobEngineOptions.MaxConcurrency"/>'s default (4): the job
	/// dispatcher is the only intended caller, so a larger pool would only idle.
	/// </summary>
	public int MaxRunspaces { get; set; } = 4;

	/// <summary>
	/// Module paths (or names resolvable via PSModulePath) imported into every
	/// runspace's initial session state. In production these are the
	/// vcf-docker-download modules; tests point this at an invented stub module.
	/// These are disk paths the compliance runner's readiness check verifies exist.
	/// </summary>
	public IList<string> ModulePreloadPaths { get; } = [];

	/// <summary>
	/// Module NAMES (not disk paths) imported into every runspace's initial session
	/// state, resolved via PSModulePath. Separate from <see cref="ModulePreloadPaths"/>
	/// because these are not files under <c>/app</c> the readiness check can stat --
	/// they live in the image's system module tree.
	///
	/// Issue #629/#618: the compliance runner names <c>VMware.PowerCLI</c> here so the
	/// full meta-module is imported into every pooled runspace up front. Without it,
	/// PowerCLI cmdlets (<c>Get-Cluster</c>/<c>Get-VMHost</c>/<c>Get-VM</c>) run under
	/// partial cmdlet-autoload -- the same profile-never-ran state issue #307 documents
	/// -- and in the runner's in-process (non-pwsh) host that partial state produces
	/// discovery <c>[pscustomobject]</c> rows whose NoteProperties do not survive the
	/// executor's output capture, so live discovery silently persisted zero inventory.
	/// A full meta-module import (mirroring the sibling repo's
	/// <c>Test-EnvironmentDependencies</c> self-heal) makes the objects hydrate
	/// correctly; verified live against a populated vCenter.
	/// </summary>
	public IList<string> ModulePreloadNames { get; } = [];

	/// <summary>
	/// Case-insensitive markers that classify a failure reason as an authentication
	/// failure (-> <c>auth-failed</c>, feeding the #5 consecutive-failure queue halt)
	/// rather than an ordinary failure. Word markers (<c>"unauthorized"</c>, etc.) match
	/// as substrings -- vendor/HTTP wording, not typed exceptions. Markers that are bare
	/// digit runs (<c>"401"</c>, <c>"403"</c>) are matched only as a standalone token
	/// (<c>\b401\b</c>), not as a substring: a plain substring match on three digits also
	/// fires on GUIDs, byte counts, ports, and other identifiers that happen to embed
	/// those digits, which would misclassify a deterministic ordinary failure as
	/// auth-failed on every retry and durably three-strike a healthy credential (#162).
	/// A false 'failed' merely skips the halt while a false 'auth-failed' counts toward
	/// it -- three in a row are required to act.
	/// </summary>
	public IList<string> AuthFailureMarkers { get; } =
	[
		"401", "403", "unauthorized", "forbidden", "authentication failed",
		"invalid credential", "incorrect user", "login failed", "access denied",
	];

	/// <summary>Per-invocation wall-clock budget when the request does not carry its own.</summary>
	public TimeSpan DefaultInvocationTimeout { get; set; } = TimeSpan.FromMinutes(30);

	/// <summary>
	/// How long a timed-out pipeline gets to honor Stop() before its runspace is
	/// abandoned as poisoned and replaced. Short on purpose: a pipeline that ignores
	/// Stop for 5 s is exactly the hung-native-call case that cannot be waited out.
	/// </summary>
	public TimeSpan StopGracePeriod { get; set; } = TimeSpan.FromSeconds(5);

	/// <summary>
	/// Issue #1305: ceiling (milliseconds) <see cref="Waypoint.Infrastructure.Discovery.DiscoverJobHandler"/>
	/// passes as <c>Invoke-WaypointDiscovery -DnsTimeoutMilliseconds</c>, bounding
	/// every DNS lookup <c>WaypointDiscovery.psm1</c> performs while resolving the
	/// vCenter session identity (<c>Resolve-WaypointPrimarySession</c>). Default
	/// matches the module's own <c>$script:WaypointDnsTimeoutMillisecondsDefault</c>
	/// (issue #1251/#1297) -- long enough that a healthy resolver always answers,
	/// short enough that a blackholed one cannot stall a discovery pass. Raise this
	/// only when the deployment's own resolver is genuinely slower than that; a
	/// lookup that exceeds it emits a job.log warning naming the lookup kind and
	/// host (see the module's own doc comment), so raising the ceiling is a
	/// deliberate operator choice rather than a silent workaround.
	/// </summary>
	/// <remarks>
	/// Accepted range: 100-60000 ms, enforced by <see cref="RangeAttribute"/> and by
	/// the <c>.ValidateDataAnnotations().ValidateOnStart()</c> on this options
	/// registration, so an out-of-range value fails the runner at startup with a named
	/// message instead of silently at the first lookup. Both bounds exist because this
	/// option's only job is to bound a hang, and the values just outside them defeat
	/// that job invisibly: <c>-1</c> is <c>Timeout.Infinite</c> (the .NET-idiomatic
	/// "no limit", and exactly what an operator disabling the ceiling would type), so
	/// <c>Task.Wait(-1)</c> reinstates the unbounded stall issue #1251/#1297 removed
	/// and never reaches the warning branch; <c>-2</c> and below throw
	/// <see cref="ArgumentOutOfRangeException"/>, which the module's fail-open
	/// <c>catch { return @() }</c> swallows, disabling DNS matching for the whole pass
	/// with no warning at all; and <c>0</c> makes every lookup instantly "time out".
	/// The 100 ms floor is above any plausible healthy-resolver round trip on a LAN, so
	/// no legitimate deployment is excluded by it; the 60 s ceiling is longer than any
	/// resolver worth waiting for and well under the job-level invocation timeout, so a
	/// fat-fingered value cannot outlive the job that set it. (The module's own
	/// <c>[ValidateRange(1, 60000)]</c> floor is 1 ms rather than 100 ms: any positive
	/// value is safe there -- it cannot hang -- and the module's tests legitimately
	/// drive 50 ms ceilings to make the timeout branch fire fast. 100 ms is an
	/// operator-facing sanity floor, not a safety one.)
	/// </remarks>
	[Range(100, 60000)]
	public int DiscoveryDnsTimeoutMilliseconds { get; set; } = 3000;
}
