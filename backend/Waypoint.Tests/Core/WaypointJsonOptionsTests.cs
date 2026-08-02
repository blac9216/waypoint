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
