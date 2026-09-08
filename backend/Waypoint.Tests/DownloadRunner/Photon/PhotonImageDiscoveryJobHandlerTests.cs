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
using Waypoint.Core.Downloads.Photon;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.Downloads.Photon;
using Waypoint.Infrastructure.Jobs;
using Xunit;

namespace Waypoint.Tests.DownloadRunner.Photon;

/// <summary>
/// <c>photon-image-discovery</c> (issue #1790) against fakes for both HTTP boundaries
/// (<see cref="IPhotonRepoMetadataSource"/> for version enumeration,
/// <see cref="IPhotonImageListingSource"/> for the per-channel listing) and the index
/// write path (<see cref="IPhotonIndexRepository"/>). Mirrors
/// <c>PhotonRepoDiscoveryJobHandlerTests</c>'s own convention.
/// </summary>
public sealed class PhotonImageDiscoveryJobHandlerTests
{
	private sealed class FakeVersionSource : IPhotonRepoMetadataSource
	{
		public List<string> VersionRequests { get; } = [];

		public IReadOnlyList<string>? Versions { get; set; } = ["5.0"];

		public Task<IReadOnlyList<string>?> GetVersionBranchesAsync(string baseUrl, CancellationToken cancellationToken)
		{
			VersionRequests.Add(baseUrl);
			return Task.FromResult(Versions);
		}

		public Task<PhotonRepomdProbeResult> TryGetRepomdRevisionAndPackageCountAsync(string repoBaseUrl, CancellationToken cancellationToken) =>
			throw new NotSupportedException("Not exercised by PhotonImageDiscoveryJobHandlerTests.");
	}

	private sealed class FakeImageSource : IPhotonImageListingSource
	{
		public List<string> ListingRequests { get; } = [];

		/// <summary>Returned for any channel base URL not overridden in <see cref="ResultsByChannelBaseUrl"/>.</summary>
		public PhotonImageListingResult DefaultResult { get; set; } = PhotonImageListingResult.Found(
			[new PhotonImageListingEntry("photon-5.0-x86_64.iso", 123456, "\"etag\"")]);

		public Dictionary<string, PhotonImageListingResult> ResultsByChannelBaseUrl { get; } = [];

		public Task<PhotonImageListingResult> ListImagesAsync(string channelBaseUrl, CancellationToken cancellationToken)
		{
			ListingRequests.Add(channelBaseUrl);
			return Task.FromResult(
				ResultsByChannelBaseUrl.TryGetValue(channelBaseUrl, out PhotonImageListingResult? result)
					? result
					: DefaultResult);
		}
	}

	private sealed class FakeIndexRepository : IPhotonIndexRepository
	{
		public List<PhotonImageIndexEntry> Upserted { get; } = [];

		public Task UpsertImageIndexEntryAsync(PhotonImageIndexEntry entry, CancellationToken cancellationToken)
		{
			Upserted.Add(entry);
			return Task.CompletedTask;
		}

		public Task<IReadOnlyList<PhotonImageIndexEntry>> ListImageIndexEntriesAsync(CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<PhotonImageIndexEntry>>(Upserted);

		public Task<PhotonImageIndexEntry?> GetImageIndexEntryAsync(string version, string channel, string relativePath, CancellationToken cancellationToken) =>
			Task.FromResult(Upserted.LastOrDefault(e => e.Version == version && e.Channel == channel && e.RelativePath == relativePath));

		public Task UpsertRepoIndexEntryAsync(PhotonRepoIndexEntry entry, CancellationToken cancellationToken) =>
			throw new NotSupportedException("Not exercised by PhotonImageDiscoveryJobHandlerTests.");

		public Task<IReadOnlyList<PhotonRepoIndexEntry>> ListRepoIndexEntriesAsync(CancellationToken cancellationToken) =>
			throw new NotSupportedException("Not exercised by PhotonImageDiscoveryJobHandlerTests.");

		public Task<PhotonRepoIndexEntry?> GetRepoIndexEntryAsync(string version, string variant, string arch, CancellationToken cancellationToken) =>
			throw new NotSupportedException("Not exercised by PhotonImageDiscoveryJobHandlerTests.");
	}

	private sealed class FakeEventPublisher : IJobEventPublisher
	{
		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken) => Task.CompletedTask;
	}

	private static JobExecutionContext ContextFor(string payload)
	{
		ClaimedJob job = new(
			Id: Guid.NewGuid(), RunId: null, JobType: "photon-image-discovery", TargetId: null, TargetName: null,
			CredentialId: null, Priority: 4, Payload: payload, AttemptCount: 1, MaxAttempts: 3);
		return new JobExecutionContext(
			job, "worker-test", new FakeEventPublisher(),
			new JobQueueRepository("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x", NullLogger<JobQueueRepository>.Instance),
			JobShape.Simple);
	}

	private const string BaseUrl = "https://photon.example.internal/photon";
	private const string Payload = """{"base_url": "https://photon.example.internal/photon"}""";

	[Fact]
	public async Task ExecuteAsync_MissingBaseUrl_Fails()
	{
		PhotonImageDiscoveryJobHandler handler = new(
			new FakeVersionSource(), new FakeImageSource(), new FakeIndexRepository(), NullLogger<PhotonImageDiscoveryJobHandler>.Instance);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor("{}"), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
	}

	[Fact]
	public async Task ExecuteAsync_NoVersionsDiscoverable_Fails()
	{
		FakeVersionSource versionSource = new() { Versions = null };
		PhotonImageDiscoveryJobHandler handler = new(
			versionSource, new FakeImageSource(), new FakeIndexRepository(), NullLogger<PhotonImageDiscoveryJobHandler>.Instance);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(Payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
	}

	[Fact]
	public async Task ExecuteAsync_IndexesEveryChannelsEntries()
	{
		FakeVersionSource versionSource = new();
		FakeImageSource imageSource = new();
		FakeIndexRepository repository = new();
		PhotonImageDiscoveryJobHandler handler = new(
			versionSource, imageSource, repository, NullLogger<PhotonImageDiscoveryJobHandler>.Instance);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(Payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Equal(PhotonImageChannels.All.Count, repository.Upserted.Count);
		foreach (string channel in PhotonImageChannels.All)
		{
			Assert.Contains(repository.Upserted, e => e.Version == "5.0" && e.Channel == channel && e.ImageKind == PhotonImageKinds.Iso);
		}
	}

	/// <summary>
	/// A channel the vendor never published (an explicit 404 on its listing) is a
	/// normal, expected outcome: no row, no error, and -- the half round-1 finding 2
	/// turns on -- an UNQUALIFIED success note, because the mirror really is fully
	/// indexed. Compare
	/// <see cref="ExecuteAsync_ChannelIndeterminate_IsSurfacedAsAFaultNotAsUnpublished"/>,
	/// which feeds the same handler the other outcome and asserts the opposite.
	/// </summary>
	[Fact]
	public async Task ExecuteAsync_ChannelDefinitivelyAbsent_ProducesNoRowAndIsNotAnError()
	{
		FakeVersionSource versionSource = new();
		FakeImageSource imageSource = new();
		string rcChannelUrl = $"{BaseUrl}/5.0/{PhotonImageDiscoveryJobHandler.ChannelDirectoryName(PhotonImageChannels.Rc)}";
		imageSource.ResultsByChannelBaseUrl[rcChannelUrl] = PhotonImageListingResult.Absent;
		FakeIndexRepository repository = new();
		PhotonImageDiscoveryJobHandler handler = new(
			versionSource, imageSource, repository, NullLogger<PhotonImageDiscoveryJobHandler>.Instance);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(Payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.DoesNotContain(repository.Upserted, e => e.Channel == PhotonImageChannels.Rc);
		Assert.Equal(PhotonImageChannels.All.Count - 1, repository.Upserted.Count);
		Assert.DoesNotContain("INDEXING INCOMPLETE", outcome.Note, StringComparison.Ordinal);
	}

	/// <summary>
	/// Round-1 review finding 2: a channel whose listing could not be determined (a 5xx
	/// here; a 403, transport failure, timeout or over-cap document take the same arm)
	/// must NOT read as "channel unpublished". The sweep is incomplete by an unknown
	/// number of files, so the outcome names the channel and says so, rather than
	/// riding on the global <c>indexed == 0</c> gate that one file from any other
	/// channel already satisfies. Mutating the handler to route
	/// <see cref="PhotonImageListingKind.Indeterminate"/> down the Absent arm turns
	/// this test red while
	/// <see cref="ExecuteAsync_ChannelDefinitivelyAbsent_ProducesNoRowAndIsNotAnError"/>
	/// stays green -- which is exactly the distinction the contract exists to make.
	/// </summary>
	[Fact]
	public async Task ExecuteAsync_ChannelIndeterminate_IsSurfacedAsAFaultNotAsUnpublished()
	{
		FakeVersionSource versionSource = new();
		FakeImageSource imageSource = new();
		string rcChannelUrl = $"{BaseUrl}/5.0/{PhotonImageDiscoveryJobHandler.ChannelDirectoryName(PhotonImageChannels.Rc)}";
		imageSource.ResultsByChannelBaseUrl[rcChannelUrl] = PhotonImageListingResult.Indeterminate(
			"channel listing returned HTTP 503 (Service Unavailable)");
		FakeIndexRepository repository = new();
		PhotonImageDiscoveryJobHandler handler = new(
			versionSource, imageSource, repository, NullLogger<PhotonImageDiscoveryJobHandler>.Instance);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(Payload), CancellationToken.None);

		// Every other channel still indexed -- one indeterminate channel does not fail the sweep...
		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Equal(PhotonImageChannels.All.Count - 1, repository.Upserted.Count);
		Assert.DoesNotContain(repository.Upserted, e => e.Channel == PhotonImageChannels.Rc);
		// ... but it is never reported as an unqualified success, and the operator can
		// see WHICH channel and WHY.
		Assert.Contains("INDEXING INCOMPLETE", outcome.Note, StringComparison.Ordinal);
		Assert.Contains(PhotonImageChannels.Rc, outcome.Note, StringComparison.Ordinal);
		Assert.Contains("503", outcome.Note, StringComparison.Ordinal);
	}

	/// <summary>
	/// The all-indeterminate sweep: nothing indexed, and the failure note must say the
	/// channels were indeterminate rather than unpublished -- an operator reading
	/// "0 unpublished, 5 indeterminate" chases an upstream/network fault, one reading
	/// "5 unpublished" chases a vendor that removed the channels.
	/// </summary>
	[Fact]
	public async Task ExecuteAsync_EveryChannelIndeterminate_FailsAndNamesTheChannels()
	{
		FakeVersionSource versionSource = new();
		FakeImageSource imageSource = new()
		{
			DefaultResult = PhotonImageListingResult.Indeterminate("connection reset"),
		};
		FakeIndexRepository repository = new();
		PhotonImageDiscoveryJobHandler handler = new(
			versionSource, imageSource, repository, NullLogger<PhotonImageDiscoveryJobHandler>.Instance);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(Payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Empty(repository.Upserted);
		Assert.Contains("connection reset", outcome.Note, StringComparison.Ordinal);
		Assert.Contains("indeterminate", outcome.Note, StringComparison.Ordinal);
	}

	/// <summary>Mirrors the sibling repo-discovery job's all-failed gate: an entirely-unpublished sweep fails the job, not "Indexed 0" success.</summary>
	[Fact]
	public async Task ExecuteAsync_EveryChannelUnpublished_FailsTheJob()
	{
		FakeVersionSource versionSource = new();
		FakeImageSource imageSource = new() { DefaultResult = PhotonImageListingResult.Absent };
		FakeIndexRepository repository = new();
		PhotonImageDiscoveryJobHandler handler = new(
			versionSource, imageSource, repository, NullLogger<PhotonImageDiscoveryJobHandler>.Instance);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(Payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Empty(repository.Upserted);
	}

	[Fact]
	public async Task ExecuteAsync_ReDiscoveryOfSameEntry_UpsertsNotDuplicates()
	{
		FakeVersionSource versionSource = new();
		FakeImageSource imageSource = new();
		FakeIndexRepository repository = new();
		PhotonImageDiscoveryJobHandler handler = new(
			versionSource, imageSource, repository, NullLogger<PhotonImageDiscoveryJobHandler>.Instance);

		await handler.ExecuteAsync(ContextFor(Payload), CancellationToken.None);
		int firstPassCount = repository.Upserted.Count;
		await handler.ExecuteAsync(ContextFor(Payload), CancellationToken.None);

		// The fake repository records every upsert call (never dedupes) -- the real
		// idempotent-upsert behavior against Postgres's UNIQUE constraint (this table's
		// ON CONFLICT target) is proven against a real database by
		// PhotonImageIndexRunnerRoleGrantTests.DownloadRunnerRole_CanUpsertAndListImageIndexEntries;
		// this asserts only that the handler issues one
		// upsert call per (version, channel, file), same shape both passes.
		Assert.Equal(firstPassCount, repository.Upserted.Count - firstPassCount);
	}

	/// <summary>
	/// Interface-level guarantee mirroring the sibling suite's own AC 5 test:
	/// <see cref="IPhotonImageListingSource"/> has no method capable of fetching an
	/// image file's body at all, so there is structurally no code path from this
	/// handler to a file fetch. The real HTTP implementation's own no-GET-on-a-file
	/// proof lives in <c>HttpPhotonImageListingSourceTests</c>.
	/// </summary>
	[Fact]
	public async Task ExecuteAsync_NeverRequestsAnythingOtherThanVersionsOrChannelListings()
	{
		FakeVersionSource versionSource = new();
		FakeImageSource imageSource = new();
		PhotonImageDiscoveryJobHandler handler = new(
			versionSource, imageSource, new FakeIndexRepository(), NullLogger<PhotonImageDiscoveryJobHandler>.Instance);

		await handler.ExecuteAsync(ContextFor(Payload), CancellationToken.None);

		Assert.Single(versionSource.VersionRequests);
		Assert.Equal(BaseUrl, versionSource.VersionRequests[0]);
		Assert.Equal(PhotonImageChannels.All.Count, imageSource.ListingRequests.Count);
		Assert.All(imageSource.ListingRequests, url => Assert.StartsWith($"{BaseUrl}/5.0/", url, StringComparison.Ordinal));
	}
}
