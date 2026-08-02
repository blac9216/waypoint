using System.Net;
using Waypoint.Core.Errors;

namespace Waypoint.Tests.Core;

public sealed class ApiExceptionTests
{
	[Fact]
	public void ModeUnavailable_UsesDocumented409ConflictWithModeUnavailableCode()
	{
		ApiException exception = ApiException.ModeUnavailable();

		Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
		Assert.Equal("mode_unavailable", exception.Code);
	}

	[Fact]
	public void ToErrorDetail_CarriesCodeMessageAndDetail()
	{
		ApiException exception = new(HttpStatusCode.BadRequest, "validation_error", "Invalid input.", "field 'name' is required");

		ErrorDetail detail = exception.ToErrorDetail();

		Assert.Equal("validation_error", detail.Code);
		Assert.Equal("Invalid input.", detail.Message);
		Assert.Equal("field 'name' is required", detail.Detail);
	}

	[Fact]
	public void ToErrorDetail_WithoutDetail_LeavesDetailNull()
	{
		ApiException exception = ApiException.NotFound();

		ErrorDetail detail = exception.ToErrorDetail();

		Assert.Null(detail.Detail);
	}
}
