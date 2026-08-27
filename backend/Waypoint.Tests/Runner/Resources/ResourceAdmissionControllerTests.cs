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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Waypoint.Runner.Resources;
using Xunit;

namespace Waypoint.Tests.Runner.Resources;

/// <summary>
/// Issue #437's admission acceptance criteria: both the CPU and memory budgets must
/// independently bound admission, mixed handler profiles must not oversubscribe either
/// axis, and an operator cap intersects with (never widens past) the discovered budget.
///
/// ADR-0018 (issue #555) inserted host-derived capacity ahead of the constant fallback
/// in <c>CgroupResourceDiscovery</c>; every controller built here supplies an
/// unusable-by-design fake <see cref="IHostCapabilitySource"/> so an empty cgroup root
/// still resolves deterministically to the configured
/// <see cref="RunnerResourceOptions.FallbackCpuCores"/>/<c>FallbackMemoryBytes"</c>
/// constants these tests assert against, rather than the real (and non-deterministic
/// across test hosts) machine CPU/memory <c>SystemHostCapabilitySource</c> would report.
/// </summary>
public sealed class ResourceAdmissionControllerTests : IDisposable
{
	private readonly string _emptyCgroupRoot = Directory.CreateTempSubdirectory("waypoint-admission-test-").FullName;

	public void Dispose()
	{
		if (Directory.Exists(_emptyCgroupRoot))
		{
			Directory.Delete(_emptyCgroupRoot, recursive: true);
		}
	}

	private ResourceAdmissionController CreateController(RunnerResourceOptions? options = null)
	{
		RunnerResourceOptions effective = options ?? new RunnerResourceOptions();
		effective.CgroupRoot = _emptyCgroupRoot; // Always empty -> always falls back to the configured defaults below, deterministically.
		CgroupResourceDiscovery discovery = new(Options.Create(effective), new UnusableHostCapabilitySource(), NullLogger<CgroupResourceDiscovery>.Instance);
		return new ResourceAdmissionController(Options.Create(effective), discovery, NullLogger<ResourceAdmissionController>.Instance);
	}

	private (ResourceAdmissionController Controller, RecordingLogger Logger, ManualTimeProvider TimeProvider) CreateControllerWithLogging(RunnerResourceOptions? options = null)
	{
		RunnerResourceOptions effective = options ?? new RunnerResourceOptions();
		effective.CgroupRoot = _emptyCgroupRoot;
		CgroupResourceDiscovery discovery = new(Options.Create(effective), new UnusableHostCapabilitySource(), NullLogger<CgroupResourceDiscovery>.Instance);
		RecordingLogger logger = new();
		ManualTimeProvider timeProvider = new();
		ResourceAdmissionController controller = new(Options.Create(effective), discovery, logger, timeProvider);
		return (controller, logger, timeProvider);
	}

	/// <summary>Zero/zero always fails <c>CgroupResourceDiscovery</c>'s host-derivation usability check, forcing the constant fallback.</summary>
	private sealed class UnusableHostCapabilitySource : IHostCapabilitySource
	{
		public double AvailableCpuCores() => 0;

		public long TotalMemoryBytes() => 0;
	}

	[Fact]
	public void FirstJobWithinBudget_IsAdmitted()
	{
		ResourceAdmissionController controller = CreateController(new RunnerResourceOptions
		{
			FallbackCpuCores = 4.0,
			FallbackMemoryBytes = 4L * 1024 * 1024 * 1024,
		});

		bool admitted = controller.TryAdmit(Guid.NewGuid(), "scan"); // 2.0 cores / 1 GiB per JobResourceProfiles.
		Assert.True(admitted);
		Assert.Equal(1, controller.AdmittedJobCount);
	}

	[Fact]
	public void CpuBudgetExhausted_DeniesFurtherAdmission()
	{
		ResourceAdmissionController controller = CreateController(new RunnerResourceOptions
		{
			FallbackCpuCores = 2.0, // Exactly one "scan" (2.0 cores) worth of CPU.
			FallbackMemoryBytes = 16L * 1024 * 1024 * 1024, // Memory is not the constraint here.
		});

		Assert.True(controller.TryAdmit(Guid.NewGuid(), "scan"));
		Assert.False(controller.TryAdmit(Guid.NewGuid(), "discover")); // Even a light 0.25-core job no longer fits.
	}

	[Fact]
	public void MemoryBudgetExhausted_DeniesFurtherAdmissionEvenWithCpuHeadroom()
	{
		ResourceAdmissionController controller = CreateController(new RunnerResourceOptions
		{
			FallbackCpuCores = 16.0, // CPU is not the constraint here.
			FallbackMemoryBytes = 1024L * 1024 * 1024, // Exactly one "scan" (1 GiB) worth of memory.
		});

		Assert.True(controller.TryAdmit(Guid.NewGuid(), "scan"));
		Assert.False(controller.TryAdmit(Guid.NewGuid(), "discover")); // 128 MiB more would exceed the 1 GiB memory budget.
	}

	[Fact]
	public void MixedHandlerProfiles_CannotOversubscribeEitherAxis()
	{
		// Budget sized for exactly: one scan (2.0 cores / 1 GiB) + one download (0.5
		// cores / 512 MiB) + one discover (0.25 cores / 128 MiB) = 2.75 cores / 1664 MiB.
		ResourceAdmissionController controller = CreateController(new RunnerResourceOptions
		{
			FallbackCpuCores = 2.75,
			FallbackMemoryBytes = (1024L + 512 + 128) * 1024 * 1024,
		});

		Guid scanId = Guid.NewGuid();
		Guid downloadId = Guid.NewGuid();
		Guid discoverId = Guid.NewGuid();

		Assert.True(controller.TryAdmit(scanId, "scan"));
		Assert.True(controller.TryAdmit(downloadId, "download"));
		Assert.True(controller.TryAdmit(discoverId, "discover"));

		// The budget is now exactly exhausted on both axes -- one more of even the
		// lightest job type must be denied.
		Assert.False(controller.TryAdmit(Guid.NewGuid(), "credential-test"));
		Assert.Equal(3, controller.AdmittedJobCount);

		// Releasing the heaviest job frees enough of both axes for another scan.
		controller.Release(scanId);
		Assert.True(controller.TryAdmit(Guid.NewGuid(), "scan"));
	}

	[Fact]
	public void Release_FreesBudgetForASubsequentAdmission()
	{
		ResourceAdmissionController controller = CreateController(new RunnerResourceOptions
		{
			FallbackCpuCores = 2.0,
			FallbackMemoryBytes = 1024L * 1024 * 1024,
		});

		Guid jobId = Guid.NewGuid();
		Assert.True(controller.TryAdmit(jobId, "scan"));
		Assert.False(controller.TryAdmit(Guid.NewGuid(), "scan"));

		controller.Release(jobId);

		Assert.Equal(0, controller.AdmittedJobCount);
		Assert.True(controller.TryAdmit(Guid.NewGuid(), "scan"));
	}

	[Fact]
	public void Release_UnknownJobId_IsANoOp()
	{
		ResourceAdmissionController controller = CreateController();

		// Never admitted, and releasing twice -- neither should throw or go negative.
		controller.Release(Guid.NewGuid());
		Assert.Equal(0, controller.AdmittedJobCount);

		Guid jobId = Guid.NewGuid();
		controller.TryAdmit(jobId, "discover");
		controller.Release(jobId);
		controller.Release(jobId);
		Assert.Equal(0, controller.AdmittedJobCount);
		Assert.Equal(0, controller.AdmittedCpuCores);
	}

	[Fact]
	public void UnknownJobType_FallsBackToTheDefaultProfileRatherThanThrowing()
	{
		ResourceAdmissionController controller = CreateController(new RunnerResourceOptions
		{
			FallbackCpuCores = 1.0,
			FallbackMemoryBytes = 512L * 1024 * 1024,
		});

		// "unregistered-type" is not one of jobs_job_type_check's values, but
		// TryAdmit/JobResourceProfiles must degrade to JobResourceProfiles.Default
		// rather than throwing over an accounting lookup miss.
		Assert.True(controller.TryAdmit(Guid.NewGuid(), "unregistered-type"));
	}

	[Fact]
	public void OperatorCap_IntersectsWithDiscoveredBudgetRatherThanWideningIt()
	{
		// Discovered (fallback) budget is generous; the operator cap is the binding
		// constraint -- EffectiveBudget must reflect min(discovered, cap), not the cap
		// alone or the discovered value alone.
		ResourceAdmissionController controller = CreateController(new RunnerResourceOptions
		{
			FallbackCpuCores = 16.0,
			FallbackMemoryBytes = 32L * 1024 * 1024 * 1024,
			MaxCpuCores = 2.0,
			MaxMemoryBytes = 1024L * 1024 * 1024,
		});

		Assert.Equal(2.0, controller.EffectiveBudget.CpuCores, precision: 6);
		Assert.Equal(1024L * 1024 * 1024, controller.EffectiveBudget.MemoryBytes);

		Assert.True(controller.TryAdmit(Guid.NewGuid(), "scan")); // Exactly fills the capped budget.
		Assert.False(controller.TryAdmit(Guid.NewGuid(), "discover")); // The generous discovered budget alone would have allowed this; the cap must not.
	}

	[Fact]
	public void OperatorCapLargerThanDiscovered_DoesNotWidenTheBudget()
	{
		// A cap above the discovered/fallback value must not raise the effective
		// budget past what was actually discovered.
		ResourceAdmissionController controller = CreateController(new RunnerResourceOptions
		{
			FallbackCpuCores = 1.0,
			FallbackMemoryBytes = 512L * 1024 * 1024,
			MaxCpuCores = 100.0,
			MaxMemoryBytes = 100L * 1024 * 1024 * 1024,
		});

		Assert.Equal(1.0, controller.EffectiveBudget.CpuCores, precision: 6);
		Assert.Equal(512L * 1024 * 1024, controller.EffectiveBudget.MemoryBytes);
	}

	[Fact]
	public void DiscoveredAndEffectiveBudget_AreExposedForHealthDiagnostics()
	{
		ResourceAdmissionController controller = CreateController(new RunnerResourceOptions
		{
			FallbackCpuCores = 3.0,
			FallbackMemoryBytes = 2L * 1024 * 1024 * 1024,
		});

		Assert.Equal(HostResourceLimitSource.Fallback, controller.Discovered.Source);
		Assert.True(controller.Discovered.IsFallback);
		Assert.Equal(3.0, controller.Discovered.CpuCores, precision: 6);
		Assert.Equal(0, controller.AdmittedJobCount);
		Assert.Equal(0.0, controller.AdmittedCpuCores);
		Assert.Equal(0L, controller.AdmittedMemoryBytes);
	}

	// Issue #467: admission starvation must be operator-visible -- a Warning-level log
	// (rate-limited per job type) plus a live StarvedJobTypes snapshot distinguishing
	// "will never fit" (permanent) from "doesn't fit right now" (transient).

	[Fact]
	public void PermanentlyOversizedJobType_IsFlaggedPermanentAndLogsWarningOnFirstDenial()
	{
		// scan's 2.0-core profile exceeds this runner's entire 1.0-core budget -- no
		// amount of waiting ever admits it.
		(ResourceAdmissionController controller, RecordingLogger logger, _) = CreateControllerWithLogging(new RunnerResourceOptions
		{
			FallbackCpuCores = 1.0,
			FallbackMemoryBytes = 4L * 1024 * 1024 * 1024,
		});

		Assert.False(controller.TryAdmit(Guid.NewGuid(), "scan"));

		StarvedJobType starved = Assert.Single(controller.StarvedJobTypes);
		Assert.Equal("scan", starved.JobType);
		Assert.True(starved.Permanent);

		Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("scan", StringComparison.Ordinal) && e.Message.Contains("never be admitted", StringComparison.Ordinal));
	}

	[Fact]
	public void TransientlyOversubscribedJobType_IsFlaggedTransientNotPermanent()
	{
		// discover (0.25 cores) fits in isolation within a 2.0-core budget, but scan
		// (2.0 cores) already occupies all of it -- this is contention, not a
		// misconfiguration, so it must resolve once scan releases.
		(ResourceAdmissionController controller, RecordingLogger logger, _) = CreateControllerWithLogging(new RunnerResourceOptions
		{
			FallbackCpuCores = 2.0,
			FallbackMemoryBytes = 4L * 1024 * 1024 * 1024,
		});

		Guid scanId = Guid.NewGuid();
		Assert.True(controller.TryAdmit(scanId, "scan"));
		Assert.False(controller.TryAdmit(Guid.NewGuid(), "discover"));

		StarvedJobType starved = Assert.Single(controller.StarvedJobTypes);
		Assert.Equal("discover", starved.JobType);
		Assert.False(starved.Permanent);

		Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("discover", StringComparison.Ordinal) && e.Message.Contains("transient", StringComparison.Ordinal));

		// Releasing scan frees the budget -- discover is no longer starved once admitted.
		controller.Release(scanId);
		Assert.True(controller.TryAdmit(Guid.NewGuid(), "discover"));
		Assert.Empty(controller.StarvedJobTypes);
	}

	[Fact]
	public void RepeatedDenialsOfTheSameJobType_WarnOnceThenRateLimit()
	{
		(ResourceAdmissionController controller, RecordingLogger logger, ManualTimeProvider timeProvider) = CreateControllerWithLogging(new RunnerResourceOptions
		{
			FallbackCpuCores = 1.0,
			FallbackMemoryBytes = 4L * 1024 * 1024 * 1024,
		});

		// Every dispatcher poll re-denies the same starved type -- must not re-warn on
		// every single call within the rate-limit interval.
		for (int i = 0; i < 5; i++)
		{
			Assert.False(controller.TryAdmit(Guid.NewGuid(), "scan"));
		}

		int warningsAfterBurst = logger.Entries.Count(e => e.Level == LogLevel.Warning);
		Assert.Equal(1, warningsAfterBurst);

		// Debug-level denial logging is unaffected by the rate limiter -- every attempt
		// still gets a Debug line for detailed diagnosis.
		Assert.Equal(5, logger.Entries.Count(e => e.Level == LogLevel.Debug));

		// Advance past the rate-limit interval -- the next denial warns again.
		timeProvider.Advance(TimeSpan.FromMinutes(6));
		Assert.False(controller.TryAdmit(Guid.NewGuid(), "scan"));

		Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Warning));
	}

	[Fact]
	public void DifferentStarvedJobTypes_EachGetTheirOwnRateLimit()
	{
		// remediate (1.5 cores) also exceeds a 1.0-core budget alongside scan (2.0
		// cores) -- both are permanently starved, and each must warn independently on
		// first denial rather than sharing one global rate limit.
		(ResourceAdmissionController controller, RecordingLogger logger, _) = CreateControllerWithLogging(new RunnerResourceOptions
		{
			FallbackCpuCores = 1.0,
			FallbackMemoryBytes = 4L * 1024 * 1024 * 1024,
		});

		Assert.False(controller.TryAdmit(Guid.NewGuid(), "scan"));
		Assert.False(controller.TryAdmit(Guid.NewGuid(), "remediate"));

		Assert.Equal(2, controller.StarvedJobTypes.Count);
		Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Warning));
	}

	/// <summary>
	/// Issue #737 (epic #726 Wave 2 capstone, ADR-0024 "Resource admission applies to
	/// real component jobs"): the <c>scanComponentTransport</c> overload admits MORE
	/// component-granular `scan` jobs within the same budget than the flat `scan`
	/// weight would, because <see cref="Waypoint.Core.Jobs.JobResourceProfiles.ResolveScanComponentProfile"/>'s
	/// per-transport weight is lighter than <see cref="Waypoint.Core.Jobs.JobResourceProfiles.ForJobType"/>'s
	/// flat `scan` entry (2.0 cores) -- proving admission actually reads the finer
	/// profile rather than silently ignoring the transport hint.
	/// </summary>
	[Fact]
	public void TryAdmit_WithScanComponentTransport_UsesLighterPerTransportProfile()
	{
		ResourceAdmissionController controller = CreateController(new RunnerResourceOptions
		{
			// 1.0 CPU core: too small for the flat `scan` weight (2.0 cores) but large
			// enough for the `vmware` component weight (0.5 cores) with room to spare
			// for a second one.
			FallbackCpuCores = 1.0,
			FallbackMemoryBytes = 4L * 1024 * 1024 * 1024,
		});

		Assert.False(controller.TryAdmit(Guid.NewGuid(), "scan"));

		bool admittedFirst = controller.TryAdmit(Guid.NewGuid(), "scan", "vmware");
		bool admittedSecond = controller.TryAdmit(Guid.NewGuid(), "scan", "vmware");
		Assert.True(admittedFirst);
		Assert.True(admittedSecond);
		Assert.Equal(2, controller.AdmittedJobCount);
	}

	[Fact]
	public void TryAdmit_WithNullScanComponentTransport_BehavesExactlyLikeTheFlatOverload()
	{
		ResourceAdmissionController controller = CreateController(new RunnerResourceOptions
		{
			FallbackCpuCores = 4.0,
			FallbackMemoryBytes = 4L * 1024 * 1024 * 1024,
		});

		bool admitted = controller.TryAdmit(Guid.NewGuid(), "scan", scanComponentTransport: null);
		Assert.True(admitted);
		Assert.Equal(1, controller.AdmittedJobCount);
	}

	private sealed class ManualTimeProvider : TimeProvider
	{
		private DateTimeOffset _now = DateTimeOffset.UtcNow;

		public override DateTimeOffset GetUtcNow() => _now;

		public void Advance(TimeSpan by) => _now += by;
	}

	private sealed class RecordingLogger : ILogger<ResourceAdmissionController>
	{
		public List<(LogLevel Level, string Message)> Entries { get; } = [];

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			Entries.Add((logLevel, formatter(state, exception)));
		}
	}
}
