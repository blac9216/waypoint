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

namespace Waypoint.Core.Errors;

/// <summary>
/// The <c>error</c> object of the documented envelope
/// (<c>docs/api-contract.md</c> Conventions): <c>{ "error": { code, message, detail?,
/// blockers? } }</c>. Serialized snake_case by the API's global JSON options.
/// </summary>
/// <param name="Code">Stable, machine-readable error code (e.g. <c>mode_unavailable</c>).</param>
/// <param name="Message">Human-readable summary, safe to show a user.</param>
/// <param name="Detail">Optional extra context; omitted from the payload when null.</param>
/// <param name="Blockers">
/// Issue #593: an optional machine-readable breakdown for a <c>409</c> whose cause is
/// more than one enumerable category (e.g. <c>credential_in_use</c> naming targets,
/// schedules, AND active jobs at once) -- omitted for every other error, and for a
/// <c>409</c> whose cause is a single indivisible fact (e.g. <c>name_taken</c>) that a
/// category/count breakdown would not clarify. <see cref="Message"/> stays the
/// human-readable summary; this is the structured form a caller can branch on without
/// parsing prose.
/// </param>
public sealed record ErrorDetail(string Code, string Message, string? Detail = null, IReadOnlyList<BlockingCategory>? Blockers = null);

/// <summary>
/// One machine-readable reason a request is blocked, plus how many rows are
/// responsible -- e.g. <c>{ category: "targets", count: 2 }</c>. <see cref="Category"/>
/// values are a closed, per-endpoint set (see the endpoint's own doc comment for its
/// list); never free text.
/// </summary>
/// <param name="Category">Stable, machine-readable category name.</param>
/// <param name="Count">Number of blocking rows in this category (always &gt;= 1).</param>
public sealed record BlockingCategory(string Category, int Count);

/// <summary>The envelope itself — the sole top-level shape for every error response.</summary>
public sealed record ErrorResponse(ErrorDetail Error);
