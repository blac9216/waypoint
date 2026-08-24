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
	public void Unavailable_UsesDocumented503ServiceUnavailableWithServiceUnavailableCode()
	{
		ApiException exception = ApiException.Unavailable(
			"The upload-staging location is not writable on this appliance.",
			"Confirm the tool-upload-staging volume is mounted and writable by the backend service.");

		Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
		Assert.Equal("service_unavailable", exception.Code);
		Assert.Equal("The upload-staging location is not writable on this appliance.", exception.Message);

		ErrorDetail detail = exception.ToErrorDetail();
		Assert.Equal("service_unavailable", detail.Code);
		Assert.Equal("Confirm the tool-upload-staging volume is mounted and writable by the backend service.", detail.Detail);
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
