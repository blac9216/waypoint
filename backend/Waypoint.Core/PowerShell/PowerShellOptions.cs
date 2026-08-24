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
}
