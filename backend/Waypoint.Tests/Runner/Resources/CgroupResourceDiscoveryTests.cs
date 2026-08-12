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

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Waypoint.Runner.Resources;
using Xunit;

namespace Waypoint.Tests.Runner.Resources;

/// <summary>
/// Issue #437's cgroup-discovery acceptance criteria: cgroup v2 primary, cgroup v1
/// handled when v2 is absent, and a tested conservative fallback -- always logged, never
/// silent -- when neither hierarchy yields a usable limit. Every fixture value below is
/// invented for this test, not read from any real host or lab (CLAUDE.md's public-repo
/// sanitization rule), and each test points <see cref="RunnerResourceOptions.CgroupRoot"/>
/// at its own temp directory so this suite has no dependency on the actual test host's
/// cgroup state -- it passes identically on a plain dev container with no cgroup limits
/// at all, on a real Docker container, or on cgroup v1.
/// </summary>
public sealed class CgroupResourceDiscoveryTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("waypoint-cgroup-test-").FullName;

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	private CgroupResourceDiscovery CreateDiscovery(RunnerResourceOptions? options = null)
	{
		RunnerResourceOptions effective = options ?? new RunnerResourceOptions();
		effective.CgroupRoot = _root;
		return new CgroupResourceDiscovery(Options.Create(effective), NullLogger<CgroupResourceDiscovery>.Instance);
	}

	[Fact]
	public void CgroupV2_QuotaAndMemoryPresent_ReadsBoth()
	{
		// Invented fixture: 150000/100000 us = 1.5 cores; 2 GiB memory.max.
		File.WriteAllText(Path.Combine(_root, "cpu.max"), "150000 100000\n");
		File.WriteAllText(Path.Combine(_root, "memory.max"), "2147483648\n");

		HostResourceLimits limits = CreateDiscovery().Discover();

		Assert.Equal(HostResourceLimitSource.CgroupV2, limits.Source);
		Assert.False(limits.IsFallback);
		Assert.Equal(1.5, limits.CpuCores, precision: 6);
		Assert.Equal(2147483648L, limits.MemoryBytes);
	}

	[Fact]
	public void CgroupV2_UnlimitedCpuQuota_FallsBackEntirely()
	{
		File.WriteAllText(Path.Combine(_root, "cpu.max"), "max 100000\n");
		File.WriteAllText(Path.Combine(_root, "memory.max"), "2147483648\n");

		RunnerResourceOptions options = new() { FallbackCpuCores = 1.0, FallbackMemoryBytes = 1024L * 1024 * 1024 };
		HostResourceLimits limits = CreateDiscovery(options).Discover();

		// A partial v2 read (memory present, CPU unlimited) is treated as a full
		// fallback -- see CgroupResourceDiscovery's remarks on not mixing sources.
		Assert.Equal(HostResourceLimitSource.Fallback, limits.Source);
		Assert.True(limits.IsFallback);
		Assert.Equal(1.0, limits.CpuCores, precision: 6);
		Assert.Equal(1024L * 1024 * 1024, limits.MemoryBytes);
	}

	[Fact]
	public void CgroupV2_UnlimitedMemory_FallsBackEntirely()
	{
		File.WriteAllText(Path.Combine(_root, "cpu.max"), "150000 100000\n");
		File.WriteAllText(Path.Combine(_root, "memory.max"), "max\n");

		HostResourceLimits limits = CreateDiscovery().Discover();

		Assert.Equal(HostResourceLimitSource.Fallback, limits.Source);
	}

	[Fact]
	public void CgroupV1_QuotaAndLimitPresent_ReadsBoth()
	{
		string cpuDir = Path.Combine(_root, "cpu");
		string memoryDir = Path.Combine(_root, "memory");
		Directory.CreateDirectory(cpuDir);
		Directory.CreateDirectory(memoryDir);

		// Invented fixture: 200000/100000 us = 2.0 cores; 1 GiB limit_in_bytes.
		File.WriteAllText(Path.Combine(cpuDir, "cpu.cfs_quota_us"), "200000\n");
		File.WriteAllText(Path.Combine(cpuDir, "cpu.cfs_period_us"), "100000\n");
		File.WriteAllText(Path.Combine(memoryDir, "memory.limit_in_bytes"), "1073741824\n");

		HostResourceLimits limits = CreateDiscovery().Discover();

		Assert.Equal(HostResourceLimitSource.CgroupV1, limits.Source);
		Assert.Equal(2.0, limits.CpuCores, precision: 6);
		Assert.Equal(1073741824L, limits.MemoryBytes);
	}

	[Fact]
	public void CgroupV1_NoQuotaConfigured_FallsBackEntirely()
	{
		string cpuDir = Path.Combine(_root, "cpu");
		string memoryDir = Path.Combine(_root, "memory");
		Directory.CreateDirectory(cpuDir);
		Directory.CreateDirectory(memoryDir);

		// cgroup v1's "no quota configured" sentinel is -1, not a text marker.
		File.WriteAllText(Path.Combine(cpuDir, "cpu.cfs_quota_us"), "-1\n");
		File.WriteAllText(Path.Combine(cpuDir, "cpu.cfs_period_us"), "100000\n");
		File.WriteAllText(Path.Combine(memoryDir, "memory.limit_in_bytes"), "1073741824\n");

		HostResourceLimits limits = CreateDiscovery().Discover();

		Assert.Equal(HostResourceLimitSource.Fallback, limits.Source);
	}

	[Fact]
	public void CgroupV1_UnlimitedMemorySentinel_FallsBackEntirely()
	{
		string cpuDir = Path.Combine(_root, "cpu");
		string memoryDir = Path.Combine(_root, "memory");
		Directory.CreateDirectory(cpuDir);
		Directory.CreateDirectory(memoryDir);

		File.WriteAllText(Path.Combine(cpuDir, "cpu.cfs_quota_us"), "200000\n");
		File.WriteAllText(Path.Combine(cpuDir, "cpu.cfs_period_us"), "100000\n");
		// The near-LONG_MAX sentinel cgroup v1 reports for "no memory limit configured".
		File.WriteAllText(Path.Combine(memoryDir, "memory.limit_in_bytes"), "9223372036854771712\n");

		HostResourceLimits limits = CreateDiscovery().Discover();

		Assert.Equal(HostResourceLimitSource.Fallback, limits.Source);
	}

	[Fact]
	public void NoCgroupFilesAtAll_FallsBackToConfiguredConservativeDefaults()
	{
		// _root exists but is empty -- neither v2 nor v1 files are present. This is
		// also exactly what a plain non-containerized dev box looks like, so this test
		// doubles as the "non-containerized tests still pass" proof (issue #437 AC).
		RunnerResourceOptions options = new() { FallbackCpuCores = 1.0, FallbackMemoryBytes = 1024L * 1024 * 1024 };

		HostResourceLimits limits = CreateDiscovery(options).Discover();

		Assert.Equal(HostResourceLimitSource.Fallback, limits.Source);
		Assert.True(limits.IsFallback);
		Assert.Equal(1.0, limits.CpuCores, precision: 6);
		Assert.Equal(1024L * 1024 * 1024, limits.MemoryBytes);
	}

	[Fact]
	public void MalformedCgroupV2CpuFile_FallsBackRatherThanThrowing()
	{
		File.WriteAllText(Path.Combine(_root, "cpu.max"), "not-a-number\n");
		File.WriteAllText(Path.Combine(_root, "memory.max"), "2147483648\n");

		HostResourceLimits limits = CreateDiscovery().Discover();

		Assert.Equal(HostResourceLimitSource.Fallback, limits.Source);
	}

	[Fact]
	public void UnreadableCgroupRoot_FallsBackRatherThanThrowing()
	{
		// A CgroupRoot that does not exist at all is the "unreadable" case in
		// practice (a container without the cgroup mount, or a permissions issue that
		// makes File.Exists effectively false from this process's perspective).
		RunnerResourceOptions options = new()
		{
			CgroupRoot = Path.Combine(_root, "does-not-exist"),
			FallbackCpuCores = 1.0,
			FallbackMemoryBytes = 1024L * 1024 * 1024,
		};

		CgroupResourceDiscovery discovery = new(Options.Create(options), NullLogger<CgroupResourceDiscovery>.Instance);
		HostResourceLimits limits = discovery.Discover();

		Assert.Equal(HostResourceLimitSource.Fallback, limits.Source);
	}

	[Fact]
	public void CgroupV2PreferredOverV1WhenBothPresent()
	{
		File.WriteAllText(Path.Combine(_root, "cpu.max"), "100000 100000\n");
		File.WriteAllText(Path.Combine(_root, "memory.max"), "1073741824\n");

		string cpuDir = Path.Combine(_root, "cpu");
		string memoryDir = Path.Combine(_root, "memory");
		Directory.CreateDirectory(cpuDir);
		Directory.CreateDirectory(memoryDir);
		File.WriteAllText(Path.Combine(cpuDir, "cpu.cfs_quota_us"), "400000\n");
		File.WriteAllText(Path.Combine(cpuDir, "cpu.cfs_period_us"), "100000\n");
		File.WriteAllText(Path.Combine(memoryDir, "memory.limit_in_bytes"), "4294967296\n");

		HostResourceLimits limits = CreateDiscovery().Discover();

		Assert.Equal(HostResourceLimitSource.CgroupV2, limits.Source);
		Assert.Equal(1.0, limits.CpuCores, precision: 6);
		Assert.Equal(1073741824L, limits.MemoryBytes);
	}
}
