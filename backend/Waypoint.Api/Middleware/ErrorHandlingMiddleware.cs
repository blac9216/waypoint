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
using Microsoft.Extensions.Logging;
using Waypoint.Core.Errors;

namespace Waypoint.Api.Middleware;

/// <summary>
/// Outermost pipeline middleware: catches every exception that escapes the rest of the
/// pipeline and turns it into the documented error envelope. A thrown
/// <see cref="ApiException"/> maps to its own status/code; anything else is logged and
/// reported as an opaque 500 (never leaking exception details to the client).
/// </summary>
public sealed partial class ErrorHandlingMiddleware
{
	private readonly RequestDelegate _next;
	private readonly ILogger<ErrorHandlingMiddleware> _logger;

	public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
	{
		_next = next;
		_logger = logger;
	}

	public async Task InvokeAsync(HttpContext context)
	{
		try
		{
			await _next(context);
		}
		catch (ApiException apiException)
		{
			LogApiExceptionRejected(context.Request.Path, apiException.Code, (int)apiException.StatusCode);
			await ErrorEnvelopeWriter.WriteAsync(context, apiException.StatusCode, apiException.ToErrorDetail());
		}
		catch (Exception exception)
		{
			LogUnhandledException(exception, context.Request.Path);
			await ErrorEnvelopeWriter.WriteAsync(
				context,
				HttpStatusCode.InternalServerError,
				new ErrorDetail("internal_error", "An unexpected error occurred."));
		}
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Request to {Path} rejected: {Code} ({StatusCode})")]
	private partial void LogApiExceptionRejected(PathString path, string code, int statusCode);

	[LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception processing {Path}")]
	private partial void LogUnhandledException(Exception exception, PathString path);
}
