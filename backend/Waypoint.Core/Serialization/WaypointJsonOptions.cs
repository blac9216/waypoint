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

namespace Waypoint.Core.Serialization;

/// <summary>
/// The single <see cref="JsonSerializerOptions"/> instance for the whole API — snake_case
/// fields per <c>docs/api-contract.md</c> Conventions. Shared between MVC's configured
/// output formatter and any code that serializes JSON by hand outside the MVC pipeline
/// (the error/status-code middleware), so both paths produce byte-identical shapes.
/// </summary>
public static class WaypointJsonOptions
{
	public static readonly JsonSerializerOptions Default = Create();

	/// <summary>Applies the same snake_case conventions to an externally-owned options instance (e.g. MVC's).</summary>
	public static void Apply(JsonSerializerOptions target)
	{
		target.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
		target.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
		target.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
	}

	private static JsonSerializerOptions Create()
	{
		JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
		Apply(options);
		return options;
	}
}
