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

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Waypoint.Core.Catalog;
using Waypoint.Core.ContentLibraries;
using Waypoint.Core.Jobs;
using Waypoint.Core.Pagination;
using Waypoint.Infrastructure.ContentLibraries;
using Waypoint.Infrastructure.Execution.ContentLibrary;
using Waypoint.Infrastructure.Jobs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Execution.ContentLibrary;

/// <summary>
/// Issue #1513: <c>SupervisorLibrarySyncJobHandler</c>'s orchestration against a real
/// temp filesystem (a fake store cannot prove the SUPERVISOR product tree's bytes
/// actually land under the library's own directory as a valid VCSP library, the
/// same rationale <c>VcspContentLibraryWriterTests</c> states) but fully in-memory
/// repositories, mirroring <c>SubscriptionEvaluationJobHandlerTests</c>'s convention
/// for everything that does not need Postgres.
/// </summary>
public sealed class SupervisorLibrarySyncJobHandlerTests : IDisposable
{
	private readonly string _depotRoot = Directory.CreateTempSubdirectory("wp-supervisor-sync-depot-test").FullName;
	private readonly string _libraryRoot = Directory.CreateTempSubdirectory("wp-supervisor-sync-library-test").FullName;

	public void Dispose()
	{
		TryDelete(_depotRoot);
		TryDelete(_libraryRoot);
	}

	private static void TryDelete(string path)
	{
		try
		{
			Directory.Delete(path, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	private sealed class FakeLibraryRepository : IContentLibraryRepository
	{
		private readonly List<Waypoint.Core.ContentLibraries.ContentLibrary> _libraries = [];

		public Task<(ContentLibraryCreateOutcome Outcome, Waypoint.Core.ContentLibraries.ContentLibrary? Library)> CreateAsync(string name, CancellationToken cancellationToken)
		{
			if (_libraries.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
			{
				return Task.FromResult<(ContentLibraryCreateOutcome, Waypoint.Core.ContentLibraries.ContentLibrary?)>((ContentLibraryCreateOutcome.NameTaken, null));
			}

			Waypoint.Core.ContentLibraries.ContentLibrary library = new(Guid.NewGuid(), name, $"/unused/{name}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
			_libraries.Add(library);
			return Task.FromResult<(ContentLibraryCreateOutcome, Waypoint.Core.ContentLibraries.ContentLibrary?)>((ContentLibraryCreateOutcome.Created, library));
		}

		public Task<Waypoint.Core.ContentLibraries.ContentLibrary?> GetAsync(Guid id, CancellationToken cancellationToken) =>
			Task.FromResult(_libraries.FirstOrDefault(l => l.Id == id));

		public Task<IReadOnlyList<Waypoint.Core.ContentLibraries.ContentLibrary>> ListAsync(CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<Waypoint.Core.ContentLibraries.ContentLibrary>>(_libraries);

		public Task<ContentLibraryDeleteOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken) => throw new InvalidOperationException();
	}

	private sealed class FakeItemRepository : IContentLibraryItemRepository
	{
		private readonly List<ContentLibraryItem> _items = [];

		public Task<(ContentLibraryItemAddOutcome Outcome, ContentLibraryItem? Item)> AddAsync(
			Guid id, Guid libraryId, string directoryName, string name, string type, string description,
			IReadOnlyList<ContentLibraryItemFileWrite> files, CancellationToken cancellationToken)
		{
			ContentLibraryItem item = new(id, libraryId, directoryName, name, type, description, 1, files, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
			_items.Add(item);
			return Task.FromResult<(ContentLibraryItemAddOutcome, ContentLibraryItem?)>((ContentLibraryItemAddOutcome.Added, item));
		}

		public Task<ContentLibraryItem?> GetAsync(Guid libraryId, Guid itemId, CancellationToken cancellationToken) =>
			Task.FromResult(_items.FirstOrDefault(i => i.LibraryId == libraryId && i.Id == itemId));

		public Task<IReadOnlyList<ContentLibraryItem>> ListAsync(Guid libraryId, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<ContentLibraryItem>>([.. _items.Where(i => i.LibraryId == libraryId)]);

		public Task<ContentLibraryItemUpdateOutcome> UpdateAsync(
			Guid libraryId, Guid itemId, string name, string type, string description,
			IReadOnlyList<ContentLibraryItemFileWrite> files, CancellationToken cancellationToken)
		{
			int index = _items.FindIndex(i => i.LibraryId == libraryId && i.Id == itemId);
			if (index < 0)
			{
				return Task.FromResult(ContentLibraryItemUpdateOutcome.NotFound);
			}

			ContentLibraryItem existing = _items[index];
			_items[index] = existing with { Name = name, Type = type, Description = description, Files = files, Version = existing.Version + 1 };
			return Task.FromResult(ContentLibraryItemUpdateOutcome.Updated);
		}

		public Task<ContentLibraryItemRemoveOutcome> RemoveAsync(Guid libraryId, Guid itemId, CancellationToken cancellationToken) => throw new InvalidOperationException();
	}

	private sealed class FakeWriter : IContentLibraryWriter
	{
		public int WriteCount { get; private set; }
		public IReadOnlyList<ContentLibraryItemWrite>? LastItems { get; private set; }

		public Task WriteAsync(Waypoint.Core.ContentLibraries.ContentLibrary library, IReadOnlyList<ContentLibraryItemWrite> items, CancellationToken cancellationToken)
		{
			WriteCount++;
			LastItems = items;
			return Task.CompletedTask;
		}
	}

	private sealed class FakeArtifactRepository(IReadOnlyList<DepotArtifact> artifacts) : IDepotArtifactRepository
	{
		public Task<Guid> UpsertAsync(DepotArtifactUpsert artifact, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task<int> RekeyManyAsync(IReadOnlyDictionary<string, string> renames, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task<DepotArtifact?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task<IReadOnlyList<DepotArtifact>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) => throw new InvalidOperationException();

		public Task<(IReadOnlyList<DepotArtifact> Items, long TotalCount)> ListAsync(DepotArtifactFilter filter, PageRequest page, CancellationToken cancellationToken)
			=> Task.FromResult<(IReadOnlyList<DepotArtifact>, long)>((artifacts, artifacts.Count));

		public Task<bool> SupersedeCatalogDocumentRowAsync(string catalogDocumentRelativePath, CancellationToken cancellationToken) => throw new InvalidOperationException();
	}

	private static JobExecutionContext ContextFor()
	{
		ClaimedJob job = new(
			Id: Guid.NewGuid(), RunId: Guid.NewGuid(), JobType: RunTypes.ContentLibrarySync, TargetId: null, TargetName: null,
			CredentialId: null, Priority: 1, Payload: "{}", AttemptCount: 1, MaxAttempts: 3);
		return new JobExecutionContext(
			job, "worker-test", new NoopEventPublisher(),
			new JobQueueRepository("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x", NullLogger<JobQueueRepository>.Instance),
			JobShape.Simple);
	}

	private sealed class NoopEventPublisher : IJobEventPublisher
	{
		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken) => Task.CompletedTask;
	}

	private void WriteDepotFile(string relativePath, byte[] bytes, out string sha256)
	{
		string fullPath = Path.Combine(_depotRoot, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
		File.WriteAllBytes(fullPath, bytes);
		sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
	}

	private SupervisorLibrarySyncJobHandler MakeHandler(
		FakeLibraryRepository libraries, FakeItemRepository items, FakeWriter writer, FakeArtifactRepository artifacts) =>
		new(
			libraries, items, writer, artifacts,
			Options.Create(new ContentLibraryOptions { RootPath = _libraryRoot }),
			Options.Create(new CatalogOptions { DepotPath = _depotRoot }));

	[Fact]
	public async Task ExecuteAsync_NoSupervisorArtifacts_SucceedsAsNoOp()
	{
		FakeWriter writer = new();
		SupervisorLibrarySyncJobHandler handler = MakeHandler(new FakeLibraryRepository(), new FakeItemRepository(), writer, new FakeArtifactRepository([]));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Equal(0, writer.WriteCount);
	}

	[Fact]
	public async Task ExecuteAsync_NewOvaArtifact_CreatesOvfItemAndCopiesBytes()
	{
		WriteDepotFile("PROD/COMP/SUPERVISOR/vm-service.ova", [1, 2, 3, 4], out string sha256);
		DepotArtifact artifact = new(
			Guid.NewGuid(), "PROD/COMP/SUPERVISOR/vm-service.ova", sha256, DepotArtifactStatuses.Present,
			"SUPERVISOR", null, "{}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, SizeBytes: 4);

		FakeItemRepository items = new();
		FakeWriter writer = new();
		SupervisorLibrarySyncJobHandler handler = MakeHandler(new FakeLibraryRepository(), items, writer, new FakeArtifactRepository([artifact]));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Equal(1, writer.WriteCount);
		ContentLibraryItemWrite written = Assert.Single(writer.LastItems!);
		Assert.Equal(ContentLibraryItemTypes.Ovf, written.Type);
		Assert.Equal("vm-service.ova", written.Name);
		Assert.Equal(artifact.ExternalId, written.Description);

		string copiedPath = Path.Combine(_libraryRoot, "supervisor", written.DirectoryName, "vm-service.ova");
		Assert.True(File.Exists(copiedPath));
		Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(copiedPath));
	}

	[Fact]
	public async Task ExecuteAsync_OtherProduct_IsIgnored()
	{
		DepotArtifact artifact = new(
			Guid.NewGuid(), "PROD/COMP/ESXI/esxi.zip", "deadbeef", DepotArtifactStatuses.Present,
			"ESXI", null, "{}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

		FakeWriter writer = new();
		SupervisorLibrarySyncJobHandler handler = MakeHandler(new FakeLibraryRepository(), new FakeItemRepository(), writer, new FakeArtifactRepository([]));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Equal(0, writer.WriteCount);
	}

	[Fact]
	public async Task ExecuteAsync_MissingSourceFile_SkipsWithoutError()
	{
		DepotArtifact artifact = new(
			Guid.NewGuid(), "PROD/COMP/SUPERVISOR/missing.zip", "deadbeef", DepotArtifactStatuses.Present,
			"SUPERVISOR", null, "{}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

		FakeWriter writer = new();
		SupervisorLibrarySyncJobHandler handler = MakeHandler(new FakeLibraryRepository(), new FakeItemRepository(), writer, new FakeArtifactRepository([artifact]));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Equal(0, writer.WriteCount);
		Assert.Contains("1 skipped", outcome.Note);
	}

	[Fact]
	public async Task ExecuteAsync_ZipAndJsonArtifacts_ClassifyAsVcspOther()
	{
		WriteDepotFile("PROD/COMP/SUPERVISOR/spherelet.zip", [9, 9], out string zipSha);
		WriteDepotFile("PROD/COMP/SUPERVISOR/solution.json", [8, 8], out string jsonSha);
		DepotArtifact zipArtifact = new(
			Guid.NewGuid(), "PROD/COMP/SUPERVISOR/spherelet.zip", zipSha, DepotArtifactStatuses.Present, "SUPERVISOR", null, "{}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
		DepotArtifact jsonArtifact = new(
			Guid.NewGuid(), "PROD/COMP/SUPERVISOR/solution.json", jsonSha, DepotArtifactStatuses.Present, "SUPERVISOR", null, "{}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

		FakeWriter writer = new();
		SupervisorLibrarySyncJobHandler handler = MakeHandler(
			new FakeLibraryRepository(), new FakeItemRepository(), writer, new FakeArtifactRepository([zipArtifact, jsonArtifact]));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Equal(2, writer.LastItems!.Count);
		Assert.All(writer.LastItems!, item => Assert.Equal(ContentLibraryItemTypes.Other, item.Type));
	}

	[Fact]
	public async Task ExecuteAsync_RerunWithUnchangedArtifact_IsNoOp()
	{
		WriteDepotFile("PROD/COMP/SUPERVISOR/vm-service.ova", [1, 2, 3, 4], out string sha256);
		DepotArtifact artifact = new(
			Guid.NewGuid(), "PROD/COMP/SUPERVISOR/vm-service.ova", sha256, DepotArtifactStatuses.Present, "SUPERVISOR", null, "{}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

		FakeLibraryRepository libraries = new();
		FakeItemRepository items = new();
		FakeArtifactRepository artifacts = new([artifact]);
		FakeWriter firstWriter = new();
		SupervisorLibrarySyncJobHandler firstHandler = new(
			libraries, items, firstWriter, artifacts,
			Options.Create(new ContentLibraryOptions { RootPath = _libraryRoot }),
			Options.Create(new CatalogOptions { DepotPath = _depotRoot }));
		await firstHandler.ExecuteAsync(ContextFor(), CancellationToken.None);
		Assert.Equal(1, firstWriter.WriteCount);

		FakeWriter secondWriter = new();
		SupervisorLibrarySyncJobHandler secondHandler = new(
			libraries, items, secondWriter, artifacts,
			Options.Create(new ContentLibraryOptions { RootPath = _libraryRoot }),
			Options.Create(new CatalogOptions { DepotPath = _depotRoot }));
		JobExecutionOutcome outcome = await secondHandler.ExecuteAsync(ContextFor(), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Equal(0, secondWriter.WriteCount);
		Assert.Contains("1 unchanged", outcome.Note);
	}

	[Fact]
	public async Task ExecuteAsync_ChangedArtifactContent_UpdatesInPlace()
	{
		WriteDepotFile("PROD/COMP/SUPERVISOR/vm-service.ova", [1, 2, 3, 4], out string firstSha);
		DepotArtifact firstArtifact = new(
			Guid.NewGuid(), "PROD/COMP/SUPERVISOR/vm-service.ova", firstSha, DepotArtifactStatuses.Present, "SUPERVISOR", null, "{}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

		FakeLibraryRepository libraries = new();
		FakeItemRepository items = new();
		FakeArtifactRepository firstArtifacts = new([firstArtifact]);
		SupervisorLibrarySyncJobHandler firstHandler = new(
			libraries, items, new FakeWriter(), firstArtifacts,
			Options.Create(new ContentLibraryOptions { RootPath = _libraryRoot }),
			Options.Create(new CatalogOptions { DepotPath = _depotRoot }));
		await firstHandler.ExecuteAsync(ContextFor(), CancellationToken.None);

		WriteDepotFile("PROD/COMP/SUPERVISOR/vm-service.ova", [9, 9, 9, 9, 9], out string secondSha);
		DepotArtifact secondArtifact = firstArtifact with { Sha256 = secondSha };
		FakeArtifactRepository secondArtifacts = new([secondArtifact]);
		FakeWriter secondWriter = new();
		SupervisorLibrarySyncJobHandler secondHandler = new(
			libraries, items, secondWriter, secondArtifacts,
			Options.Create(new ContentLibraryOptions { RootPath = _libraryRoot }),
			Options.Create(new CatalogOptions { DepotPath = _depotRoot }));
		JobExecutionOutcome outcome = await secondHandler.ExecuteAsync(ContextFor(), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Equal(1, secondWriter.WriteCount);
		Assert.Contains("1 updated", outcome.Note);
		ContentLibraryItemWrite written = Assert.Single(secondWriter.LastItems!);
		string copiedPath = Path.Combine(_libraryRoot, "supervisor", written.DirectoryName, "vm-service.ova");
		Assert.Equal([9, 9, 9, 9, 9], File.ReadAllBytes(copiedPath));
	}
}
