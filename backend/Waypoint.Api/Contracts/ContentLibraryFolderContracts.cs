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

using System.Text.Json.Serialization;
using Waypoint.Core.ContentLibraries;

namespace Waypoint.Api.Contracts;

/// <summary>One folder node in the <c>GET .../folders</c> tree response, nested via <see cref="Children"/> rather than carrying a flat <c>parent_folder_id</c> on the wire.</summary>
public sealed record ContentLibraryFolderNode(
	[property: JsonPropertyName("id")] Guid Id,
	[property: JsonPropertyName("name")] string Name,
	[property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
	[property: JsonPropertyName("item_ids")] IReadOnlyList<Guid> ItemIds,
	[property: JsonPropertyName("children")] IReadOnlyList<ContentLibraryFolderNode> Children)
{
	/// <summary>Assembles the flat, already-locked-and-read row set into a tree rooted at every folder with no parent.</summary>
	public static IReadOnlyList<ContentLibraryFolderNode> BuildTree(IReadOnlyList<ContentLibraryFolderWithItems> flat)
	{
		ArgumentNullException.ThrowIfNull(flat);

		ILookup<Guid?, ContentLibraryFolderWithItems> byParent = flat.ToLookup(f => f.Folder.ParentFolderId);
		return Build(null, byParent);
	}

	private static IReadOnlyList<ContentLibraryFolderNode> Build(Guid? parentFolderId, ILookup<Guid?, ContentLibraryFolderWithItems> byParent)
	{
		return [.. byParent[parentFolderId]
			.OrderBy(f => f.Folder.Name, StringComparer.Ordinal)
			.Select(f => new ContentLibraryFolderNode(
				f.Folder.Id, f.Folder.Name, f.Folder.CreatedAt, f.ItemIds, Build(f.Folder.Id, byParent)))];
	}
}

/// <summary>Request body for <c>POST .../folders</c>.</summary>
public sealed record ContentLibraryFolderCreateBody(
	[property: JsonPropertyName("name")] string? Name,
	[property: JsonPropertyName("parent_folder_id")] Guid? ParentFolderId);

/// <summary>
/// Request body for <c>PATCH .../folders/{id}</c> -- rename and/or move in one call.
/// Both fields are always applied (a full replace, not a partial patch): a caller
/// changing only the parent must still resend the folder's current name, and vice
/// versa. <c>parent_folder_id: null</c> moves the folder to the library root.
/// </summary>
public sealed record ContentLibraryFolderUpdateBody(
	[property: JsonPropertyName("name")] string? Name,
	[property: JsonPropertyName("parent_folder_id")] Guid? ParentFolderId);

/// <summary>Response body for a single created/updated folder (not the tree shape -- see <see cref="ContentLibraryFolderNode"/> for that).</summary>
public sealed record ContentLibraryFolderResponse(
	[property: JsonPropertyName("id")] Guid Id,
	[property: JsonPropertyName("library_id")] Guid LibraryId,
	[property: JsonPropertyName("parent_folder_id")] Guid? ParentFolderId,
	[property: JsonPropertyName("name")] string Name,
	[property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt)
{
	public static ContentLibraryFolderResponse FromDomain(ContentLibraryFolder folder)
	{
		ArgumentNullException.ThrowIfNull(folder);
		return new ContentLibraryFolderResponse(folder.Id, folder.LibraryId, folder.ParentFolderId, folder.Name, folder.CreatedAt);
	}
}

/// <summary>Request body for <c>PATCH .../items/{itemId}/folder</c>. <c>folder_id: null</c> unassigns the item back to the library root.</summary>
public sealed record ContentLibraryItemFolderAssignmentBody(
	[property: JsonPropertyName("folder_id")] Guid? FolderId);
