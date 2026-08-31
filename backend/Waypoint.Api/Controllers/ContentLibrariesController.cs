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
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.ContentLibraries;
using Waypoint.Core.Errors;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The content-library registry surface (issue #1391, epic #1185, design record #16
/// section 6): Admin-only create/delete, Viewer+ read -- matching the
/// Sites/Trust/Credentials controllers' RBAC shape for every other Configuration-like
/// resource in this codebase. Deliberately NOT this slice (its own "Risks" section,
/// stated here so a reviewer does not read the absence as a gap): no VCSP
/// <c>lib.json</c>/<c>items.json</c> file writing (#1393), no item CRUD (#1396), no
/// repair/prune (#1398), no depot-fed sync wiring (#1057) -- this controller only lets
/// an operator create, list, and delete-when-empty the library rows and directories
/// those pieces will write into.
/// </summary>
[ApiController]
[Route("api/v1/content-libraries")]
public sealed class ContentLibrariesController : ControllerBase
{
	/// <summary>
	/// A library name doubles as its directory's leaf name (see
	/// <see cref="ContentLibraryOptions"/>), so it is restricted to a single safe path
	/// segment: no <c>/</c>, no leading <c>.</c> (rules out both a hidden directory and
	/// <c>.</c>/<c>..</c>), letters/digits/underscore/hyphen only. This is stricter than
	/// a general display name needs to be, deliberately -- it is the operator-facing
	/// 400 for the same input a name-traversal attempt would otherwise reach; the
	/// repository layer that actually touches the filesystem
	/// (<c>ContentLibraryRepository.ResolveDiskPath</c>) enforces the same
	/// single-segment invariant independently rather than trusting this regex alone.
	/// </summary>
	/// <remarks>
	/// <c>internal</c> (not <c>private</c>) so <c>ContentLibrariesControllerNamePatternTests</c>
	/// in <c>Waypoint.Tests</c> can exercise this pattern directly rather than only
	/// indirectly through an HTTP-shaped test, matching this controller's existing
	/// no-per-endpoint-HTTP-test precedent (see class remarks).
	/// </remarks>
	internal static readonly Regex NamePattern = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,62}$", RegexOptions.Compiled);

	private readonly IContentLibraryRepository _libraries;

	public ContentLibrariesController(IContentLibraryRepository libraries)
	{
		ArgumentNullException.ThrowIfNull(libraries);
		_libraries = libraries;
	}

	[HttpGet]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ContentLibraryResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<ContentLibraryResponse>>> List(CancellationToken cancellationToken)
	{
		IReadOnlyList<ContentLibrary> libraries = await _libraries.ListAsync(cancellationToken).ConfigureAwait(false);
		return Ok(libraries.Select(ContentLibraryResponse.FromDomain).ToArray());
	}

	[HttpGet("{id:guid}")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ContentLibraryResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<ContentLibraryResponse>> Get(Guid id, CancellationToken cancellationToken)
	{
		ContentLibrary? library = await _libraries.GetAsync(id, cancellationToken).ConfigureAwait(false);
		return library is null ? throw NotFoundError(id) : Ok(ContentLibraryResponse.FromDomain(library));
	}

	[HttpPost]
	[RequireAdminRole]
	[ProducesResponseType(typeof(ContentLibraryResponse), StatusCodes.Status201Created)]
	[ProducesResponseType(StatusCodes.Status400BadRequest)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<ActionResult<ContentLibraryResponse>> Create([FromBody] ContentLibraryCreateBody request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		string? name = request.Name?.Trim();
		if (string.IsNullOrEmpty(name) || !NamePattern.IsMatch(name))
		{
			throw ApiException.Validation(
				"'name' is required and must be 1-63 characters of letters, digits, '_', or '-', starting with a letter or digit.");
		}

		(ContentLibraryCreateOutcome outcome, ContentLibrary? library) = await _libraries.CreateAsync(name, cancellationToken).ConfigureAwait(false);
		if (outcome == ContentLibraryCreateOutcome.NameTaken)
		{
			throw new ApiException(HttpStatusCode.Conflict, "name_taken", $"A content library named '{name}' already exists.");
		}

		return CreatedAtAction(nameof(Get), new { id = library!.Id }, ContentLibraryResponse.FromDomain(library));
	}

	/// <summary>Delete-when-empty (issue #1391 AC): 409, not a silent cascade, when the library's directory still has any entry.</summary>
	[HttpDelete("{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
	{
		return await _libraries.DeleteAsync(id, cancellationToken).ConfigureAwait(false) switch
		{
			ContentLibraryDeleteOutcome.Deleted => NoContent(),
			ContentLibraryDeleteOutcome.NotEmpty => throw new ApiException(
				HttpStatusCode.Conflict, "content_library_not_empty",
				"This content library's directory still has content and cannot be deleted.",
				"Remove its contents first. This slice has no cascading item delete."),
			_ => throw NotFoundError(id),
		};
	}

	private static ApiException NotFoundError(Guid id) =>
		new(HttpStatusCode.NotFound, "not_found", $"No content library exists with id '{id}'.");
}
