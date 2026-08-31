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
/// Npgsql) that also owns provisioning/removing each library's own directory. The
/// REAL contract, after PR #1649 round 1 closed the create-race and atomicity gaps an
/// earlier draft of this comment claimed away: on create, the DB's UNIQUE constraint
/// on <c>name</c> is the sole serializer -- the row is inserted first, and only a
/// winning insert then provisions the directory, with a compensating row delete if
/// that provisioning then fails; on delete, the directory is removed before the row,
/// so a failed unlink leaves both intact rather than orphaning the row. Barring a
/// process crash inside that narrow window (row inserted but directory not yet
/// created on the create path; directory removed but row not yet deleted on the
/// delete path -- both a completed call's own compensation/ordering closes, but
/// neither survives the process dying mid-call), a row does not outlive its directory
/// or vice versa. The name itself is validated against path traversal at THIS layer
/// (<c>ContentLibraryRepository.ResolveDiskPath</c>), not only by the controller's
/// input regex one layer up, because this is the code that actually touches the
/// filesystem.
/// </summary>
public interface IContentLibraryRepository
{
	/// <summary>
	/// Creates one library: the DB's UNIQUE constraint on <c>name</c> is the sole
	/// serializer for concurrent creates, so the row is inserted FIRST -- only a
	/// winning insert then provisions the directory. If the name is already taken,
	/// nothing on disk is touched (a losing call never had a directory of its own to
	/// clean up, and cannot reach the winner's). If directory provisioning fails after
	/// a winning insert, the just-inserted row is removed again (compensating delete)
	/// so a failed create never leaves a row without a directory.
	/// </summary>
	Task<(ContentLibraryCreateOutcome Outcome, ContentLibrary? Library)> CreateAsync(string name, CancellationToken cancellationToken);

	Task<ContentLibrary?> GetAsync(Guid id, CancellationToken cancellationToken);

	Task<IReadOnlyList<ContentLibrary>> ListAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Deletes a library only when its directory is empty (issue #1391 AC): checked
	/// against the real filesystem, not a DB-tracked item count -- this slice has no
	/// items table yet (that is #1396's). The directory is removed BEFORE the row: a
	/// failed unlink (a file that appeared after the emptiness check, a denied
	/// delete, ...) then leaves the row and directory both intact -- recoverable --
	/// rather than deleting the row and leaving an orphaned directory behind.
	/// </summary>
	Task<ContentLibraryDeleteOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
