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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.Swagger;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Api;

/// <summary>
/// Issue #1453 AC: "API response shapes are documented (OpenAPI/contract file)". No
/// OpenAPI snapshot/contract test exists repo-wide yet, so this asserts directly
/// against the generated document (registered by <c>AddSwaggerGen()</c> in
/// <c>Program.cs</c>) that every <c>RetentionController</c> route is present, with the
/// DTO schemas it declares reachable from the document's component schemas.
/// </summary>
public sealed class RetentionOpenApiTests
{
	[Fact]
	public void SwaggerDocument_ListsEveryRetentionRoute()
	{
		using WaypointApiFactory factory = new();
		ISwaggerProvider provider = factory.Services.GetRequiredService<ISwaggerProvider>();

		OpenApiDocument document = provider.GetSwagger("v1");

		string[] expectedRoutes =
		[
			"/api/v1/download-retention/state",
			"/api/v1/download-retention/{id}/pin",
			"/api/v1/download-retention/{id}/unpin",
			"/api/v1/download-retention/{id}/purge-now",
			"/api/v1/download-retention/dial",
			"/api/v1/download-retention/review-list",
		];

		foreach (string route in expectedRoutes)
		{
			Assert.Contains(route, document.Paths.Keys);
		}

		string[] expectedSchemas =
		[
			nameof(Waypoint.Api.Contracts.RetainedContentStateResponse),
			nameof(Waypoint.Api.Contracts.PurgeNowResponse),
			nameof(Waypoint.Api.Contracts.RetentionDialResponse),
			nameof(Waypoint.Api.Contracts.ReviewListEntryResponse),
			nameof(Waypoint.Api.Contracts.DeleteReviewListEntryResponse),
		];
		foreach (string schema in expectedSchemas)
		{
			Assert.Contains(schema, document.Components.Schemas.Keys);
		}
	}
}
