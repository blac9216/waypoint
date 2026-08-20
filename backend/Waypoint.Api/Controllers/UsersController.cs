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

using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.Errors;
using Waypoint.Core.Users;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md `/users` surface (issue #512, epic #14): Admin-only GET/POST/PUT
/// over the local <c>users</c> mirror table.
///
/// Design interpretation (per #512's brief -- read alongside a comment posted on the
/// issue): ADR-0004 makes Keycloak group membership authoritative for role, and this
/// backend is a plain OIDC relying party that never calls the Keycloak admin API
/// (CLAUDE.md "Do NOT invent Keycloak admin-API calls"). So <c>role</c> here is a
/// **read-only mirror**, refreshed every time the subject authenticates
/// (<c>UserUpsertMiddleware</c>) -- never something this controller's PUT can change.
/// <see cref="Update"/> only ever writes <see cref="UserUpdateRequest.SiteScope"/>, the
/// one app-local field with no IdP equivalent. An operator who wants to change a
/// caller's role edits their Keycloak group membership; it takes effect on that
/// caller's next token evaluation, satisfying #512's "role edits Admin-only and take
/// effect on next token evaluation" acceptance criterion via the IdP rather than via
/// this table (an app-local role column here would silently drift from the IdP's own
/// answer, which is worse than not offering the edit at all).
/// </summary>
[ApiController]
[Route("api/v1/users")]
public sealed class UsersController : ControllerBase
{
	private readonly IUserDirectory _users;

	public UsersController(IUserDirectory users)
	{
		ArgumentNullException.ThrowIfNull(users);
		_users = users;
	}

	[HttpGet]
	[RequireAdminRole]
	[ProducesResponseType(typeof(UserResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<UserResponse>>> List(CancellationToken cancellationToken)
	{
		IReadOnlyList<UserRecord> users = await _users.ListAsync(cancellationToken).ConfigureAwait(false);
		return Ok(users.Select(MapUser).ToArray());
	}

	[HttpGet("{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<UserResponse>> Get(Guid id, CancellationToken cancellationToken)
	{
		UserRecord? user = await _users.GetAsync(id, cancellationToken).ConfigureAwait(false);
		return user is null ? throw NotFoundError(id) : Ok(MapUser(user));
	}

	/// <summary>
	/// Pre-provisions a row for a subject that has not yet authenticated (see
	/// <see cref="IUserDirectory.CreateAsync"/>'s doc comment) -- e.g. setting
	/// <c>site_scope</c> ahead of a new hire's first login. <c>role</c> in the request
	/// body only seeds the mirror; it is overwritten the moment that subject actually
	/// authenticates.
	/// </summary>
	[HttpPost]
	[RequireAdminRole]
	[ProducesResponseType(typeof(UserResponse), StatusCodes.Status201Created)]
	public async Task<ActionResult<UserResponse>> Create([FromBody] UserCreateRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (string.IsNullOrWhiteSpace(request.OidcSub) || string.IsNullOrWhiteSpace(request.Username))
		{
			throw ApiException.Validation(
				"oidc_sub and username are required.",
				"Set non-empty \"oidc_sub\" and \"username\" in the request body.");
		}

		if (!Enum.TryParse(request.Role, ignoreCase: true, out WaypointRole role))
		{
			throw ApiException.Validation(
				"role is not a recognized Waypoint role.",
				$"\"role\" must be one of: {string.Join(", ", Enum.GetNames<WaypointRole>())}.");
		}

		string siteScopeJson = string.IsNullOrWhiteSpace(request.SiteScope) ? "[]" : request.SiteScope;
		ValidateSiteScopeJson(siteScopeJson);

		Guid? id = await _users.CreateAsync(
			request.OidcSub, request.Username, role, siteScopeJson, "oidc", cancellationToken).ConfigureAwait(false);

		if (id is not Guid createdId)
		{
			throw new ApiException(HttpStatusCode.Conflict, "oidc_sub_taken", $"A user with oidc_sub '{request.OidcSub}' already exists.");
		}

		UserRecord created = (await _users.GetAsync(createdId, cancellationToken).ConfigureAwait(false))!;
		return CreatedAtAction(nameof(Get), new { id = createdId }, MapUser(created));
	}

	/// <summary>
	/// Edits <see cref="UserUpdateRequest.SiteScope"/> -- the only field this endpoint
	/// accepts. See this controller's class-level doc comment for why role is not
	/// editable here.
	/// </summary>
	[HttpPut("{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<UserResponse>> Update(Guid id, [FromBody] UserUpdateRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (request.SiteScope is null)
		{
			throw ApiException.Validation("site_scope is required.", "Set \"site_scope\" (a JSON array) in the request body.");
		}

		ValidateSiteScopeJson(request.SiteScope);

		bool updated = await _users.UpdateSiteScopeAsync(id, request.SiteScope, cancellationToken).ConfigureAwait(false);
		if (!updated)
		{
			throw NotFoundError(id);
		}

		UserRecord user = (await _users.GetAsync(id, cancellationToken).ConfigureAwait(false))!;
		return Ok(MapUser(user));
	}

	private static UserResponse MapUser(UserRecord user)
	{
		return new UserResponse(
			Id: user.Id.ToString(),
			OidcSub: user.OidcSub,
			Username: user.Username,
			Role: user.Role.ToString(),
			SiteScope: user.SiteScopeJson,
			AuthMethod: user.AuthMethod,
			LastSeenAt: user.LastSeenAt.ToString("O", CultureInfo.InvariantCulture),
			CreatedAt: user.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
			UpdatedAt: user.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
	}

	private static ApiException NotFoundError(Guid id) =>
		new(HttpStatusCode.NotFound, "not_found", $"No user exists with id '{id}'.");

	/// <summary>
	/// Guards the <c>$N::jsonb</c> cast in <c>UserRepository.CreateAsync</c> /
	/// <c>UpdateSiteScopeAsync</c> (issue #530): malformed JSON reaching that cast makes
	/// Postgres raise <c>invalid_text_representation</c>, which
	/// <c>Waypoint.Api.Middleware.ErrorHandlingMiddleware</c> shapes as an unmapped 500.
	/// Parsing here, before the repository call, turns that into the documented 400
	/// <c>validation_error</c> -- the same "parse in the controller, map to
	/// ApiException.Validation" shape <c>Waypoint.Core.Jobs.ScanScopeParser</c> uses for
	/// run scope. site_scope is documented as a JSON array, so a well-formed but
	/// non-array value (e.g. an object or a bare string) is rejected too.
	/// </summary>
	private static void ValidateSiteScopeJson(string siteScopeJson)
	{
		JsonElement root;
		try
		{
			root = JsonSerializer.Deserialize<JsonElement>(siteScopeJson);
		}
		catch (JsonException)
		{
			throw ApiException.Validation(
				"site_scope is not valid JSON.",
				"\"site_scope\" must be a JSON array (as a string), e.g. \"[\\\"site-a\\\"]\".");
		}

		if (root.ValueKind != JsonValueKind.Array)
		{
			throw ApiException.Validation(
				"site_scope must be a JSON array.",
				"\"site_scope\" must be a JSON array (as a string), e.g. \"[\\\"site-a\\\"]\".");
		}
	}
}
