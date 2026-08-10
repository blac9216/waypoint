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
using Waypoint.Core.Secrets;

namespace Waypoint.Api.Middleware;

/// <summary>
/// Outermost pipeline middleware: catches every exception that escapes the rest of the
/// pipeline and turns it into the documented error envelope. A thrown
/// <see cref="ApiException"/> maps to its own status/code; a thrown
/// <see cref="MasterKeyUnavailableException"/> maps to the distinct
/// <c>master_key_unavailable</c> 503 (issue #409); anything else is logged and
/// reported as an opaque 500 (never leaking exception details to the client).
/// </summary>
public sealed partial class ErrorHandlingMiddleware
{
	/// <summary>
	/// Operator-actionable wire text for a missing/unreadable master key. Deliberately
	/// generic about *where* the key lives -- the mounted file path is server
	/// filesystem layout (docs/security.md control 1) and stays in the log-only
	/// exception message (<see cref="LogMasterKeyUnavailable"/>), never the response
	/// body. Points at deploy/README.md's "Generate a secrets master key" step instead
	/// of repeating its instructions here, so the two don't drift.
	/// </summary>
	internal const string MasterKeyUnavailableMessage =
		"The appliance secrets master key is not available. An administrator must mount the master key file and " +
		"restart the appliance -- see deploy/README.md, \"Generate a secrets master key\".";

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
		catch (MasterKeyUnavailableException masterKeyException)
		{
			// The detailed message (env var name, mounted path, format expectations) is
			// exactly what an operator needs -- but it is also server filesystem layout,
			// so it is logged here and only here. The wire body stays generic-actionable.
			LogMasterKeyUnavailable(masterKeyException, context.Request.Path);
			await ErrorEnvelopeWriter.WriteAsync(
				context,
				HttpStatusCode.ServiceUnavailable,
				new ErrorDetail("master_key_unavailable", MasterKeyUnavailableMessage));
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

	[LoggerMessage(Level = LogLevel.Error, Message = "Request to {Path} rejected: master key unavailable")]
	private partial void LogMasterKeyUnavailable(Exception exception, PathString path);

	[LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception processing {Path}")]
	private partial void LogUnhandledException(Exception exception, PathString path);
}
