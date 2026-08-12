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
	private readonly object _gate = new();
	private readonly ConcurrentDictionary<Guid, JobResourceProfile> _running = new();
	private readonly ILogger<ResourceAdmissionController> _logger;
	private double _admittedCpuCores;
	private long _admittedMemoryBytes;

	public ResourceAdmissionController(
		IOptions<RunnerResourceOptions> resourceOptions,
		CgroupResourceDiscovery discovery,
		ILogger<ResourceAdmissionController> logger)
	{
		ArgumentNullException.ThrowIfNull(resourceOptions);
		ArgumentNullException.ThrowIfNull(discovery);
		ArgumentNullException.ThrowIfNull(logger);

		_logger = logger;

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
				LogAdmissionDenied(jobId, jobType, profile.CpuCores, profile.MemoryBytes, _admittedCpuCores, _admittedMemoryBytes, EffectiveBudget.CpuCores, EffectiveBudget.MemoryBytes);
				return false;
			}

			_admittedCpuCores = projectedCpu;
			_admittedMemoryBytes = projectedMemory;
			_running[jobId] = profile;
			return true;
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
	private partial void LogAdmissionDenied(Guid jobId, string jobType, double profileCpu, long profileMemory, double admittedCpu, long admittedMemory, double budgetCpu, long budgetMemory);
}
