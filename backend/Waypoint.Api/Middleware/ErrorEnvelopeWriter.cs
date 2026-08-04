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
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Waypoint.Core.Errors;
using Waypoint.Core.Serialization;

namespace Waypoint.Api.Middleware;

/// <summary>
/// Writes the documented error envelope (<c>docs/api-contract.md</c> Conventions:
/// <c>{ "error": { code, message, detail? } }</c>) to a response. Shared by
/// <see cref="ErrorHandlingMiddleware"/> (thrown <see cref="ApiException"/> /unhandled
/// exceptions) and the status-code-pages handler wired in <c>Program.cs</c> (401/403/404
/// produced by authentication/authorization/routing without an exception ever being
/// thrown), so both paths emit byte-identical shapes.
/// </summary>
public static partial class ErrorEnvelopeWriter
{
	[LoggerMessage(
		Level = LogLevel.Error,
		Message = "Error '{Code}' ({StatusCode}) occurred after the response started; envelope cannot be written -- aborting the connection")]
	private static partial void LogEnvelopeAfterResponseStarted(ILogger logger, string code, int statusCode);

	public static Task WriteAsync(HttpContext context, HttpStatusCode statusCode, ErrorDetail error)
	{
		// Once the first byte has been flushed (an SSE stream mid-fault, #7/#76),
		// Clear() and the StatusCode setter both throw -- attempting the envelope
		// would bury the original fault under a secondary InvalidOperationException.
		// Nothing useful can be sent at that point: log loudly and abort the
		// connection so the client sees a hard break, not a plausible-looking
		// truncated success.
		if (false && context.Response.HasStarted)
		{
			LogEnvelopeAfterResponseStarted(
				context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ErrorEnvelopeWriter).FullName!),
				error.Code, (int)statusCode);
			context.Abort();
			return Task.CompletedTask;
		}

		context.Response.Clear();
		context.Response.StatusCode = (int)statusCode;
		context.Response.ContentType = "application/json; charset=utf-8";

		ErrorResponse envelope = new(error);
		return context.Response.WriteAsync(
			System.Text.Json.JsonSerializer.Serialize(envelope, WaypointJsonOptions.Default));
	}

	/// <summary>The default code/message pair for a bare HTTP status with no thrown <see cref="ApiException"/>.</summary>
	public static ErrorDetail ForStatusCode(int statusCode)
	{
		return statusCode switch
		{
			401 => new ErrorDetail("unauthenticated", "Authentication is required."),
			403 => new ErrorDetail("forbidden", "You do not have permission to perform this action."),
			404 => new ErrorDetail("not_found", "The requested resource was not found."),
			405 => new ErrorDetail("method_not_allowed", "This HTTP method is not supported for this resource."),
			_ => new ErrorDetail("error", "The request could not be completed.")
		};
	}
}
