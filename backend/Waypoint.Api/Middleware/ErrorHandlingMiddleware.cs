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
public sealed class ErrorHandlingMiddleware
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
			_logger.LogInformation(
				"Request to {Path} rejected: {Code} ({StatusCode})",
				context.Request.Path, apiException.Code, (int)apiException.StatusCode);
			await ErrorEnvelopeWriter.WriteAsync(context, apiException.StatusCode, apiException.ToErrorDetail());
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Unhandled exception processing {Path}", context.Request.Path);
			await ErrorEnvelopeWriter.WriteAsync(
				context,
				HttpStatusCode.InternalServerError,
				new ErrorDetail("internal_error", "An unexpected error occurred."));
		}
	}
}
