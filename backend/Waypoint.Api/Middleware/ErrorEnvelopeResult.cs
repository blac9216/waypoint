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

using Microsoft.AspNetCore.Mvc;
using Waypoint.Core.Errors;

namespace Waypoint.Api.Middleware;

/// <summary>
/// An MVC <see cref="IActionResult"/> that writes the documented error envelope through
/// <see cref="ErrorEnvelopeWriter"/>. It exists so error responses produced *inside* the
/// MVC pipeline — today the <c>[ApiController]</c> automatic model-state 400 wired in
/// <c>Program.cs</c> — are byte-identical to the ones the exception middleware and the
/// status-code-pages handler produce, rather than MVC's default RFC 7807 camelCase
/// <c>ProblemDetails</c>.
/// </summary>
/// <param name="Exception">The error to render (status code, code, message and optional detail).</param>
public sealed record ErrorEnvelopeResult(ApiException Exception) : IActionResult
{
	public Task ExecuteResultAsync(ActionContext context)
	{
		return ErrorEnvelopeWriter.WriteAsync(
			context.HttpContext,
			Exception.StatusCode,
			Exception.ToErrorDetail());
	}
}
