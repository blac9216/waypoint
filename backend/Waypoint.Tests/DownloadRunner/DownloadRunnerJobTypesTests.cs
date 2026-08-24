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
using Waypoint.DownloadRunner;
using Waypoint.Runner.Jobs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.DownloadRunner;

/// <summary>
/// Issue #441's registration acceptance criterion: "Catalog-index and download jobs
/// execute only in the download runner" -- proven here as "this host's allowlist and
/// registered handlers are exactly {catalog-index, download, tool-install,
/// depot-enrollment, catalog-pull}, nothing from JobCapabilities.Compliance and none
/// of Download's remaining unimplemented 'later' types." <c>tool-install</c> joined
/// the set in issue #619, which fixed the handler-registered-but-never-claimable gap
/// it had sat in since #39/#602; <c>depot-enrollment</c> joined in issue #691 for the
/// same reason (the assisted enrollment job invokes the same managed tool
/// tool-install/depot-fetch already require); <c>catalog-pull</c> joined in issue
/// #687 (the connected vendor catalog pull, distinct from the local credential-free
/// <c>catalog-index</c> re-index).
/// </summary>
public sealed class DownloadRunnerJobTypesTests
{
	[Fact]
	public void Allowed_IsExactlyCatalogIndexDownloadToolInstallDepotEnrollmentAndCatalogPull()
	{
		Assert.Equal(
			new HashSet<string>(StringComparer.Ordinal) { "catalog-index", "download", "tool-install", "depot-enrollment", "catalog-pull" },
			Waypoint.DownloadRunner.DownloadRunnerJobTypes.Allowed);
	}

	[Fact]
	public void Allowed_IsASubsetOfJobCapabilitiesDownload()
	{
		Assert.All(Waypoint.DownloadRunner.DownloadRunnerJobTypes.Allowed, jobType => Assert.Contains(jobType, JobCapabilities.Download));
	}

	[Fact]
	public void Allowed_SharesNoJobTypeWithCompliance()
	{
		Assert.Empty(Waypoint.DownloadRunner.DownloadRunnerJobTypes.Allowed.Intersect(JobCapabilities.Compliance));
	}

	/// <summary>
	/// Mirrors Program.cs's actual composition: a full IJobHandler set (as
	/// AddWaypointInfrastructure registers, including compliance-only handlers) is
	/// filtered down to this host's allowlist before constructing JobHandlerRegistry.
	/// The registry must resolve exactly the download-domain types and nothing else.
	/// </summary>
	[Fact]
	public void RegistryBuiltFromTheFullHandlerSet_ResolvesOnlyDownloadDomainTypes()
	{
		FakeJobHandler catalogIndex = new("catalog-index", (_, _) => Task.FromResult(JobExecutionOutcome.Succeeded()));
		FakeJobHandler download = new("download", (_, _) => Task.FromResult(JobExecutionOutcome.Succeeded()));
		FakeJobHandler discover = new("discover", (_, _) => Task.FromResult(JobExecutionOutcome.Succeeded()));
		FakeJobHandler scan = new("scan", (_, _) => Task.FromResult(JobExecutionOutcome.Succeeded()));
		FakeJobHandler credentialTest = new("credential-test", (_, _) => Task.FromResult(JobExecutionOutcome.Succeeded()));

		IJobHandler[] allHandlers = [catalogIndex, download, discover, scan, credentialTest];
		JobHandlerRegistry registry = new(
			allHandlers.Where(handler => Waypoint.DownloadRunner.DownloadRunnerJobTypes.Allowed.Contains(handler.JobType)),
			Waypoint.DownloadRunner.DownloadRunnerJobTypes.Allowed);

		Assert.Equal(Waypoint.DownloadRunner.DownloadRunnerJobTypes.Allowed, registry.AllowedJobTypes);
		Assert.True(registry.TryResolve("catalog-index", out IJobHandler? resolvedCatalogIndex));
		Assert.Same(catalogIndex, resolvedCatalogIndex);
		Assert.True(registry.TryResolve("download", out IJobHandler? resolvedDownload));
		Assert.Same(download, resolvedDownload);

		Assert.False(registry.TryResolve("discover", out _));
		Assert.False(registry.TryResolve("scan", out _));
		Assert.False(registry.TryResolve("credential-test", out _));
	}
}
