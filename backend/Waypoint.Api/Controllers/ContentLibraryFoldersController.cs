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

using System.Net;
using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.ContentLibraries;
using Waypoint.Core.Errors;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The DB-only virtual folder tree over one content library's items (migration 0113,
/// issue #1389, epic #1185). RBAC follows owner grill decision R2-10 (design record
/// #16), reconciled against this controller by issue #1034/PR #1747 and closed here by
/// issue #1746: R2-10 buckets "library upload/organize" as Operator-tier and
/// "deletes/purges" as its own Admin-tier bucket, so folder create/rename-move and
/// item-folder assignment (organize) are <c>[RequireOperatorRole]</c>, folder delete
/// (a destructive action, matching the deletes/purges bucket) stays
/// <c>[RequireAdminRole]</c>, and every read is Viewer+ -- NOT the uniform
/// Admin-write/Viewer-read shape <see cref="ContentLibrariesController"/> uses for the
/// library *registry* (registry create/delete is persistent configuration, outside
/// R2-10's "organize" wording, per issue #1746's own reconciliation note). Folder
/// structure and item-folder assignment are pure metadata; nothing here ever reads
/// or writes the library's on-disk directory, and item identity is accepted as
/// given -- there is no items table yet (#1396 is still queued), so <c>item_id</c> is
/// never validated against anything beyond being a syntactically valid Guid.
/// </summary>
[ApiController]
[Route("api/v1/content-libraries/{libraryId:guid}")]
public sealed class ContentLibraryFoldersController : ControllerBase
{
	private const int MaxNameLength = 128;

	private readonly IContentLibraryFolderRepository _folders;

	public ContentLibraryFoldersController(IContentLibraryFolderRepository folders)
	{
		ArgumentNullException.ThrowIfNull(folders);
		_folders = folders;
	}

	/// <summary>The library's whole folder tree, each node carrying the item Ids assigned directly to it (never a descendant's).</summary>
	[HttpGet("folders")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ContentLibraryFolderNode[]), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<IReadOnlyList<ContentLibraryFolderNode>>> GetTree(Guid libraryId, CancellationToken cancellationToken)
	{
		if (!await _folders.LibraryExistsAsync(libraryId, cancellationToken).ConfigureAwait(false))
		{
			throw LibraryNotFoundError(libraryId);
		}

		IReadOnlyList<ContentLibraryFolderWithItems> flat = await _folders.ListWithItemsAsync(libraryId, cancellationToken).ConfigureAwait(false);
		return Ok(ContentLibraryFolderNode.BuildTree(flat));
	}

	[HttpPost("folders")]
	[RequireOperatorRole]
	[ProducesResponseType(typeof(ContentLibraryFolderResponse), StatusCodes.Status201Created)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<ActionResult<ContentLibraryFolderResponse>> Create(
		Guid libraryId, [FromBody] ContentLibraryFolderCreateBody request, CancellationToken cancellationToken)
	{
		string name = ValidateName(request?.Name);

		(ContentLibraryFolderCreateOutcome outcome, ContentLibraryFolder? folder) =
			await _folders.CreateAsync(libraryId, request!.ParentFolderId, name, cancellationToken).ConfigureAwait(false);
		return outcome switch
		{
			ContentLibraryFolderCreateOutcome.Created => CreatedAtAction(
				nameof(GetTree), new { libraryId }, ContentLibraryFolderResponse.FromDomain(folder!)),
			ContentLibraryFolderCreateOutcome.LibraryNotFound => throw LibraryNotFoundError(libraryId),
			ContentLibraryFolderCreateOutcome.ParentNotFound => throw ApiException.Validation(
				"'parent_folder_id' does not name a folder in this library."),
			_ => throw new ApiException(
				HttpStatusCode.Conflict, "folder_name_taken", $"A sibling folder named '{name}' already exists."),
		};
	}

	/// <summary>Rename and/or move (issue #1389 AC): rejects a move that would place the folder under itself or one of its own descendants.</summary>
	[HttpPatch("folders/{folderId:guid}")]
	[RequireOperatorRole]
	[ProducesResponseType(typeof(ContentLibraryFolderResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<ActionResult<ContentLibraryFolderResponse>> Update(
		Guid libraryId, Guid folderId, [FromBody] ContentLibraryFolderUpdateBody request, CancellationToken cancellationToken)
	{
		string name = ValidateName(request?.Name);
		await EnsureFolderInLibraryAsync(libraryId, folderId, cancellationToken).ConfigureAwait(false);

		ContentLibraryFolderUpdateOutcome outcome =
			await _folders.UpdateAsync(folderId, name, request!.ParentFolderId, cancellationToken).ConfigureAwait(false);
		if (outcome == ContentLibraryFolderUpdateOutcome.Updated)
		{
			ContentLibraryFolder updated = (await _folders.GetAsync(folderId, cancellationToken).ConfigureAwait(false))!;
			return Ok(ContentLibraryFolderResponse.FromDomain(updated));
		}

		throw outcome switch
		{
			ContentLibraryFolderUpdateOutcome.NotFound => FolderNotFoundError(folderId),
			ContentLibraryFolderUpdateOutcome.ParentNotFound => ApiException.Validation(
				"'parent_folder_id' does not name a folder in this library."),
			ContentLibraryFolderUpdateOutcome.CycleRejected => ApiException.Validation(
				"A folder cannot be moved under itself or one of its own descendants."),
			_ => new ApiException(HttpStatusCode.Conflict, "folder_name_taken", $"A sibling folder named '{name}' already exists."),
		};
	}

	/// <summary>Delete-when-empty (issue #1389 AC): 409, not a silent cascade, when the folder still has a child folder or a directly-assigned item.</summary>
	[HttpDelete("folders/{folderId:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<IActionResult> Delete(Guid libraryId, Guid folderId, CancellationToken cancellationToken)
	{
		await EnsureFolderInLibraryAsync(libraryId, folderId, cancellationToken).ConfigureAwait(false);

		return await _folders.DeleteAsync(folderId, cancellationToken).ConfigureAwait(false) switch
		{
			ContentLibraryFolderDeleteOutcome.Deleted => NoContent(),
			ContentLibraryFolderDeleteOutcome.NotEmpty => throw new ApiException(
				HttpStatusCode.Conflict, "folder_not_empty",
				"This folder still has a child folder or an assigned item and cannot be deleted.",
				"Move or remove its contents first. This slice has no cascading delete."),
			_ => throw FolderNotFoundError(folderId),
		};
	}

	/// <summary>Assigns/moves/unassigns one item (<c>folder_id: null</c> returns it to the library root). Single-parent: an item has at most one folder.</summary>
	[HttpPatch("items/{itemId:guid}/folder")]
	[RequireOperatorRole]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<IActionResult> AssignItem(
		Guid libraryId, Guid itemId, [FromBody] ContentLibraryItemFolderAssignmentBody request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		return await _folders.AssignItemAsync(libraryId, itemId, request.FolderId, cancellationToken).ConfigureAwait(false) switch
		{
			ContentLibraryItemAssignmentOutcome.LibraryNotFound => throw LibraryNotFoundError(libraryId),
			ContentLibraryItemAssignmentOutcome.FolderNotFound => throw ApiException.Validation(
				"'folder_id' does not name a folder in this library."),
			_ => NoContent(),
		};
	}

	private async Task EnsureFolderInLibraryAsync(Guid libraryId, Guid folderId, CancellationToken cancellationToken)
	{
		ContentLibraryFolder? folder = await _folders.GetAsync(folderId, cancellationToken).ConfigureAwait(false);
		if (folder is null || folder.LibraryId != libraryId)
		{
			throw FolderNotFoundError(folderId);
		}
	}

	private static string ValidateName(string? rawName)
	{
		string? name = rawName?.Trim();
		if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
		{
			throw ApiException.Validation($"'name' is required and must be 1-{MaxNameLength} characters after trimming.");
		}

		return name;
	}

	private static ApiException LibraryNotFoundError(Guid libraryId) =>
		new(HttpStatusCode.NotFound, "not_found", $"No content library exists with id '{libraryId}'.");

	private static ApiException FolderNotFoundError(Guid folderId) =>
		new(HttpStatusCode.NotFound, "not_found", $"No folder exists with id '{folderId}' in this library.");
}
