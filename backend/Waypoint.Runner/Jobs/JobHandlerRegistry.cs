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

using Waypoint.Core.Jobs;

namespace Waypoint.Runner.Jobs;

/// <summary>
/// Resolves the <see cref="IJobHandler"/> registered for a claimed job's
/// <c>job_type</c>, and is this runner host's single mandatory capability
/// registration point (issue #436): the constructor fails closed with no ambiguity
/// left for startup to silently ignore.
///
/// <list type="bullet">
/// <item><description><paramref name="allowedJobTypes"/> must be non-null and
/// non-empty -- an empty or missing allowlist means this process would claim
/// nothing, which is very likely a misconfiguration rather than an intentional
/// idle runner, and <see cref="JobDispatcherHostedService"/> requires exactly this
/// set be passed to every claim per ADR-0014 (never claim globally, never filter
/// after claiming).</description></item>
/// <item><description>Two handlers registering the same <c>job_type</c> throw
/// rather than the second silently overwriting the first -- a duplicate handler
/// registration is an authoring mistake (e.g. both a domain module and a test
/// fixture registering the same job type in the same host) that should fail
/// startup, not resolve to "whichever handler DI enumerated last".</description></item>
/// </list>
///
/// A claimed job whose type has no registered handler still fails cleanly at
/// dispatch time (see <see cref="JobDispatcherHostedService"/>) rather than
/// hanging -- that remains a per-job runtime outcome, not a startup failure,
/// because a runner's allowlist may legitimately list job types that do not yet
/// have a handler (see <see cref="JobCapabilities"/>'s "later" types).
/// </summary>
public sealed class JobHandlerRegistry
{
	private readonly Dictionary<string, IJobHandler> _handlersByJobType;

	/// <summary>
	/// Convenience overload for tests and the current unsplit dispatcher that still
	/// wants the full <see cref="JobCapabilities.All"/> allowlist. Prefer the
	/// two-argument constructor for anything that models a real runner's
	/// capabilities.
	/// </summary>
	public JobHandlerRegistry(IEnumerable<IJobHandler> handlers)
		: this(handlers, JobCapabilities.All)
	{
	}

	public JobHandlerRegistry(IEnumerable<IJobHandler> handlers, IReadOnlySet<string> allowedJobTypes)
	{
		ArgumentNullException.ThrowIfNull(handlers);
		ArgumentNullException.ThrowIfNull(allowedJobTypes);
		if (allowedJobTypes.Count == 0)
		{
			throw new InvalidOperationException(
				"Runner host startup failed: no job-type allowlist was registered. " +
				"A runner must be given a non-empty JobCapabilities set (see ADR-0013 SS2/ADR-0014 SS1) " +
				"-- register one explicitly rather than letting the runner claim nothing.");
		}

		Dictionary<string, IJobHandler> map = new(StringComparer.Ordinal);
		foreach (IJobHandler handler in handlers)
		{
			if (!map.TryAdd(handler.JobType, handler))
			{
				throw new InvalidOperationException(
					$"Runner host startup failed: more than one IJobHandler is registered for job type '{handler.JobType}'. " +
					"Each job type may have exactly one handler in a given runner host.");
			}
		}

		_handlersByJobType = map;
		AllowedJobTypes = allowedJobTypes;
	}

	/// <summary>
	/// This host's job-type allowlist (ADR-0014 SS1) -- the set
	/// <see cref="JobDispatcherHostedService"/> passes to every
	/// <see cref="IJobRunnerRepository.ClaimJobAsync"/> call, never widened or
	/// filtered after the fact.
	/// </summary>
	public IReadOnlySet<string> AllowedJobTypes { get; }

	public bool TryResolve(string jobType, out IJobHandler? handler) => _handlersByJobType.TryGetValue(jobType, out handler);
}
