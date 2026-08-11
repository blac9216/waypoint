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
using Waypoint.Runner.Jobs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Runner;

/// <summary>
/// Issue #436's mandatory capability-registration acceptance criterion: a runner host
/// fails closed at startup -- construction of <see cref="JobHandlerRegistry"/>, not a
/// later runtime path -- with no allowlist or a duplicate handler, rather than starting
/// up and quietly claiming nothing or resolving ambiguously.
/// </summary>
public sealed class JobHandlerRegistryTests
{
	[Fact]
	public void EmptyAllowlist_ThrowsAtConstruction()
	{
		InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
			() => new JobHandlerRegistry([], new HashSet<string>(StringComparer.Ordinal)));
		Assert.Contains("allowlist", exception.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void NullAllowlist_Throws()
	{
		Assert.Throws<ArgumentNullException>(
			() => new JobHandlerRegistry([], null!));
	}

	[Fact]
	public void DuplicateHandlerForSameJobType_ThrowsAtConstruction()
	{
		FakeJobHandler first = new("download", (_, _) => Task.FromResult(JobExecutionOutcome.Succeeded()));
		FakeJobHandler second = new("download", (_, _) => Task.FromResult(JobExecutionOutcome.Succeeded()));

		InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
			() => new JobHandlerRegistry([first, second], JobCapabilities.All));
		Assert.Contains("download", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ConvenienceOverload_DefaultsToTheFullCapabilitySet()
	{
		JobHandlerRegistry registry = new([]);
		Assert.Equal(JobCapabilities.All, registry.AllowedJobTypes);
	}

	[Fact]
	public void ValidRegistration_ResolvesRegisteredHandlersAndExposesTheAllowlist()
	{
		FakeJobHandler download = new("download", (_, _) => Task.FromResult(JobExecutionOutcome.Succeeded()));
		JobHandlerRegistry registry = new([download], JobCapabilities.Download);

		Assert.Equal(JobCapabilities.Download, registry.AllowedJobTypes);
		Assert.True(registry.TryResolve("download", out IJobHandler? handler));
		Assert.Same(download, handler);
		Assert.False(registry.TryResolve("scan", out IJobHandler? missing));
		Assert.Null(missing);
	}
}
