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

using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Waypoint.Runner.Resources;

/// <summary>
/// Discovers the effective CPU/memory budget a runner container was actually given, by
/// reading cgroup limit files directly (ADR-0014 §5, issue #437). Prefers cgroup v2's
/// unified hierarchy (<c>cpu.max</c>, <c>memory.max</c> at <see cref="RunnerResourceOptions.CgroupRoot"/>),
/// falls back to cgroup v1's split hierarchy (<c>cpu/cpu.cfs_quota_us</c> +
/// <c>cpu/cpu.cfs_period_us</c>, <c>memory/memory.limit_in_bytes</c>) when v2 files are
/// absent, then to host-derived capacity (<see cref="IHostCapabilitySource"/>, ADR-0018
/// issue #555) when neither cgroup hierarchy yields a usable, finite limit (missing
/// files, unreadable files, or an explicit "max"/"-1" unlimited marker), and only when
/// even that is unusable to <see cref="RunnerResourceOptions.FallbackCpuCores"/>/
/// <see cref="RunnerResourceOptions.FallbackMemoryBytes"/>'s tested conservative
/// constants -- every one of these fallback steps is always logged, never silent.
///
/// <para>
/// An explicit, finite cgroup limit is always authoritative over a host-derived
/// reading: this order only ever widens what an *unlimited* container would otherwise
/// be conservatively assumed to have, it never overrides a real container cap. Any
/// operator cap (<see cref="RunnerResourceOptions.MaxCpuCores"/>/<c>MaxMemoryBytes</c>)
/// intersects with whichever of these produced the discovered numbers, unchanged from
/// ADR-0014 §5.
/// </para>
///
/// <para>
/// No .NET GC/runtime API is used to read a cgroup limit itself: <c>GC.GetGCMemoryInfo()</c>
/// reports what the CLR decided its heap limit is (already downstream of the container
/// limit and rounded/adjusted by GC heuristics), not the container's raw allocation --
/// cgroup reads above use the same source of truth Docker/Kubernetes/systemd themselves
/// write to. <see cref="SystemHostCapabilitySource"/> does use it, deliberately, for the
/// host-derived path -- see that type's doc comment for why the same API is the right
/// answer once cgroup discovery has already established there is no container limit.
/// </para>
/// </summary>
public sealed partial class CgroupResourceDiscovery
{
	private readonly IOptions<RunnerResourceOptions> _options;
	private readonly IHostCapabilitySource _hostCapabilities;
	private readonly ILogger<CgroupResourceDiscovery> _logger;

	public CgroupResourceDiscovery(IOptions<RunnerResourceOptions> options, ILogger<CgroupResourceDiscovery> logger)
		: this(options, new SystemHostCapabilitySource(), logger)
	{
	}

	/// <summary>Test/DI seam: lets callers supply a fake <see cref="IHostCapabilitySource"/> instead of reading the real host.</summary>
	public CgroupResourceDiscovery(IOptions<RunnerResourceOptions> options, IHostCapabilitySource hostCapabilities, ILogger<CgroupResourceDiscovery> logger)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(hostCapabilities);
		ArgumentNullException.ThrowIfNull(logger);

		_options = options;
		_hostCapabilities = hostCapabilities;
		_logger = logger;
	}

	/// <summary>
	/// Reads the effective CPU/memory budget, preferring cgroup v2, then v1, then
	/// host-derived capacity, then the tested conservative fallback. Never throws: every
	/// failure mode (missing file, unreadable file, malformed content,
	/// reported-unlimited, an unusable host reading) degrades to the next source in the
	/// chain rather than crashing runner startup over a diagnostics read.
	/// </summary>
	public HostResourceLimits Discover()
	{
		RunnerResourceOptions options = _options.Value;

		double? v2Cpu = ReadCgroupV2Cpu(options.CgroupRoot);
		long? v2Memory = ReadCgroupV2Memory(options.CgroupRoot);
		if (v2Cpu is { } cpu2 && v2Memory is { } mem2)
		{
			return new HostResourceLimits(cpu2, mem2, HostResourceLimitSource.CgroupV2);
		}

		double? v1Cpu = ReadCgroupV1Cpu(options.CgroupRoot);
		long? v1Memory = ReadCgroupV1Memory(options.CgroupRoot);
		if (v1Cpu is { } cpu1 && v1Memory is { } mem1)
		{
			return new HostResourceLimits(cpu1, mem1, HostResourceLimitSource.CgroupV1);
		}

		// Partial cgroup reads (e.g. v2 CPU present but memory missing/unlimited) fall
		// through to host derivation rather than mixing cgroup sources -- a mixed
		// cgroup-v2/v1 reading is more confusing in health diagnostics than a clearly
		// labeled single source, and an unlimited cgroup axis means the container truly
		// has no cap on that axis, which is exactly the condition host derivation exists
		// to answer.
		HostResourceLimits? hostDerived = TryDeriveFromHost();
		if (hostDerived is { } derived)
		{
			return derived;
		}

		LogFallingBackToConservativeDefaults(options.FallbackCpuCores, options.FallbackMemoryBytes);
		return new HostResourceLimits(options.FallbackCpuCores, options.FallbackMemoryBytes, HostResourceLimitSource.Fallback);
	}

	/// <summary>
	/// ADR-0018 (issue #555): derives capacity from the host itself when no explicit,
	/// finite cgroup limit was found. Returns <c>null</c> (degrading to the constant
	/// fallback) only if the host reading itself is unusable -- zero or negative CPU
	/// cores, or zero or negative memory -- which in practice means
	/// <see cref="IHostCapabilitySource"/> itself is misbehaving, not a real host
	/// configuration.
	/// </summary>
	private HostResourceLimits? TryDeriveFromHost()
	{
		double cpuCores;
		long memoryBytes;
		try
		{
			cpuCores = _hostCapabilities.AvailableCpuCores();
			memoryBytes = _hostCapabilities.TotalMemoryBytes();
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			LogHostCapabilityReadFailed(exception);
			return null;
		}

		if (cpuCores <= 0 || memoryBytes <= 0)
		{
			LogHostCapabilityUnusable(cpuCores, memoryBytes);
			return null;
		}

		LogHostDerivedCapacity(cpuCores, memoryBytes);
		return new HostResourceLimits(cpuCores, memoryBytes, HostResourceLimitSource.HostDerived);
	}

	private double? ReadCgroupV2Cpu(string root)
	{
		string path = Path.Combine(root, "cpu.max");
		string? content = TryReadFile(path);
		if (content is null)
		{
			return null;
		}

		// Format: "<quota> <period>" in microseconds, or "max <period>" for unlimited.
		string[] parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length != 2)
		{
			LogMalformedCgroupFile(path, content);
			return null;
		}

		if (string.Equals(parts[0], "max", StringComparison.Ordinal))
		{
			LogUnlimitedCgroupValue(path);
			return null;
		}

		if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long quotaMicros) ||
			!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long periodMicros) ||
			periodMicros <= 0)
		{
			LogMalformedCgroupFile(path, content);
			return null;
		}

		return (double)quotaMicros / periodMicros;
	}

	private long? ReadCgroupV2Memory(string root)
	{
		string path = Path.Combine(root, "memory.max");
		string? content = TryReadFile(path);
		if (content is null)
		{
			return null;
		}

		if (string.Equals(content, "max", StringComparison.Ordinal))
		{
			LogUnlimitedCgroupValue(path);
			return null;
		}

		if (!long.TryParse(content, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes) || bytes <= 0)
		{
			LogMalformedCgroupFile(path, content);
			return null;
		}

		return bytes;
	}

	private double? ReadCgroupV1Cpu(string root)
	{
		string quotaPath = Path.Combine(root, "cpu", "cpu.cfs_quota_us");
		string periodPath = Path.Combine(root, "cpu", "cpu.cfs_period_us");
		string? quotaContent = TryReadFile(quotaPath);
		string? periodContent = TryReadFile(periodPath);
		if (quotaContent is null || periodContent is null)
		{
			return null;
		}

		// cgroup v1 reports -1 for "no quota configured" (unlimited), not "max".
		if (!long.TryParse(quotaContent, NumberStyles.Integer, CultureInfo.InvariantCulture, out long quotaMicros) ||
			!long.TryParse(periodContent, NumberStyles.Integer, CultureInfo.InvariantCulture, out long periodMicros) ||
			periodMicros <= 0)
		{
			LogMalformedCgroupFile(quotaPath, quotaContent);
			return null;
		}

		if (quotaMicros < 0)
		{
			LogUnlimitedCgroupValue(quotaPath);
			return null;
		}

		return (double)quotaMicros / periodMicros;
	}

	private long? ReadCgroupV1Memory(string root)
	{
		string path = Path.Combine(root, "memory", "memory.limit_in_bytes");
		string? content = TryReadFile(path);
		if (content is null)
		{
			return null;
		}

		if (!long.TryParse(content, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes) || bytes <= 0)
		{
			LogMalformedCgroupFile(path, content);
			return null;
		}

		// cgroup v1 reports an enormous sentinel (typically close to LONG_MAX rounded
		// down to a page boundary, e.g. 9223372036854771712) rather than a dedicated
		// "unlimited" token when no memory limit is configured. Treat anything at or
		// above 2^62 bytes (4 exbibytes -- far beyond any real host) as unlimited.
		const long unlimitedThreshold = 1L << 62;
		if (bytes >= unlimitedThreshold)
		{
			LogUnlimitedCgroupValue(path);
			return null;
		}

		return bytes;
	}

	private string? TryReadFile(string path)
	{
		try
		{
			if (!File.Exists(path))
			{
				return null;
			}

			return File.ReadAllText(path).Trim();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			LogCgroupFileUnreadable(path, exception);
			return null;
		}
	}

	[LoggerMessage(Level = LogLevel.Debug, Message = "Cgroup file '{Path}' reports no effective limit (unlimited)")]
	private partial void LogUnlimitedCgroupValue(string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Cgroup file '{Path}' has unexpected content '{Content}'; ignoring")]
	private partial void LogMalformedCgroupFile(string path, string content);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Cgroup file '{Path}' could not be read")]
	private partial void LogCgroupFileUnreadable(string path, Exception exception);

	[LoggerMessage(Level = LogLevel.Warning, Message = "No usable cgroup or host-derived CPU/memory limits found; falling back to conservative defaults ({FallbackCpuCores} cores, {FallbackMemoryBytes} bytes). This runner's discovered capacity may not reflect its actual host allocation -- see RunnerResourceOptions.")]
	private partial void LogFallingBackToConservativeDefaults(double fallbackCpuCores, long fallbackMemoryBytes);

	[LoggerMessage(Level = LogLevel.Information, Message = "No usable cgroup CPU/memory limits found; deriving capacity from the host instead: {CpuCores} cores, {MemoryBytes} bytes")]
	private partial void LogHostDerivedCapacity(double cpuCores, long memoryBytes);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Host capability read failed; falling through to the conservative fallback")]
	private partial void LogHostCapabilityReadFailed(Exception exception);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Host-derived capacity reading was unusable ({CpuCores} cores, {MemoryBytes} bytes); falling through to the conservative fallback")]
	private partial void LogHostCapabilityUnusable(double cpuCores, long memoryBytes);
}
