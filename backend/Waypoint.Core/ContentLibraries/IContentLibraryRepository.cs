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

namespace Waypoint.Core.ContentLibraries;

/// <summary>Outcome of <see cref="IContentLibraryRepository.CreateAsync"/>.</summary>
public enum ContentLibraryCreateOutcome
{
	Created,

	/// <summary>Another library already carries this name (the DB's own unique constraint on <c>name</c>).</summary>
	NameTaken,
}

/// <summary>Outcome of <see cref="IContentLibraryRepository.DeleteAsync"/>.</summary>
public enum ContentLibraryDeleteOutcome
{
	Deleted,
	NotFound,

	/// <summary>The library's directory still has at least one entry -- this slice never cascades an item deletion (issue #1391 AC "deleting a non-empty library is rejected, not silently emptied").</summary>
	NotEmpty,
}

/// <summary>
/// Storage for the content-library registry (migration 0090, issue #1391, epic #1185,
/// design record #16 section 6): the minimal CRUD surface every later VCSP-writer
/// piece (#1393, #1396, #1398) resolves "which library, which path" through before it
/// writes anything. One implementation
/// (<c>Waypoint.Infrastructure.ContentLibraries.ContentLibraryRepository</c>, plain
/// Npgsql) that also owns provisioning/removing each library's own directory --
/// the DB row and the directory are created and deleted together, never one without
/// the other, so a row never outlives its directory or vice versa.
/// </summary>
public interface IContentLibraryRepository
{
	/// <summary>
	/// Creates one library: provisions its directory under
	/// <see cref="ContentLibraryOptions.RootPath"/> first, then inserts the row. If the
	/// name is already taken, the just-created directory is removed again (best
	/// effort) so a failed create never leaves an orphaned empty directory behind.
	/// </summary>
	Task<(ContentLibraryCreateOutcome Outcome, ContentLibrary? Library)> CreateAsync(string name, CancellationToken cancellationToken);

	Task<ContentLibrary?> GetAsync(Guid id, CancellationToken cancellationToken);

	Task<IReadOnlyList<ContentLibrary>> ListAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Deletes a library only when its directory is empty (issue #1391 AC): checked
	/// against the real filesystem, not a DB-tracked item count -- this slice has no
	/// items table yet (that is #1396's). The directory itself is removed together
	/// with the row, in that order, so a delete never leaves the row gone but the
	/// (now provably empty) directory behind.
	/// </summary>
	Task<ContentLibraryDeleteOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
