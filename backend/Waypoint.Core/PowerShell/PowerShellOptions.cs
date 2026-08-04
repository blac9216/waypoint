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
	/// </summary>
	public IList<string> ModulePreloadPaths { get; } = [];

	/// <summary>
	/// Case-insensitive substrings of a failure reason that classify it as an
	/// authentication failure (-> <c>auth-failed</c>, feeding the #5 consecutive-
	/// failure queue halt) rather than an ordinary failure. Deliberately coarse:
	/// the vcf modules surface vendor/HTTP wording, not typed exceptions, and a
	/// false 'failed' merely skips the halt while a false 'auth-failed' only
	/// counts toward it -- three in a row are required to act.
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
