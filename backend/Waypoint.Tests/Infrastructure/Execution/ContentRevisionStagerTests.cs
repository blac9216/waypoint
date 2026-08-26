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

using Waypoint.Core.ComplianceContent;
using Waypoint.Infrastructure.Execution.ComplianceContent;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Execution;

/// <summary>
/// Issue #731: proves <see cref="ContentRevisionStager"/>'s filesystem snapshot
/// behavior against a real temp directory (no Postgres -- the repository call is
/// faked; <c>BaselineRepositoryTests</c> covers the real-Postgres storage contract
/// separately). The central claim under test is immutability of the staged copy: it
/// must be a genuine, independent copy of the working tree at stage time, not a
/// reference that later mutates alongside the next pull's checkout.
/// </summary>
public sealed class ContentRevisionStagerTests : IDisposable
{
	private readonly string _contentPath = Directory.CreateTempSubdirectory("wp-content-revision-stager-test").FullName;

	public void Dispose() => Directory.Delete(_contentPath, recursive: true);

	private sealed class FakeBaselineRepository : IBaselineRepository
	{
		public List<(string SourceCommit, string ContentDigest, string StagedRelativePath)> Recorded { get; } = [];

		public Task<ContentRevision> RecordStagedRevisionAsync(string sourceCommit, string contentDigest, string stagedRelativePath, CancellationToken cancellationToken)
		{
			Recorded.Add((sourceCommit, contentDigest, stagedRelativePath));
			return Task.FromResult(new ContentRevision(Guid.NewGuid(), sourceCommit, contentDigest, stagedRelativePath, ContentRevisionStatuses.Staged, false, DateTimeOffset.UtcNow));
		}

		public Task<ContentRevision?> GetRevisionAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<ContentRevision>> ListRevisionsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<Baseline> CreateStagedBaselineAsync(Guid contentRevisionId, Guid catalogExecutionProfileId, Guid? benchmarkRevisionId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<Baseline?> GetBaselineAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<Baseline>> ListBaselinesForExecutionProfileAsync(Guid catalogExecutionProfileId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<Baseline>> ListAllBaselinesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<Baseline?> GetActiveBaselineAsync(Guid catalogExecutionProfileId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<BaselineActivationOutcome> ActivateAsync(Guid baselineId, string activatedBy, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<BaselineActivationOutcome> RollbackAsync(Guid baselineId, string activatedBy, CancellationToken cancellationToken) => throw new NotSupportedException();
	}

	[Fact]
	public async Task StageAsync_CopiesWorkingTreeIntoDigestAddressedDirectory()
	{
		File.WriteAllText(Path.Combine(_contentPath, "profile.yml"), "name: invented-profile\n");
		Directory.CreateDirectory(Path.Combine(_contentPath, "controls"));
		File.WriteAllText(Path.Combine(_contentPath, "controls", "V-0001.rb"), "control 'V-0001' do\nend\n");

		FakeBaselineRepository repository = new();
		ContentRevisionStager stager = new(repository);

		ContentRevision revision = await stager.StageAsync(_contentPath, "commit-abc123", "digest-deadbeef", CancellationToken.None);

		string stagedDirectory = Path.Combine(_contentPath, revision.StagedRelativePath);
		Assert.True(Directory.Exists(stagedDirectory));
		Assert.Equal("name: invented-profile\n", File.ReadAllText(Path.Combine(stagedDirectory, "profile.yml")));
		Assert.Equal("control 'V-0001' do\nend\n", File.ReadAllText(Path.Combine(stagedDirectory, "controls", "V-0001.rb")));

		(string sourceCommit, string contentDigest, string stagedRelativePath) = Assert.Single(repository.Recorded);
		Assert.Equal("commit-abc123", sourceCommit);
		Assert.Equal("digest-deadbeef", contentDigest);
		Assert.Equal(revision.StagedRelativePath, stagedRelativePath);
	}

	/// <summary>
	/// Issue #731 AC "pull/import failure cannot disturb the active revision": mutating
	/// the working tree AFTER staging (simulating the NEXT pull's checkout of a
	/// different ref) must never change the already-staged snapshot's file content --
	/// the whole point of copying rather than referencing.
	/// </summary>
	[Fact]
	public async Task StageAsync_StagedSnapshot_IsIndependentOfLaterWorkingTreeMutation()
	{
		string profilePath = Path.Combine(_contentPath, "profile.yml");
		File.WriteAllText(profilePath, "version: 1\n");

		FakeBaselineRepository repository = new();
		ContentRevisionStager stager = new(repository);
		ContentRevision revision = await stager.StageAsync(_contentPath, "commit-v1", "digest-v1", CancellationToken.None);
		string stagedDirectory = Path.Combine(_contentPath, revision.StagedRelativePath);

		// Simulate the next content-pull's checkout mutating the SAME working tree path
		// in place (git checkout of a new ref does exactly this).
		File.WriteAllText(profilePath, "version: 2\n");

		Assert.Equal("version: 1\n", File.ReadAllText(Path.Combine(stagedDirectory, "profile.yml")));
	}

	[Fact]
	public async Task StageAsync_SameDigestStagedTwice_DoesNotDuplicateOrFail()
	{
		File.WriteAllText(Path.Combine(_contentPath, "profile.yml"), "name: invented-profile\n");

		FakeBaselineRepository repository = new();
		ContentRevisionStager stager = new(repository);

		ContentRevision first = await stager.StageAsync(_contentPath, "commit-abc123", "digest-repeat", CancellationToken.None);
		ContentRevision second = await stager.StageAsync(_contentPath, "commit-abc123", "digest-repeat", CancellationToken.None);

		Assert.Equal(first.StagedRelativePath, second.StagedRelativePath);
		Assert.Equal(2, repository.Recorded.Count); // the repository's own idempotency dedups by digest; the stager always records its result.
	}

	/// <summary>
	/// The <c>revisions/</c> directory the stager creates under <c>ContentPath</c> must
	/// never be recursively copied into itself when staging a SECOND, different
	/// revision from the same working tree -- otherwise every subsequent stage would
	/// grow unboundedly by re-copying every prior revision.
	/// </summary>
	[Fact]
	public async Task StageAsync_DoesNotRecursivelyCopyThePriorRevisionsDirectory()
	{
		File.WriteAllText(Path.Combine(_contentPath, "profile.yml"), "version: 1\n");

		FakeBaselineRepository repository = new();
		ContentRevisionStager stager = new(repository);
		await stager.StageAsync(_contentPath, "commit-v1", "digest-v1", CancellationToken.None);

		File.WriteAllText(Path.Combine(_contentPath, "profile.yml"), "version: 2\n");
		ContentRevision second = await stager.StageAsync(_contentPath, "commit-v2", "digest-v2", CancellationToken.None);

		string secondStagedDirectory = Path.Combine(_contentPath, second.StagedRelativePath);
		Assert.False(Directory.Exists(Path.Combine(secondStagedDirectory, "revisions")),
			"staging must exclude the revisions/ directory itself, not recursively copy prior revisions into the new one.");
	}
}
