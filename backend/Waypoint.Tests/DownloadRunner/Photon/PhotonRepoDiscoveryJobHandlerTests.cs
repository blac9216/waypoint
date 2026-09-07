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
/// <c>photon-repo-discovery</c> (issue #1509) against fakes for both HTTP boundaries
/// (<see cref="IPhotonRepoMetadataSource"/>) and the index write path
/// (<see cref="IPhotonIndexRepository"/>) -- the real network parsing is covered by
/// <c>HttpPhotonRepoMetadataSourceTests</c> against fixture bytes, and the real
/// idempotent-upsert behavior against real Postgres is covered by
/// <c>PhotonIndexRepositoryTests</c>. This suite proves the handler's own orchestration:
/// both arches indexed for every version/variant (AC 2), a
/// <c>photon_snapshots</c>-shaped no-repodata repo is indexed with
/// <c>has_repodata=false</c> and never fails the job (AC 3), a probe error on one repo
/// is skipped rather than failing the whole job, and -- AC 5 -- the fake source's
/// request log never contains anything but a <c>photon_versions.json</c> or
/// <c>repomd.xml</c>-shaped call: <see cref="IPhotonRepoMetadataSource"/> exposes no
/// method capable of fetching a package/image file at all, so there is structurally no
/// code path from this handler to a file fetch.
/// </summary>
public sealed class PhotonRepoDiscoveryJobHandlerTests
{
	private sealed class FakeMetadataSource : IPhotonRepoMetadataSource
	{
		public List<string> VersionRequests { get; } = [];
		public List<string> RepomdRequests { get; } = [];

		public IReadOnlyList<string>? Versions { get; set; } = ["5.0"];

		/// <summary>Returned for any repo base URL not overridden in <see cref="ProbesByRepoBaseUrl"/>.</summary>
		public PhotonRepomdProbeResult DefaultProbeResult { get; set; } = PhotonRepomdProbeResult.Found("1700000000", 1);

		/// <summary>Keyed by repo base URL (the exact string the handler built).</summary>
		public Dictionary<string, PhotonRepomdProbeResult> ProbesByRepoBaseUrl { get; } = [];

		public Task<IReadOnlyList<string>?> GetVersionBranchesAsync(string baseUrl, CancellationToken cancellationToken)
		{
			VersionRequests.Add(baseUrl);
			return Task.FromResult(Versions);
		}

		public Task<PhotonRepomdProbeResult> TryGetRepomdRevisionAndPackageCountAsync(string repoBaseUrl, CancellationToken cancellationToken)
		{
			RepomdRequests.Add(repoBaseUrl);
			return Task.FromResult(
				ProbesByRepoBaseUrl.TryGetValue(repoBaseUrl, out PhotonRepomdProbeResult? result)
					? result
					: DefaultProbeResult);
		}
	}

	private sealed class FakeIndexRepository : IPhotonIndexRepository
	{
		public List<PhotonRepoIndexEntry> Upserted { get; } = [];

		public Task UpsertRepoIndexEntryAsync(PhotonRepoIndexEntry entry, CancellationToken cancellationToken)
		{
			Upserted.Add(entry);
			return Task.CompletedTask;
		}

		public Task<IReadOnlyList<PhotonRepoIndexEntry>> ListRepoIndexEntriesAsync(CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<PhotonRepoIndexEntry>>(Upserted);

		public Task<PhotonRepoIndexEntry?> GetRepoIndexEntryAsync(string version, string variant, string arch, CancellationToken cancellationToken) =>
			Task.FromResult(Upserted.LastOrDefault(e => e.Version == version && e.Variant == variant && e.Arch == arch));

		/// <summary>Not exercised by this suite -- the image-index half belongs to <c>PhotonImageDiscoveryJobHandlerTests</c>.</summary>
		public Task UpsertImageIndexEntryAsync(PhotonImageIndexEntry entry, CancellationToken cancellationToken) =>
			throw new NotSupportedException("Not exercised by PhotonRepoDiscoveryJobHandlerTests.");

		public Task<IReadOnlyList<PhotonImageIndexEntry>> ListImageIndexEntriesAsync(CancellationToken cancellationToken) =>
			throw new NotSupportedException("Not exercised by PhotonRepoDiscoveryJobHandlerTests.");

		public Task<PhotonImageIndexEntry?> GetImageIndexEntryAsync(string version, string channel, string relativePath, CancellationToken cancellationToken) =>
			throw new NotSupportedException("Not exercised by PhotonRepoDiscoveryJobHandlerTests.");
	}

	private sealed class FakeEventPublisher : IJobEventPublisher
	{
		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken) => Task.CompletedTask;
	}

	private static JobExecutionContext ContextFor(string payload)
	{
		ClaimedJob job = new(
			Id: Guid.NewGuid(), RunId: null, JobType: "photon-repo-discovery", TargetId: null, TargetName: null,
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
		PhotonRepoDiscoveryJobHandler handler = new(new FakeMetadataSource(), new FakeIndexRepository(), NullLogger<PhotonRepoDiscoveryJobHandler>.Instance);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor("{}"), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
	}

	[Fact]
	public async Task ExecuteAsync_NoVersionsDiscoverable_Fails()
	{
		FakeMetadataSource source = new() { Versions = null };
		PhotonRepoDiscoveryJobHandler handler = new(source, new FakeIndexRepository(), NullLogger<PhotonRepoDiscoveryJobHandler>.Instance);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(Payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
	}

	[Fact]
	public async Task ExecuteAsync_IndexesBothArches_ForEveryVariant()
	{
		FakeMetadataSource source = new();
		FakeIndexRepository repository = new();
		PhotonRepoDiscoveryJobHandler handler = new(source, repository, NullLogger<PhotonRepoDiscoveryJobHandler>.Instance);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(Payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Equal(PhotonRepoVariants.All.Count * PhotonArches.All.Count, repository.Upserted.Count);
		foreach (string variant in PhotonRepoVariants.All)
		{
			foreach (string arch in PhotonArches.All)
			{
				Assert.Contains(repository.Upserted, e => e.Version == "5.0" && e.Variant == variant && e.Arch == arch);
			}
		}
	}

	[Fact]
	public async Task ExecuteAsync_NoRepodataRepo_IsIndexedWithoutFailingTheJob()
	{
		FakeMetadataSource source = new();
		string snapshotsRepoUrl = $"{BaseUrl}/5.0/{PhotonRepoDiscoveryJobHandler.RepoDirectoryName("5.0", PhotonRepoVariants.Snapshots, PhotonArches.X8664)}";
		source.ProbesByRepoBaseUrl[snapshotsRepoUrl] = PhotonRepomdProbeResult.NotFound;
		FakeIndexRepository repository = new();
		PhotonRepoDiscoveryJobHandler handler = new(source, repository, NullLogger<PhotonRepoDiscoveryJobHandler>.Instance);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(Payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		PhotonRepoIndexEntry snapshotsEntry = Assert.Single(
			repository.Upserted, e => e.Variant == PhotonRepoVariants.Snapshots && e.Arch == PhotonArches.X8664);
		Assert.False(snapshotsEntry.HasRepodata);
		Assert.Null(snapshotsEntry.RepomdRevision);
		Assert.Null(snapshotsEntry.PackageCount);
	}

	[Fact]
	public async Task ExecuteAsync_OneRepoProbeError_IsSkippedButJobStillSucceeds()
	{
		FakeMetadataSource source = new();
		string releaseRepoUrl = $"{BaseUrl}/5.0/{PhotonRepoDiscoveryJobHandler.RepoDirectoryName("5.0", PhotonRepoVariants.Release, PhotonArches.Aarch64)}";
		source.ProbesByRepoBaseUrl[releaseRepoUrl] = PhotonRepomdProbeResult.Failed("connection reset");
		FakeIndexRepository repository = new();
		PhotonRepoDiscoveryJobHandler handler = new(source, repository, NullLogger<PhotonRepoDiscoveryJobHandler>.Instance);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(Payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Contains("connection reset", outcome.Note);
		Assert.DoesNotContain(repository.Upserted, e => e.Variant == PhotonRepoVariants.Release && e.Arch == PhotonArches.Aarch64);
		// Every other repo in the sweep still got indexed.
		Assert.Equal(PhotonRepoVariants.All.Count * PhotonArches.All.Count - 1, repository.Upserted.Count);
	}

	/// <summary>Round-0 review finding #3: an all-probes-failed sweep must fail the job, not report "Indexed 0" as success.</summary>
	[Fact]
	public async Task ExecuteAsync_AllProbesFail_FailsTheJob()
	{
		FakeMetadataSource source = new() { DefaultProbeResult = PhotonRepomdProbeResult.Failed("connection reset") };
		FakeIndexRepository repository = new();
		PhotonRepoDiscoveryJobHandler handler = new(source, repository, NullLogger<PhotonRepoDiscoveryJobHandler>.Instance);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(Payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains((PhotonRepoVariants.All.Count * PhotonArches.All.Count).ToString(System.Globalization.CultureInfo.InvariantCulture), outcome.Note);
		Assert.Empty(repository.Upserted);
	}

	/// <summary>
	/// Round-0 review finding #3: a sweep with SOME successes and SOME failures stays a
	/// success-with-warnings -- only an all-failed sweep fails the job.
	/// </summary>
	[Fact]
	public async Task ExecuteAsync_SomeProbesFailButAtLeastOneIndexed_StaysSucceeded()
	{
		FakeMetadataSource source = new();
		string releaseRepoUrl = $"{BaseUrl}/5.0/{PhotonRepoDiscoveryJobHandler.RepoDirectoryName("5.0", PhotonRepoVariants.Release, PhotonArches.Aarch64)}";
		source.ProbesByRepoBaseUrl[releaseRepoUrl] = PhotonRepomdProbeResult.Failed("connection reset");
		FakeIndexRepository repository = new();
		PhotonRepoDiscoveryJobHandler handler = new(source, repository, NullLogger<PhotonRepoDiscoveryJobHandler>.Instance);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(Payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.NotEmpty(repository.Upserted);
	}

	/// <summary>
	/// Round-2 review finding 1: an upstream repo-directory rename classifies EVERY probe
	/// <c>Absent</c> while <c>photon_versions.json</c> keeps serving -- zero errors, zero
	/// rows. The prior <c>errors.Count == totalProbes</c> gate returned
	/// <c>Succeeded("Indexed 0 ...")</c> for that sweep, hiding a permanently stale index
	/// behind a green run. The gate is <c>indexed == 0</c>, so this fails, and the note
	/// names the absent/error counts.
	/// </summary>
	[Fact]
	public async Task ExecuteAsync_EveryProbeAbsent_FailsTheJob()
	{
		FakeMetadataSource source = new() { DefaultProbeResult = PhotonRepomdProbeResult.Absent };
		FakeIndexRepository repository = new();
		PhotonRepoDiscoveryJobHandler handler = new(source, repository, NullLogger<PhotonRepoDiscoveryJobHandler>.Instance);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(Payload), CancellationToken.None);

		int totalProbes = PhotonRepoVariants.All.Count * PhotonArches.All.Count;
		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Empty(repository.Upserted);
		Assert.NotNull(outcome.Note);
		Assert.Contains($"{totalProbes} absent upstream", outcome.Note, StringComparison.Ordinal);
		Assert.Contains("0 probe error(s)", outcome.Note, StringComparison.Ordinal);
		// Every probe was still attempted -- the failure is the verdict, not an early exit.
		Assert.Equal(totalProbes, source.RepomdRequests.Count);
	}

	/// <summary>Round-0 review finding #4: a repo directory absent upstream produces no row, only a debug-level note.</summary>
	[Fact]
	public async Task ExecuteAsync_AbsentRepoDirectory_ProducesNoRowAndIsNotAnError()
	{
		FakeMetadataSource source = new();
		string debuginfoRepoUrl = $"{BaseUrl}/5.0/{PhotonRepoDiscoveryJobHandler.RepoDirectoryName("5.0", PhotonRepoVariants.Debuginfo, PhotonArches.Aarch64)}";
		source.ProbesByRepoBaseUrl[debuginfoRepoUrl] = PhotonRepomdProbeResult.Absent;
		FakeIndexRepository repository = new();
		PhotonRepoDiscoveryJobHandler handler = new(source, repository, NullLogger<PhotonRepoDiscoveryJobHandler>.Instance);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(Payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.DoesNotContain(repository.Upserted, e => e.Variant == PhotonRepoVariants.Debuginfo && e.Arch == PhotonArches.Aarch64);
		// Every other repo in the sweep still got indexed; only the absent one is missing.
		Assert.Equal(PhotonRepoVariants.All.Count * PhotonArches.All.Count - 1, repository.Upserted.Count);
	}

	/// <summary>
	/// Proves only what this handler can observe through the fake source: every call it
	/// makes is shaped like a versions-manifest or repomd probe, never anything
	/// resembling a package or image file fetch. This is an interface-level guarantee
	/// (<see cref="IPhotonRepoMetadataSource"/> has no method that could even be asked
	/// for a package/image file) -- it does NOT prove that the real HTTP implementation
	/// honors it end-to-end; that proof (rejecting a traversing <c>repomd.xml</c>
	/// <c>&lt;location href&gt;</c> before any fetch) lives in
	/// <c>HttpPhotonRepoMetadataSourceTests</c>, the only suite that can observe the
	/// layer that actually issues requests.
	/// </summary>
	[Fact]
	public async Task ExecuteAsync_NeverRequestsAnythingOtherThanVersionsOrRepomd()
	{
		FakeMetadataSource source = new();
		PhotonRepoDiscoveryJobHandler handler = new(source, new FakeIndexRepository(), NullLogger<PhotonRepoDiscoveryJobHandler>.Instance);

		await handler.ExecuteAsync(ContextFor(Payload), CancellationToken.None);

		Assert.Single(source.VersionRequests);
		Assert.Equal(BaseUrl, source.VersionRequests[0]);
		Assert.Equal(PhotonRepoVariants.All.Count * PhotonArches.All.Count, source.RepomdRequests.Count);
		Assert.All(source.RepomdRequests, url => Assert.StartsWith($"{BaseUrl}/5.0/photon_", url, StringComparison.Ordinal));
		Assert.All(source.RepomdRequests, url => Assert.DoesNotContain(".rpm", url, StringComparison.Ordinal));
		Assert.All(source.RepomdRequests, url => Assert.DoesNotContain(".iso", url, StringComparison.Ordinal));
	}
}
