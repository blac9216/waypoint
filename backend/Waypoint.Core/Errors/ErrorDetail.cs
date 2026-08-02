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
/// (<c>docs/api-contract.md</c> Conventions): <c>{ "error": { code, message, detail? } }</c>.
/// Serialized snake_case by the API's global JSON options.
/// </summary>
/// <param name="Code">Stable, machine-readable error code (e.g. <c>mode_unavailable</c>).</param>
/// <param name="Message">Human-readable summary, safe to show a user.</param>
/// <param name="Detail">Optional extra context; omitted from the payload when null.</param>
public sealed record ErrorDetail(string Code, string Message, string? Detail = null);

/// <summary>The envelope itself — the sole top-level shape for every error response.</summary>
public sealed record ErrorResponse(ErrorDetail Error);
