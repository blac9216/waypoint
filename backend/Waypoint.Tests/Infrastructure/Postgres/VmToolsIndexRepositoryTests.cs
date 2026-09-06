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
using Waypoint.Core.Downloads;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1392, migration 0109, against real Postgres: AC3 -- a re-crawl of an
/// unchanged fixture tree must never duplicate a <c>vmtools_artifact_index</c> row,
/// and the equivalent re-parse guarantee for <c>vmtools_esx_version_mapping</c>.
/// Fixtures below are entirely invented (CLAUDE.md sanitization rules): the host
/// used is <c>https://tools.example.internal/tools/</c>, never the real mirror.
/// </summary>
[Collection("Postgres")]
public sealed class VmToolsIndexRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private VmToolsIndexRepository _repository = null!;

	public VmToolsIndexRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		_repository = new VmToolsIndexRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task UpsertArtifactsAsync_ReCrawlOfUnchangedTree_YieldsNoDuplicateRows()
	{
		string relativePath = $"releases/13.1.0/windows/VMware-tools-windows-13.1.0-25218885-{Guid.NewGuid():N}.iso";
		DateTimeOffset firstCrawl = DateTimeOffset.UtcNow.AddMinutes(-10);
		VmToolsArtifact firstObservation = VmToolsArtifact.FromCrawl(
			relativePath, "13.1.0", 13, 1, 0, "25218885", VmToolsPlatforms.Windows, VmToolsFileTypes.Iso,
			142_280_704, "\"etag-fixture-abc123\"", isLatestAlias: false, observedAt: firstCrawl);

		await _repository.UpsertArtifactsAsync([firstObservation], CancellationToken.None);

		DateTimeOffset secondCrawl = DateTimeOffset.UtcNow;
		VmToolsArtifact secondObservation = firstObservation with { Id = Guid.NewGuid(), FirstSeenAt = secondCrawl, LastSeenAt = secondCrawl };

		await _repository.UpsertArtifactsAsync([secondObservation], CancellationToken.None);

		IReadOnlyList<VmToolsArtifact> artifacts = await _repository.GetArtifactsAsync(CancellationToken.None);
		VmToolsArtifact[] matching = [.. artifacts.Where(a => a.RelativePath == relativePath)];
		Assert.Single(matching);
		Assert.Equal(firstObservation.Id, matching[0].Id);
		Assert.True(matching[0].LastSeenAt >= secondCrawl.AddSeconds(-1));
		// Postgres timestamptz has microsecond precision; .NET DateTimeOffset has
		// tick (100ns) precision, so compare with a tolerance rather than exact equality.
		Assert.True((matching[0].FirstSeenAt - firstCrawl).Duration() < TimeSpan.FromMilliseconds(1));
	}

	[Fact]
	public async Task UpsertArtifactsAsync_DifferentEtagAtSamePath_IsANewRow_NotAClobber()
	{
		// A vendor-side content change at the same path (different ETag) must be its
		// own row -- upsert identity is (relative_path, etag) together, per AC3 and
		// migration 0109's unique constraint, not relative_path alone.
		string relativePath = $"releases/latest/windows/VMware-tools-windows-{Guid.NewGuid():N}.iso";
		VmToolsArtifact original = VmToolsArtifact.FromCrawl(
			relativePath, "13.1.0", 13, 1, 0, "25218885", VmToolsPlatforms.Windows, VmToolsFileTypes.Iso,
			142_280_704, "\"etag-fixture-v1\"", isLatestAlias: true, observedAt: DateTimeOffset.UtcNow);
		VmToolsArtifact updated = original with { Id = Guid.NewGuid(), ETag = "\"etag-fixture-v2\"" };

		await _repository.UpsertArtifactsAsync([original, updated], CancellationToken.None);

		IReadOnlyList<VmToolsArtifact> artifacts = await _repository.GetArtifactsAsync(CancellationToken.None);
		Assert.Equal(2, artifacts.Count(a => a.RelativePath == relativePath));
		Assert.Contains(artifacts, a => a.RelativePath == relativePath && a.IsLatestAlias);
	}

	[Fact]
	public async Task UpsertVersionMappingsAsync_RePersistOfSameFile_YieldsNoDuplicateRows()
	{
		string esxiVersionDir = $"esx/9.{Guid.NewGuid():N}";
		VmToolsEsxVersionMapping mapping = new(
			Id: Guid.NewGuid(), SequenceInFile: 0, EsxiVersionDir: esxiVersionDir, EsxiBuild: "25370933",
			ToolsVersionCode: "13344", ToolsVersionRaw: "13.1.0", ToolsVersionMajor: 13, ToolsVersionMinor: 1,
			ToolsVersionPatch: 0, ToolsBuild: "25218885", RawRow: $"13344 {esxiVersionDir} 25370933 13.1.0 25218885");

		await _repository.UpsertVersionMappingsAsync([mapping], CancellationToken.None);
		await _repository.UpsertVersionMappingsAsync([mapping with { Id = Guid.NewGuid() }], CancellationToken.None);

		IReadOnlyList<VmToolsEsxVersionMapping> mappings = await _repository.GetVersionMappingsAsync(CancellationToken.None);
		Assert.Single(mappings.Where(m => m.EsxiVersionDir == esxiVersionDir));
	}
}
