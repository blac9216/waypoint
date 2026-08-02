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

using Waypoint.Api.Middleware;
using Waypoint.Core.Errors;

namespace Waypoint.Tests.Api;

/// <summary>
/// The default code/message pairs used for bare HTTP statuses that reach the
/// status-code-pages handler without a thrown <see cref="ApiException"/>.
/// </summary>
public sealed class ErrorEnvelopeWriterTests
{
	[Theory]
	[InlineData(401, "unauthenticated")]
	[InlineData(403, "forbidden")]
	[InlineData(404, "not_found")]
	[InlineData(405, "method_not_allowed")]
	// Anything unmapped still gets an envelope rather than an empty body.
	[InlineData(409, "error")]
	[InlineData(500, "error")]
	public void ForStatusCode_MapsEveryStatusToASnakeCaseCode(int statusCode, string expectedCode)
	{
		ErrorDetail detail = ErrorEnvelopeWriter.ForStatusCode(statusCode);

		Assert.Equal(expectedCode, detail.Code);
		Assert.False(string.IsNullOrWhiteSpace(detail.Message));
		Assert.Null(detail.Detail);
	}
}
