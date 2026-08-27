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
using System.Text.Json;
using Waypoint.Api.Contracts;
using Waypoint.Core.Jobs;
using Waypoint.Tests.Support;

namespace Waypoint.Tests.Api;

/// <summary>
/// Controller-level tests for issue #757's component-job read surface
/// (<c>GET /runs/{id}/component-jobs/counts</c> and <c>GET /runs/{id}/component-jobs</c>):
/// happy-path wire mapping, 404 for an unknown run, 400 for every malformed
/// filter/cursor value (never a 500 on client-abusable input), and the limit clamp
/// edges. Uses <see cref="RunsTestApiFactory"/>'s <see cref="FakeComponentJobRepository"/>;
/// real SQL correctness lives in
/// <c>Waypoint.Tests.Infrastructure.Postgres.ComponentJobQueryRepositoryTests</c>.
/// </summary>
public sealed class ComponentJobEndpointTests : IClassFixture<RunsTestApiFactory>
{
	private readonly RunsTestApiFactory _factory;

	public ComponentJobEndpointTests(RunsTestApiFactory factory)
	{
		_factory = factory;
	}

	private async Task<HttpResponseMessage> GetAsync(string path, string role = "Viewer")
	{
		HttpClient client = _factory.CreateClient();
		HttpRequestMessage request = new(HttpMethod.Get, path);
		request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
		return await client.SendAsync(request);
	}

	private Guid SeedRun()
	{
		Guid runId = Guid.NewGuid();
		_factory.Repository.SetRun(runId, new RunQueueState("running", false, false, null, "test-user"));
		return runId;
	}

	// -- counts ------------------------------------------------------------

	[Fact]
	public async Task GetComponentJobCounts_HappyPath_MapsRowsToWireShape()
	{
		Guid runId = SeedRun();
		_factory.ComponentJobs.CountRows =
		[
			new ComponentJobCountRow(4, "esxi", "queued", 9500),
			new ComponentJobCountRow(4, "esxi", "failed", 500),
		];

		HttpResponseMessage response = await GetAsync($"/api/v1/runs/{runId}/component-jobs/counts");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement rows = doc.RootElement;
		Assert.Equal(2, rows.GetArrayLength());
		Assert.Equal(4, rows[0].GetProperty("priority").GetInt32());
		Assert.Equal("esxi", rows[0].GetProperty("component_kind").GetString());
		Assert.Equal("queued", rows[0].GetProperty("state").GetString());
		Assert.Equal(9500, rows[0].GetProperty("count").GetInt64());
		Assert.Equal(runId, _factory.ComponentJobs.LastCountsRunId);
	}

	[Fact]
	public async Task GetComponentJobCounts_ForwardsFilterCombination()
	{
		Guid runId = SeedRun();

		HttpResponseMessage response = await GetAsync(
			$"/api/v1/runs/{runId}/component-jobs/counts?state=queued,failed&priority=1,4&component_kind=esxi,unknown&search=host-01");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		ComponentJobFilter filter = _factory.ComponentJobs.LastCountsFilter!;
		Assert.Equal(["queued", "failed"], filter.States);
		Assert.Equal(new short[] { 1, 4 }, filter.Priorities);
		Assert.Equal(["esxi", "unknown"], filter.ComponentKinds);
		Assert.Equal("host-01", filter.Search);
	}

	[Fact]
	public async Task GetComponentJobCounts_UnknownRun_Returns404()
	{
		HttpResponseMessage response = await GetAsync($"/api/v1/runs/{Guid.NewGuid()}/component-jobs/counts");
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Theory]
	[InlineData("state=bogus")]
	[InlineData("state=queued,bogus")]
	[InlineData("priority=0")]
	[InlineData("priority=7")]
	[InlineData("priority=junk")]
	[InlineData("priority=-1")]
	[InlineData("component_kind=warp-core")]
	public async Task GetComponentJobCounts_MalformedFilter_Returns400(string queryString)
	{
		Guid runId = SeedRun();
		HttpResponseMessage response = await GetAsync($"/api/v1/runs/{runId}/component-jobs/counts?{queryString}");
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	// -- list --------------------------------------------------------------

	[Fact]
	public async Task ListComponentJobs_HappyPath_MapsRowsAndEncodesNextCursor()
	{
		Guid runId = SeedRun();
		Guid jobId = Guid.NewGuid();
		DateTimeOffset createdAt = DateTimeOffset.Parse("2026-08-27T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
		_factory.ComponentJobs.NextPage = new ComponentJobPage(
			[
				new ComponentJobRow(jobId, "scan", null, "esxi-01.example.internal", "queued", null, 4, "esxi", 0,
					"2026-08-27T00:00:00Z", null, null),
			],
			new ComponentJobCursorPosition(4, createdAt, jobId));

		HttpResponseMessage response = await GetAsync($"/api/v1/runs/{runId}/component-jobs");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement item = doc.RootElement.GetProperty("items")[0];
		Assert.Equal(jobId.ToString(), item.GetProperty("id").GetString());
		Assert.Equal("esxi", item.GetProperty("component_kind").GetString());
		Assert.Equal("esxi-01.example.internal", item.GetProperty("target_name").GetString());

		// The wire cursor is opaque but must decode back to the exact keyset
		// position the repository reported -- round-trip through the real codec.
		string wireCursor = doc.RootElement.GetProperty("next_cursor").GetString()!;
		Assert.True(ComponentJobCursor.TryDecode(wireCursor, out ComponentJobCursorPosition? decoded));
		Assert.Equal(new ComponentJobCursorPosition(4, createdAt, jobId), decoded);
	}

	[Fact]
	public async Task ListComponentJobs_EndOfSet_ReturnsNullCursor()
	{
		Guid runId = SeedRun();
		_factory.ComponentJobs.NextPage = new ComponentJobPage([], null);

		HttpResponseMessage response = await GetAsync($"/api/v1/runs/{runId}/component-jobs");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		// The host's serializer omits null properties (WhenWritingNull), so "no more
		// pages" is either an explicit null or an absent key -- both are the honest
		// end-of-set signal; what must NEVER appear is a non-null cursor.
		if (doc.RootElement.TryGetProperty("next_cursor", out JsonElement nextCursor))
		{
			Assert.Equal(JsonValueKind.Null, nextCursor.ValueKind);
		}
	}

	[Fact]
	public async Task ListComponentJobs_UnknownRun_Returns404()
	{
		HttpResponseMessage response = await GetAsync($"/api/v1/runs/{Guid.NewGuid()}/component-jobs");
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task ListComponentJobs_GarbageCursor_Returns400NotA500()
	{
		Guid runId = SeedRun();
		HttpResponseMessage response = await GetAsync($"/api/v1/runs/{runId}/component-jobs?cursor=garbage");

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.Contains("validation_error", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ListComponentJobs_ValidCursor_DecodesIntoTheRepositoryQuery()
	{
		Guid runId = SeedRun();
		Guid jobId = Guid.NewGuid();
		DateTimeOffset createdAt = DateTimeOffset.FromUnixTimeMilliseconds(1_772_000_000_000);
		string cursor = ComponentJobCursor.Encode(new ComponentJobCursorPosition(2, createdAt, jobId));

		HttpResponseMessage response = await GetAsync($"/api/v1/runs/{runId}/component-jobs?cursor={Uri.EscapeDataString(cursor)}");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		ComponentJobListQuery query = _factory.ComponentJobs.LastListQuery!;
		Assert.Equal(new ComponentJobCursorPosition(2, createdAt, jobId), query.After);
	}

	[Theory]
	[InlineData(null, 100)] // absent -> default
	[InlineData(0, 1)] // below floor -> clamped up
	[InlineData(1, 1)] // floor
	[InlineData(500, 500)] // ceiling
	[InlineData(501, 500)] // above ceiling -> clamped down
	public async Task ListComponentJobs_ClampsLimit(int? requested, int expected)
	{
		Guid runId = SeedRun();
		string suffix = requested is { } value ? $"?limit={value}" : string.Empty;

		HttpResponseMessage response = await GetAsync($"/api/v1/runs/{runId}/component-jobs{suffix}");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal(expected, _factory.ComponentJobs.LastListQuery!.Limit);
	}

	[Theory]
	[InlineData("state=nope")]
	[InlineData("priority=9")]
	[InlineData("component_kind=hologram")]
	public async Task ListComponentJobs_MalformedFilter_Returns400(string queryString)
	{
		Guid runId = SeedRun();
		HttpResponseMessage response = await GetAsync($"/api/v1/runs/{runId}/component-jobs?{queryString}");
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}
}
