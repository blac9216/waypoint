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

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Waypoint.Core.Jobs;

namespace Waypoint.Runner.Resources;

/// <summary>
/// Runner-local resource-aware admission (ADR-0014 §5, issue #437): tracks the summed
/// <see cref="JobResourceProfile"/> of every job this dispatcher currently has running
/// and decides, before a claim, whether one more of a given job type would push either
/// the CPU or memory sum past this runner's effective budget (discovered cgroup limits
/// intersected with any operator cap -- see <see cref="EffectiveBudget"/>).
///
/// <para>
/// <b>Admission happens before claiming</b> (issue #437 AC: "decide admission BEFORE
/// claiming... don't claim jobs you can't run"). This controller only ever answers "is
/// there room" against currently-tracked running jobs; it never touches the database
/// queue itself, so <c>FOR UPDATE SKIP LOCKED</c> claim safety across replicas is
/// unaffected -- two replicas each run their own independent instance of this
/// controller, tracking only the jobs each has itself admitted, exactly as
/// <see cref="JobDispatcherHostedService"/>'s existing per-process concurrency
/// semaphore already does today. Scaling replicas does not imply more host resources
/// (ADR-0014 §5): each replica's controller still bounds itself to what that replica's
/// own container was allocated.
/// </para>
///
/// <para>
/// Thread-safe: <see cref="TryAdmit"/>/<see cref="Release"/> are called from the
/// dispatcher's claim loop and job-completion paths respectively, potentially
/// concurrently once more than one job is in flight.
/// </para>
/// </summary>
public sealed partial class ResourceAdmissionController
{
	/// <summary>
	/// Issue #467: minimum time between Warning-level "admission denied" log lines for
	/// the *same* job type. A starved job type is denied on every dispatcher poll (as
	/// often as <c>PollInterval</c>) until budget frees up or an operator intervenes --
	/// logging every single denial at Warning would flood the log with an identical line
	/// forever. One line per type per this interval keeps the signal (something is
	/// starving) without the flood.
	/// </summary>
	private static readonly TimeSpan DenialWarningInterval = TimeSpan.FromMinutes(5);

	private readonly object _gate = new();
	private readonly ConcurrentDictionary<Guid, JobResourceProfile> _running = new();
	private readonly ConcurrentDictionary<string, DateTimeOffset> _lastDenialWarningAt = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, StarvedJobType> _starvedJobTypes = new(StringComparer.Ordinal);
	private readonly ILogger<ResourceAdmissionController> _logger;
	private readonly TimeProvider _timeProvider;
	private double _admittedCpuCores;
	private long _admittedMemoryBytes;

	public ResourceAdmissionController(
		IOptions<RunnerResourceOptions> resourceOptions,
		CgroupResourceDiscovery discovery,
		ILogger<ResourceAdmissionController> logger)
		: this(resourceOptions, discovery, logger, TimeProvider.System)
	{
	}

	/// <summary>
	/// Issue #467 test seam: lets <see cref="ResourceAdmissionControllerTests"/> control
	/// the clock the denial-warning rate limiter reads, rather than sleeping real time to
	/// exercise the "warn again after the interval elapses" branch. Production callers
	/// always resolve the public constructor above (<see cref="TimeProvider.System"/>).
	/// </summary>
	internal ResourceAdmissionController(
		IOptions<RunnerResourceOptions> resourceOptions,
		CgroupResourceDiscovery discovery,
		ILogger<ResourceAdmissionController> logger,
		TimeProvider timeProvider)
	{
		ArgumentNullException.ThrowIfNull(resourceOptions);
		ArgumentNullException.ThrowIfNull(discovery);
		ArgumentNullException.ThrowIfNull(logger);
		ArgumentNullException.ThrowIfNull(timeProvider);

		_logger = logger;
		_timeProvider = timeProvider;

		HostResourceLimits discovered = discovery.Discover();
		RunnerResourceOptions options = resourceOptions.Value;

		double cpuCap = options.MaxCpuCores is { } maxCpu ? Math.Min(discovered.CpuCores, maxCpu) : discovered.CpuCores;
		long memoryCap = options.MaxMemoryBytes is { } maxMemory ? Math.Min(discovered.MemoryBytes, maxMemory) : discovered.MemoryBytes;

		Discovered = discovered;
		EffectiveBudget = new HostResourceLimits(cpuCap, memoryCap, discovered.Source);

		LogEffectiveBudget(discovered.Source, discovered.CpuCores, discovered.MemoryBytes, cpuCap, memoryCap);
	}

	/// <summary>The raw discovery result (cgroup v2/v1/fallback), before operator caps are intersected in.</summary>
	public HostResourceLimits Discovered { get; }

	/// <summary>
	/// The budget admission actually enforces: <c>min(discovered, operator cap)</c> per
	/// resource. <see cref="HostResourceLimits.Source"/> mirrors <see cref="Discovered"/>'s
	/// -- an operator cap does not change where the underlying numbers came from, only
	/// how they were clamped.
	/// </summary>
	public HostResourceLimits EffectiveBudget { get; }

	/// <summary>CPU cores currently committed to admitted, still-running jobs.</summary>
	public double AdmittedCpuCores { get { lock (_gate) { return _admittedCpuCores; } } }

	/// <summary>Memory bytes currently committed to admitted, still-running jobs.</summary>
	public long AdmittedMemoryBytes { get { lock (_gate) { return _admittedMemoryBytes; } } }

	/// <summary>How many jobs this controller currently considers admitted/running.</summary>
	public int AdmittedJobCount => _running.Count;

	/// <summary>
	/// Attempts to admit one more job of <paramref name="jobType"/>. Returns <c>true</c>
	/// and records <paramref name="jobId"/> against its resolved
	/// <see cref="JobResourceProfile"/> only when both the CPU sum and the memory sum
	/// would remain within <see cref="EffectiveBudget"/> afterward -- a mixed-handler
	/// workload (e.g. several light <c>discover</c> jobs plus one heavy <c>scan</c>) is
	/// bounded on both axes independently, so neither axis can be oversubscribed by a
	/// combination that would have passed a CPU-only or memory-only check alone.
	///
	/// <para>
	/// A budget of exactly zero on either axis (a pathological operator cap, or a
	/// fallback misconfigured to zero) still admits the very first job if that job's
	/// own profile does not exceed the budget alone-check would forbid at a nonzero
	/// budget with nothing running; see remarks below for the "never wedge the runner"
	/// exception this deliberately does NOT provide -- an operator who caps a resource
	/// below any handler's profile has configured a runner that cannot run that job
	/// type at all, which is the correct (loud, in logs) outcome for that
	/// misconfiguration rather than silently overcommitting to break the cap.
	/// </para>
	/// </summary>
	public bool TryAdmit(Guid jobId, string jobType)
	{
		JobResourceProfile profile = JobResourceProfiles.ForJobType(jobType);

		lock (_gate)
		{
			double projectedCpu = _admittedCpuCores + profile.CpuCores;
			long projectedMemory = _admittedMemoryBytes + profile.MemoryBytes;

			if (projectedCpu > EffectiveBudget.CpuCores || projectedMemory > EffectiveBudget.MemoryBytes)
			{
				// Issue #467: "will never fit" (the profile alone exceeds the total
				// effective budget on either axis) is a permanent misconfiguration --
				// no amount of other jobs finishing ever frees enough room. "doesn't fit
				// right now" (the profile would fit in isolation, but other admitted jobs
				// are currently occupying the room) is transient and self-resolves once
				// something releases. Both are worth operator visibility (issue #467's
				// AC), but only the permanent case can never be fixed by waiting.
				bool permanent = profile.CpuCores > EffectiveBudget.CpuCores || profile.MemoryBytes > EffectiveBudget.MemoryBytes;
				_starvedJobTypes[jobType] = new StarvedJobType(jobType, permanent);
				MaybeLogAdmissionDenied(jobId, jobType, permanent, profile, EffectiveBudget.CpuCores, EffectiveBudget.MemoryBytes);
				return false;
			}

			_starvedJobTypes.TryRemove(jobType, out _);
			_admittedCpuCores = projectedCpu;
			_admittedMemoryBytes = projectedMemory;
			_running[jobId] = profile;
			return true;
		}
	}

	/// <summary>
	/// Job types currently denied admission, each tagged permanent (the profile alone
	/// exceeds the total effective budget -- no release of other jobs will ever help) or
	/// transient (would fit once currently-admitted jobs free up). Cleared for a job type
	/// the moment that type is next admitted; issue #467's operator-visibility surface for
	/// <c>RunnerCapacityReport</c>/<c>GET /system</c>.
	/// </summary>
	public IReadOnlyList<StarvedJobType> StarvedJobTypes => [.. _starvedJobTypes.Values];

	private void MaybeLogAdmissionDenied(Guid jobId, string jobType, bool permanent, JobResourceProfile profile, double budgetCpu, long budgetMemory)
	{
		LogAdmissionDeniedDebug(jobId, jobType, profile.CpuCores, profile.MemoryBytes, _admittedCpuCores, _admittedMemoryBytes, budgetCpu, budgetMemory);

		DateTimeOffset now = _timeProvider.GetUtcNow();
		DateTimeOffset lastWarned = _lastDenialWarningAt.GetOrAdd(jobType, DateTimeOffset.MinValue);
		if (now - lastWarned < DenialWarningInterval)
		{
			return;
		}

		_lastDenialWarningAt[jobType] = now;

		if (permanent)
		{
			LogAdmissionPermanentlyStarved(jobType, profile.CpuCores, profile.MemoryBytes, budgetCpu, budgetMemory);
		}
		else
		{
			LogAdmissionTransientlyStarved(jobType, profile.CpuCores, profile.MemoryBytes, _admittedCpuCores, _admittedMemoryBytes, budgetCpu, budgetMemory);
		}
	}

	/// <summary>
	/// Releases the resource budget an admitted job was holding. Safe to call at most
	/// once per <paramref name="jobId"/> that a prior <see cref="TryAdmit"/> returned
	/// <c>true</c> for; a <paramref name="jobId"/> not currently tracked (never
	/// admitted, or already released) is a no-op rather than throwing, so a
	/// defensive/duplicate release in a finally-block never crashes the dispatcher.
	/// </summary>
	public void Release(Guid jobId)
	{
		lock (_gate)
		{
			if (_running.TryRemove(jobId, out JobResourceProfile profile))
			{
				_admittedCpuCores -= profile.CpuCores;
				_admittedMemoryBytes -= profile.MemoryBytes;
			}
		}
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Resource admission budget: source={Source}, discovered={DiscoveredCpu} cores / {DiscoveredMemory} bytes, effective (post-cap)={EffectiveCpu} cores / {EffectiveMemory} bytes")]
	private partial void LogEffectiveBudget(HostResourceLimitSource source, double discoveredCpu, long discoveredMemory, double effectiveCpu, long effectiveMemory);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Admission denied for job {JobId} ({JobType}): profile {ProfileCpu} cores / {ProfileMemory} bytes would push admitted {AdmittedCpu} cores / {AdmittedMemory} bytes past budget {BudgetCpu} cores / {BudgetMemory} bytes")]
	private partial void LogAdmissionDeniedDebug(Guid jobId, string jobType, double profileCpu, long profileMemory, double admittedCpu, long admittedMemory, double budgetCpu, long budgetMemory);

	// Issue #467: Warning-level, rate-limited (DenialWarningInterval) per job type -- see
	// MaybeLogAdmissionDenied. Two distinct messages so "will never fit" and "doesn't fit
	// right now" read unambiguously in a log search rather than requiring the reader to
	// interpret a shared "denied" line's numbers.
	[LoggerMessage(Level = LogLevel.Warning, Message = "Job type '{JobType}' can never be admitted on this runner: its profile ({ProfileCpu} cores / {ProfileMemory} bytes) exceeds the total effective budget ({BudgetCpu} cores / {BudgetMemory} bytes). This is a permanent misconfiguration -- raise the operator resource cap/fallback or move this job type to a larger runner.")]
	private partial void LogAdmissionPermanentlyStarved(string jobType, double profileCpu, long profileMemory, double budgetCpu, long budgetMemory);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Job type '{JobType}' is being denied admission: its profile ({ProfileCpu} cores / {ProfileMemory} bytes) does not fit alongside {AdmittedCpu} cores / {AdmittedMemory} bytes already admitted, within budget {BudgetCpu} cores / {BudgetMemory} bytes. This is transient -- admission will resume once running jobs release enough budget.")]
	private partial void LogAdmissionTransientlyStarved(string jobType, double profileCpu, long profileMemory, double admittedCpu, long admittedMemory, double budgetCpu, long budgetMemory);
}

/// <summary>
/// One job type currently unable to be admitted on this runner (issue #467), with
/// <see cref="Permanent"/> distinguishing a budget the type can never fit (the profile
/// alone exceeds the total effective budget -- an operator misconfiguration, not a
/// contention issue) from a type that would fit once other admitted jobs release their
/// budget.
/// </summary>
public readonly record struct StarvedJobType(string JobType, bool Permanent);
