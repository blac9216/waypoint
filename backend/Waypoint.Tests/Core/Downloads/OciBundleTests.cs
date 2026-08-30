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

using Waypoint.Core.Downloads;
using Xunit;

namespace Waypoint.Tests.Core.Downloads;

/// <summary>
/// Issue #1403: model-level coverage for <see cref="OciBundle"/>'s status vocabulary
/// and the deterministic component-key to depot-registry repo-path map (#1157's Q3
/// findings) -- one assertion per documented mapping, per the issue's own acceptance
/// criterion.
/// </summary>
public sealed class OciBundleTests
{
	[Theory]
	[InlineData("SUPERVISOR_SERVICE_HARBOR", "/supervisor-service-harbor/ga")]
	[InlineData("VKS_STANDARD_PACKAGES", "/vks-standard-packages/ga")]
	[InlineData("VKR", "/vsphere-kubernetes-release/ga")]
	[InlineData("VCF_CONSUMPTION_CLI_PLUGINS", "/vcf-cli-plugins/ga")]
	public void ByComponentKey_DocumentedComponent_ResolvesItsPublishedRepoPath(string componentKey, string expectedRepoPath)
	{
		Assert.Equal(expectedRepoPath, OciBundleComponentRepoPaths.TryGetRepoPath(componentKey));
		Assert.Equal(expectedRepoPath, OciBundleComponentRepoPaths.ByComponentKey[componentKey]);
	}

	[Fact]
	public void ByComponentKey_HasExactlyTheFourDocumentedMappings()
	{
		Assert.Equal(4, OciBundleComponentRepoPaths.ByComponentKey.Count);
	}

	[Fact]
	public void TryGetRepoPath_UndocumentedComponent_ReturnsNull()
	{
		Assert.Null(OciBundleComponentRepoPaths.TryGetRepoPath("SOME_FUTURE_COMPONENT"));
	}

	[Fact]
	public void OciBundleStatuses_All_IsExactlyTheThreeStatusCheckValues()
	{
		Assert.Equal(["staged", "pushed", "push_failed"], OciBundleStatuses.All);
	}

	[Fact]
	public void OciBundle_RecordEquality_IsValueBased()
	{
		DateTimeOffset stagedAt = DateTimeOffset.UtcNow;
		OciBundle first = new(
			Guid.Empty, "VKR", "1.34.2+vmware.2-vkr.2", "/vsphere-kubernetes-release/ga",
			"/var/lib/waypoint/oci-bundles/vkr-1.34.2.tar", "deadbeef", OciBundleStatuses.Staged, stagedAt, null);
		OciBundle second = first with { };

		Assert.Equal(first, second);
	}

	[Fact]
	public void PushTargetConsumer_WriteModeEnabled_DefaultsFalseUntilChild1441DrivesIt()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		PushTargetConsumer consumer = new(Guid.Empty, "Primary depot registry", "depot.example.internal", false, now, now);

		Assert.False(consumer.WriteModeEnabled);
	}
}
