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

using Waypoint.Core.Catalog;
using Xunit;

namespace Waypoint.Tests.Core.Catalog;

/// <summary>
/// Pure unit coverage for issue #36's mode-aware presence projection -- no Postgres,
/// exactly the artifacts a real <c>depot_artifacts</c> row set can produce.
/// </summary>
public sealed class LibraryPresenceEvaluatorTests
{
	private static DepotArtifact Artifact(string externalId, string status, string? product, string? version, string metadataJson = "{}") =>
		new(Guid.NewGuid(), externalId, Sha256: "sha", status, product, version, metadataJson, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

	[Fact]
	public void Evaluate_PresentArtifact_IsPresent()
	{
		DepotArtifact artifact = Artifact("a1", "present", "VCF", "9.0");

		IReadOnlyList<LibraryItem> items = LibraryPresenceEvaluator.Evaluate([artifact], connected: true);

		Assert.Equal(LibraryPresenceStates.Present, items[0].Presence);
	}

	[Fact]
	public void Evaluate_OlderPresentVersionOfSameProduct_IsSuperseded()
	{
		DepotArtifact older = Artifact("a1", "present", "VCF", "9.0");
		DepotArtifact newer = Artifact("a2", "present", "VCF", "9.1");

		IReadOnlyList<LibraryItem> items = LibraryPresenceEvaluator.Evaluate([older, newer], connected: true);

		Assert.Equal(LibraryPresenceStates.Superseded, items.Single(i => i.ExternalId == "a1").Presence);
		Assert.Equal(LibraryPresenceStates.Present, items.Single(i => i.ExternalId == "a2").Presence);
	}

	[Fact]
	public void Evaluate_NotPresent_Connected_IsInDepot()
	{
		DepotArtifact artifact = Artifact("a1", "indexed", "NSX", "4.2");

		IReadOnlyList<LibraryItem> items = LibraryPresenceEvaluator.Evaluate([artifact], connected: true);

		Assert.Equal(LibraryPresenceStates.InDepot, items[0].Presence);
	}

	[Fact]
	public void Evaluate_NotPresent_Disconnected_IsMissing()
	{
		DepotArtifact artifact = Artifact("a1", "indexed", "NSX", "4.2");

		IReadOnlyList<LibraryItem> items = LibraryPresenceEvaluator.Evaluate([artifact], connected: false);

		Assert.Equal(LibraryPresenceStates.Missing, items[0].Presence);
	}

	[Fact]
	public void Evaluate_ReadsSizeBytesFromMetadata_WhenPresent()
	{
		DepotArtifact artifact = Artifact("a1", "present", "VCF", "9.0", metadataJson: """{"product":"VCF","version":"9.0","size":2048}""");

		IReadOnlyList<LibraryItem> items = LibraryPresenceEvaluator.Evaluate([artifact], connected: true);

		Assert.Equal(2048, items[0].SizeBytes);
	}

	[Fact]
	public void Evaluate_MissingSizeInMetadata_YieldsNullNotException()
	{
		DepotArtifact artifact = Artifact("a1", "present", "VCF", "9.0", metadataJson: """{"product":"VCF","version":"9.0"}""");

		IReadOnlyList<LibraryItem> items = LibraryPresenceEvaluator.Evaluate([artifact], connected: true);

		Assert.Null(items[0].SizeBytes);
	}

	[Fact]
	public void GroupByFamily_RollsUpPresentAndMissingPerProduct()
	{
		DepotArtifact[] artifacts =
		[
			Artifact("a1", "present", "VCF", "9.0"),
			Artifact("a2", "indexed", "VCF", "9.1"),
			Artifact("a3", "present", "NSX", "4.2"),
		];
		IReadOnlyList<LibraryItem> items = LibraryPresenceEvaluator.Evaluate(artifacts, connected: true);

		IReadOnlyList<LibraryFamily> families = LibraryPresenceEvaluator.GroupByFamily(items);

		LibraryFamily vcf = families.Single(f => f.Name == "VCF");
		Assert.Equal(1, vcf.PresentCount);
		Assert.Equal(1, vcf.MissingCount);
		LibraryFamily nsx = families.Single(f => f.Name == "NSX");
		Assert.Equal(1, nsx.PresentCount);
		Assert.Equal(0, nsx.MissingCount);
	}

	[Fact]
	public void GroupByFamily_ArtifactWithNoProduct_IsExcluded()
	{
		DepotArtifact artifact = Artifact("a1", "present", product: null, version: null);
		IReadOnlyList<LibraryItem> items = LibraryPresenceEvaluator.Evaluate([artifact], connected: true);

		IReadOnlyList<LibraryFamily> families = LibraryPresenceEvaluator.GroupByFamily(items);

		Assert.Empty(families);
	}
}
