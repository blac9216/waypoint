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

namespace Waypoint.Core.Errors;

/// <summary>
/// Thrown by application code to produce a specific documented error response. The API
/// layer's error middleware catches this type and serializes <see cref="ErrorResponse"/>
/// with <see cref="StatusCode"/>; anything else is an unmapped 500.
/// </summary>
public class ApiException : Exception
{
	public ApiException(HttpStatusCode statusCode, string code, string message, string? detail = null)
		: base(message)
	{
		StatusCode = statusCode;
		Code = code;
		Detail = detail;
	}

	public HttpStatusCode StatusCode { get; }

	public string Code { get; }

	public string? Detail { get; }

	public ErrorDetail ToErrorDetail()
	{
		return new ErrorDetail(Code, Message, Detail);
	}

	public static ApiException Unauthorized(string message = "Authentication is required.", string? detail = null)
	{
		return new ApiException(HttpStatusCode.Unauthorized, "unauthenticated", message, detail);
	}

	public static ApiException Forbidden(string message = "You do not have permission to perform this action.", string? detail = null)
	{
		return new ApiException(HttpStatusCode.Forbidden, "forbidden", message, detail);
	}

	public static ApiException NotFound(string message = "The requested resource was not found.", string? detail = null)
	{
		return new ApiException(HttpStatusCode.NotFound, "not_found", message, detail);
	}

	public static ApiException Validation(string message, string? detail = null)
	{
		return new ApiException(HttpStatusCode.BadRequest, "validation_error", message, detail);
	}

	/// <summary>
	/// The endpoint exists but cannot function in the appliance's current mode
	/// (<c>docs/api-contract.md</c>: "every response is mode-aware"). Reserved for
	/// mode-gated endpoints landing in later milestones (ADR-0010) — no caller in this
	/// scaffold raises it yet.
	/// </summary>
	public static ApiException ModeUnavailable(string message = "This operation is unavailable in the appliance's current mode.", string? detail = null)
	{
		return new ApiException(HttpStatusCode.Conflict, "mode_unavailable", message, detail);
	}

	/// <summary>
	/// The endpoint cannot complete because something the deployment is responsible for
	/// provisioning (a mounted volume, a writable staging directory) is missing or
	/// misconfigured on this host -- distinct from <see cref="ModeUnavailable"/> (a
	/// connected/disconnected-mode gate) and from an unmapped 500 (an actual bug). Issue
	/// #621: <c>ManagedToolController.Upload</c> used to let a staging-directory
	/// <see cref="UnauthorizedAccessException"/>/<see cref="IOException"/> surface as an
	/// unhandled 500 (leaking a stack trace server-side) when its configured
	/// <c>UploadStagingPath</c> mount was missing or unwritable; this gives that failure
	/// mode a clean, documented shape instead.
	/// </summary>
	public static ApiException Unavailable(string message, string? detail = null)
	{
		return new ApiException(HttpStatusCode.ServiceUnavailable, "service_unavailable", message, detail);
	}
}
