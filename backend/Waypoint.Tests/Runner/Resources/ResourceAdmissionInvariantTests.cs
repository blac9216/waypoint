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

using Waypoint.Runner.Resources;
using Xunit;

namespace Waypoint.Tests.Runner.Resources;

/// <summary>
/// ADR-0018 (issue #555)'s startup admission invariant: "no advertised core job type
/// may be permanently inadmissible; fail readiness with an exact config action
/// otherwise" (owner ruling, 2026-08-23). Mirrors
/// <see cref="ResourceAdmissionController.TryAdmit"/>'s existing "permanent" vs
/// "transient" starvation distinction (issue #467) -- this only ever checks the
/// permanent case, since it runs once at startup before anything is admitted.
/// </summary>
public sealed class ResourceAdmissionInvariantTests
{
	[Fact]
	public void EveryAdvertisedJobTypeFitsWithinBudget_DoesNotThrow()
	{
		// "scan" (2.0 cores / 1 GiB, the heaviest compliance job type) fits comfortably.
		HostResourceLimits budget = new(CpuCores: 4.0, MemoryBytes: 4L * 1024 * 1024 * 1024, HostResourceLimitSource.HostDerived);

		ResourceAdmissionInvariant.Validate(
			new HashSet<string>(StringComparer.Ordinal) { "discover", "credential-test", "scan" },
			budget);
	}

	[Fact]
	public void AdvertisedJobTypeExceedsCpuBudget_ThrowsWithActionableDiagnostic()
	{
		// "scan" needs 2.0 cores; this runner's effective budget is only 1.0.
		HostResourceLimits budget = new(CpuCores: 1.0, MemoryBytes: 4L * 1024 * 1024 * 1024, HostResourceLimitSource.Fallback);

		InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
			ResourceAdmissionInvariant.Validate(new HashSet<string>(StringComparer.Ordinal) { "discover", "scan" }, budget));

		Assert.Contains("scan", exception.Message, StringComparison.Ordinal);
		Assert.Contains("RunnerResources__MaxCpuCores", exception.Message, StringComparison.Ordinal);
		Assert.Contains("RunnerResources__FallbackCpuCores", exception.Message, StringComparison.Ordinal);
		// "discover" (0.25 cores) fits and must not be named as an offender.
		Assert.DoesNotContain("'discover'", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void AdvertisedJobTypeExceedsMemoryBudget_ThrowsNamingMemoryAxis()
	{
		// "scan" needs 1 GiB memory; this runner's effective budget is only 256 MiB.
		HostResourceLimits budget = new(CpuCores: 16.0, MemoryBytes: 256L * 1024 * 1024, HostResourceLimitSource.HostDerived);

		InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
			ResourceAdmissionInvariant.Validate(new HashSet<string>(StringComparer.Ordinal) { "scan" }, budget));

		Assert.Contains("scan", exception.Message, StringComparison.Ordinal);
		Assert.Contains("memory", exception.Message, StringComparison.Ordinal);
		Assert.Contains("RunnerResources__MaxMemoryBytes", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void MultipleOffendingJobTypes_AreAllNamedInOneDiagnostic()
	{
		// Both "scan" (2.0 cores) and "remediate" (1.5 cores) exceed a 1.0-core budget.
		HostResourceLimits budget = new(CpuCores: 1.0, MemoryBytes: 8L * 1024 * 1024 * 1024, HostResourceLimitSource.Fallback);

		InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
			ResourceAdmissionInvariant.Validate(new HashSet<string>(StringComparer.Ordinal) { "discover", "scan", "remediate" }, budget));

		Assert.Contains("scan", exception.Message, StringComparison.Ordinal);
		Assert.Contains("remediate", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void EmptyAdvertisedSet_DoesNotThrow()
	{
		// JobHandlerRegistry itself already forbids an empty allowlist -- this is
		// defense in depth, not a scenario expected to occur in practice.
		HostResourceLimits budget = new(CpuCores: 0.0, MemoryBytes: 0L, HostResourceLimitSource.Fallback);

		ResourceAdmissionInvariant.Validate(new HashSet<string>(StringComparer.Ordinal), budget);
	}

	[Fact]
	public void UnregisteredJobType_UsesDefaultProfileRatherThanThrowingOnLookup()
	{
		// JobResourceProfiles.Default is 1.0 cores / 512 MiB -- fits a generous budget.
		HostResourceLimits budget = new(CpuCores: 4.0, MemoryBytes: 4L * 1024 * 1024 * 1024, HostResourceLimitSource.HostDerived);

		ResourceAdmissionInvariant.Validate(new HashSet<string>(StringComparer.Ordinal) { "some-future-job-type" }, budget);
	}
}
