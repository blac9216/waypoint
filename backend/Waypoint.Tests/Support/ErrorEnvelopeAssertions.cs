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

namespace Waypoint.Tests.Support;

/// <summary>Shared assertions for the documented error envelope shape: <c>{ "error": { "code", "message" } }</c>.</summary>
public static class ErrorEnvelopeAssertions
{
	public static void AssertEnvelope(string json, string expectedCode)
	{
		using JsonDocument document = JsonDocument.Parse(json);
		JsonElement root = document.RootElement;

		Assert.True(root.TryGetProperty("error", out JsonElement error), "Response body must have a top-level \"error\" object.");
		Assert.True(error.TryGetProperty("code", out JsonElement code), "\"error\" must have a \"code\" field.");
		Assert.True(error.TryGetProperty("message", out JsonElement message), "\"error\" must have a \"message\" field.");
		Assert.Equal(expectedCode, code.GetString());
		Assert.False(string.IsNullOrWhiteSpace(message.GetString()));

		// The envelope must be the *only* top-level shape — no sibling fields leaking
		// framework/exception detail outside "error".
		int propertyCount = 0;
		foreach (JsonProperty _ in root.EnumerateObject())
		{
			propertyCount++;
		}
		Assert.Equal(1, propertyCount);
	}
}
