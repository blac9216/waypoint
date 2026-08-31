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
using Npgsql;
using Waypoint.Core.ContentLibraries;
using Waypoint.Infrastructure.ContentLibraries;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1391 (migration 0090, epic #1185, design record #16 section 6): proves the
/// registry's DB row and its directory are created/removed together against real
/// Postgres AND a real temp filesystem root -- the "flat on disk, one directory per
/// library" invariant and the delete-when-empty check are both filesystem facts a fake
/// cannot stand in for.
/// </summary>
[Collection("Postgres")]
public sealed class ContentLibraryRepositoryTests : IAsyncLifetime, IDisposable
{
	private readonly PostgresFixture _fixture;
	private readonly string _rootPath = Directory.CreateTempSubdirectory("wp-content-library-test").FullName;
	private ContentLibraryRepository _libraries = null!;

	public ContentLibraryRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();
		_libraries = new ContentLibraryRepository(_fixture.ConnectionString, _rootPath);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	public void Dispose()
	{
		try
		{
			Directory.Delete(_rootPath, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("TRUNCATE TABLE content_libraries RESTART IDENTITY CASCADE", connection);
		await command.ExecuteNonQueryAsync();
	}

	[Fact]
	public async Task CreateAsync_persists_the_row_and_provisions_the_directory()
	{
		(ContentLibraryCreateOutcome outcome, ContentLibrary? library) = await _libraries.CreateAsync("vcsp-01", CancellationToken.None);

		Assert.Equal(ContentLibraryCreateOutcome.Created, outcome);
		Assert.NotNull(library);
		Assert.Equal("vcsp-01", library!.Name);
		Assert.Equal(Path.Combine(_rootPath, "vcsp-01"), library.DiskPath);
		Assert.True(Directory.Exists(library.DiskPath));

		ContentLibrary? fetched = await _libraries.GetAsync(library.Id, CancellationToken.None);
		Assert.NotNull(fetched);
		Assert.Equal(library.DiskPath, fetched!.DiskPath);
	}

	[Fact]
	public async Task CreateAsync_rejects_a_duplicate_name_and_leaves_no_orphaned_directory()
	{
		(ContentLibraryCreateOutcome firstOutcome, ContentLibrary? first) = await _libraries.CreateAsync("vcsp-dup", CancellationToken.None);
		Assert.Equal(ContentLibraryCreateOutcome.Created, firstOutcome);

		(ContentLibraryCreateOutcome secondOutcome, ContentLibrary? second) = await _libraries.CreateAsync("vcsp-dup", CancellationToken.None);

		Assert.Equal(ContentLibraryCreateOutcome.NameTaken, secondOutcome);
		Assert.Null(second);
		// The original library's directory must survive a colliding create attempt.
		Assert.True(Directory.Exists(first!.DiskPath));
	}

	[Fact]
	public async Task ListAsync_returns_multiple_libraries_as_independent_rows()
	{
		await _libraries.CreateAsync("vcsp-a", CancellationToken.None);
		await _libraries.CreateAsync("vcsp-b", CancellationToken.None);

		IReadOnlyList<ContentLibrary> all = await _libraries.ListAsync(CancellationToken.None);

		Assert.Equal(2, all.Count);
		Assert.Contains(all, l => l.Name == "vcsp-a");
		Assert.Contains(all, l => l.Name == "vcsp-b");
		Assert.NotEqual(
			all.Single(l => l.Name == "vcsp-a").DiskPath,
			all.Single(l => l.Name == "vcsp-b").DiskPath);
	}

	[Fact]
	public async Task DeleteAsync_returns_NotFound_for_an_unknown_id()
	{
		ContentLibraryDeleteOutcome outcome = await _libraries.DeleteAsync(Guid.NewGuid(), CancellationToken.None);
		Assert.Equal(ContentLibraryDeleteOutcome.NotFound, outcome);
	}

	[Fact]
	public async Task DeleteAsync_deletes_an_empty_library_and_removes_its_directory()
	{
		(_, ContentLibrary? library) = await _libraries.CreateAsync("vcsp-empty", CancellationToken.None);

		ContentLibraryDeleteOutcome outcome = await _libraries.DeleteAsync(library!.Id, CancellationToken.None);

		Assert.Equal(ContentLibraryDeleteOutcome.Deleted, outcome);
		Assert.Null(await _libraries.GetAsync(library.Id, CancellationToken.None));
		Assert.False(Directory.Exists(library.DiskPath));
	}

	[Fact]
	public async Task DeleteAsync_rejects_a_non_empty_library_without_touching_the_row_or_directory()
	{
		(_, ContentLibrary? library) = await _libraries.CreateAsync("vcsp-nonempty", CancellationToken.None);
		File.WriteAllText(Path.Combine(library!.DiskPath, "placeholder.txt"), "content");

		ContentLibraryDeleteOutcome outcome = await _libraries.DeleteAsync(library.Id, CancellationToken.None);

		Assert.Equal(ContentLibraryDeleteOutcome.NotEmpty, outcome);
		Assert.NotNull(await _libraries.GetAsync(library.Id, CancellationToken.None));
		Assert.True(Directory.Exists(library.DiskPath));
	}

	/// <summary>
	/// PR #1649 round 1 (S1): the create-race fix makes the DB's UNIQUE constraint on
	/// <c>name</c> the sole serializer, with <c>Directory.CreateDirectory</c> gated on a
	/// winning insert -- so no caller ever samples <c>Directory.Exists</c> before the
	/// insert resolves, and there is no interleaving left in which a losing caller can
	/// delete a winner's live directory. Two real connections (one per concurrent
	/// <see cref="ContentLibraryRepository.CreateAsync"/> call), asserted
	/// deterministically: whichever interleaving the scheduler picks, exactly one call
	/// must win, exactly one must lose, and the winner's directory must still be there
	/// afterwards -- this holds every run, not just a lucky one.
	/// </summary>
	[Fact]
	public async Task CreateAsync_TwoConcurrentCallsForTheSameName_ExactlyOneWinsAndTheWinnersDirectorySurvives()
	{
		const string name = "vcsp-race";

		(ContentLibraryCreateOutcome Outcome, ContentLibrary? Library)[] results = await Task.WhenAll(
			_libraries.CreateAsync(name, CancellationToken.None),
			_libraries.CreateAsync(name, CancellationToken.None));

		Assert.Equal(1, results.Count(r => r.Outcome == ContentLibraryCreateOutcome.Created));
		Assert.Equal(1, results.Count(r => r.Outcome == ContentLibraryCreateOutcome.NameTaken));

		ContentLibrary winner = results.Single(r => r.Outcome == ContentLibraryCreateOutcome.Created).Library!;
		Assert.True(Directory.Exists(winner.DiskPath));
		Assert.Single(Directory.EnumerateDirectories(_rootPath));

		IReadOnlyList<ContentLibrary> all = await _libraries.ListAsync(CancellationToken.None);
		Assert.Single(all, l => l.Name == name);
	}

	/// <summary>PR #1649 round 1 (S2): the same forms the reviewer ran <c>Path.Combine</c> against.</summary>
	[Theory]
	[InlineData("../../../etc/waypoint")]
	[InlineData("../x")]
	[InlineData("/etc/waypoint")]
	[InlineData("/etc/x")]
	[InlineData("a/b")]
	[InlineData("")]
	[InlineData("..")]
	[InlineData(".")]
	public async Task CreateAsync_rejects_path_traversal_and_invalid_name_forms_without_touching_the_filesystem(string name)
	{
		await Assert.ThrowsAsync<ArgumentException>(() => _libraries.CreateAsync(name, CancellationToken.None));
		Assert.Empty(Directory.EnumerateFileSystemEntries(_rootPath));
	}

	/// <summary>
	/// PR #1649 round 1 (S3a): if the row insert wins but provisioning the directory
	/// then fails, the compensating delete must remove the row again rather than
	/// leaving the documented-impossible row-without-a-directory state.
	/// </summary>
	[Fact]
	public async Task CreateAsync_WhenDirectoryProvisioningFails_RemovesTheInsertedRowAgain()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		UnixFileMode originalRootMode = File.GetUnixFileMode(_rootPath);
		File.SetUnixFileMode(_rootPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
		try
		{
			await Assert.ThrowsAsync<UnauthorizedAccessException>(
				() => _libraries.CreateAsync("vcsp-unwritable", CancellationToken.None));
		}
		finally
		{
			File.SetUnixFileMode(_rootPath, originalRootMode);
		}

		IReadOnlyList<ContentLibrary> all = await _libraries.ListAsync(CancellationToken.None);
		Assert.DoesNotContain(all, l => l.Name == "vcsp-unwritable");
	}

	/// <summary>
	/// PR #1649 round 1 (S3b): the directory is removed BEFORE the row, so a failed
	/// unlink must leave both intact -- never the row gone with the directory behind.
	/// Removing write permission on the ROOT (not the library's own directory) denies
	/// the unlink: on Linux, removing a directory entry needs write permission on its
	/// PARENT, not the entry itself.
	/// </summary>
	[Fact]
	public async Task DeleteAsync_WhenTheDirectoryUnlinkFails_LeavesBothTheRowAndDirectoryIntact()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		(_, ContentLibrary? library) = await _libraries.CreateAsync("vcsp-lockeddelete", CancellationToken.None);

		UnixFileMode originalRootMode = File.GetUnixFileMode(_rootPath);
		File.SetUnixFileMode(_rootPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
		try
		{
			// .NET surfaces a denied directory removal on Linux as IOException (via
			// Directory.Delete -> RemoveEmptyDirectory), not UnauthorizedAccessException
			// -- unlike File.* APIs, which do throw UnauthorizedAccessException for the
			// analogous EACCES.
			await Assert.ThrowsAsync<IOException>(
				() => _libraries.DeleteAsync(library!.Id, CancellationToken.None));
		}
		finally
		{
			File.SetUnixFileMode(_rootPath, originalRootMode);
		}

		Assert.NotNull(await _libraries.GetAsync(library!.Id, CancellationToken.None));
		Assert.True(Directory.Exists(library.DiskPath));
	}
}
