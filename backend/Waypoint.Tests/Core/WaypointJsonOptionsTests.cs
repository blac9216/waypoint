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

using System.Text.Json;
using Waypoint.Core.Errors;
using Waypoint.Core.Serialization;

namespace Waypoint.Tests.Core;

public sealed class WaypointJsonOptionsTests
{
	[Fact]
	public void Default_SerializesRecordsAsSnakeCase()
	{
		ErrorResponse response = new(new ErrorDetail("mode_unavailable", "Not available.", "extra detail"));

		string json = JsonSerializer.Serialize(response, WaypointJsonOptions.Default);

		Assert.Contains("\"error\"", json);
		Assert.Contains("\"code\"", json);
		Assert.Contains("\"message\"", json);
		Assert.Contains("\"detail\"", json);
	}

	[Fact]
	public void Default_OmitsNullOptionalFields()
	{
		ErrorResponse response = new(new ErrorDetail("not_found", "Missing.", Detail: null));

		string json = JsonSerializer.Serialize(response, WaypointJsonOptions.Default);

		Assert.DoesNotContain("\"detail\"", json);
	}
}
