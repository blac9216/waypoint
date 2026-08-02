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
