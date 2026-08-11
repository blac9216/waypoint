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

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.Serialization;
using Waypoint.Infrastructure.Data;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Epic #7 slice 2 (#166): the SSE HTTP surface end to end against real Postgres --
/// contract envelope and framing, <c>Last-Event-ID</c> reconnect replay through the
/// HTTP layer, run scoping and the pre-stream 404, and the auth guard.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync disposes client and factory after every test.
public sealed class EventStreamEndpointTests : IAsyncLifetime
#pragma warning restore CA1001
{
	private sealed class PostgresApiFactory : WaypointApiFactory
	{
		private readonly string _connectionString;
		private readonly TimeSpan? _sseHeartbeatInterval;

		public PostgresApiFactory(string connectionString, TimeSpan? sseHeartbeatInterval = null)
		{
			_connectionString = connectionString;
			_sseHeartbeatInterval = sseHeartbeatInterval;
		}

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			base.ConfigureWebHost(builder);

			// Config-level overrides do not stick for minimal-hosting apps (the
			// factory's sources seed the builder BEFORE Program adds appsettings.json,
			// so the JSON's Host=postgres would win) -- override the container
			// directly instead: engine enabled with a fast poll, and every
			// Postgres-touching singleton rebuilt on the fixture's connection string.
			builder.ConfigureTestServices(services =>
			{
				services.PostConfigure<Waypoint.Core.Jobs.JobEngineOptions>(options =>
				{
					options.Enabled = true;
					options.EventStreamPollInterval = TimeSpan.FromMilliseconds(50);
					options.EventFlushInterval = TimeSpan.FromMilliseconds(50);
					if (_sseHeartbeatInterval is { } interval)
					{
						// #172: the controller reads this once per stream, so a short
						// interval here is what makes the heartbeat path testable
						// without 15 s of real wall time.
						options.SseHeartbeatInterval = interval;
					}
				});
				// Issue #415: one JobQueueRepository instance satisfies both focused
				// interfaces the EventStreamController (control) and the dispatcher
				// (runner) resolve.
				services.AddSingleton(provider => new Waypoint.Infrastructure.Jobs.JobQueueRepository(
					_connectionString, NullLogger<Waypoint.Infrastructure.Jobs.JobQueueRepository>.Instance));
				services.AddSingleton<Waypoint.Core.Jobs.IJobControlRepository>(provider => provider.GetRequiredService<Waypoint.Infrastructure.Jobs.JobQueueRepository>());
				services.AddSingleton<Waypoint.Core.Jobs.IJobRunnerRepository>(provider => provider.GetRequiredService<Waypoint.Infrastructure.Jobs.JobQueueRepository>());
				// #374: this must resolve the SAME redactor instance registered for
				// ISecretRedactor/ISecretTracker in the base container -- a disconnected
				// `new InPlaySecretRedactor()` here would make EmitAsync scrub against an
				// empty tracked-secret set no matter what a test tracks, silently
				// defeating any canary written through this publisher (caught by the
				// SSE redaction canary below, which failed against the old wiring).
				services.AddSingleton<Waypoint.Core.Jobs.IJobEventPublisher>(provider => new Waypoint.Runner.Jobs.JobEventPublisher(
					_connectionString, commandTimeoutSeconds: 5, provider.GetRequiredService<Waypoint.Core.Logging.ISecretRedactor>(),
					NullLogger<Waypoint.Runner.Jobs.JobEventPublisher>.Instance));
				services.AddSingleton(provider => new Waypoint.Infrastructure.Jobs.JobEventStreamService(
					_connectionString,
					provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Waypoint.Core.Jobs.JobEngineOptions>>(),
					NullLogger<Waypoint.Infrastructure.Jobs.JobEventStreamService>.Instance));
				services.AddSingleton<Waypoint.Core.Jobs.IJobEventFeed>(provider =>
					provider.GetRequiredService<Waypoint.Infrastructure.Jobs.JobEventStreamService>());
				services.AddSingleton(provider => new Waypoint.Runner.Jobs.BufferedJobEventWriter(
					_connectionString,
					provider.GetRequiredService<Waypoint.Core.Logging.ISecretRedactor>(),
					provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Waypoint.Core.Jobs.JobEngineOptions>>(),
					NullLogger<Waypoint.Runner.Jobs.BufferedJobEventWriter>.Instance));
				services.AddSingleton<Waypoint.Core.Jobs.IJobLogBuffer>(provider =>
					provider.GetRequiredService<Waypoint.Runner.Jobs.BufferedJobEventWriter>());
			});
		}
	}

	private readonly PostgresFixture _fixture;
	private PostgresApiFactory _factory = null!;
	private HttpClient _client = null!;
	private string _token = null!;
	private Guid _runId;
	private Guid _otherRunId;

	public EventStreamEndpointTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();

		_factory = new PostgresApiFactory(_fixture.ConnectionString);
		_client = _factory.CreateClient();
		_token = await LoginAsAdminAsync();
		_runId = await SeedRunAsync();
		_otherRunId = await SeedRunAsync();
	}

	public Task DisposeAsync()
	{
		_client.Dispose();
		_factory.Dispose();
		return Task.CompletedTask;
	}

	[Fact]
	public async Task GlobalStream_DeliversTheContractEnvelope()
	{
		await InsertEventAsync(_runId, "envelope-probe");

		using HttpResponseMessage response = await OpenStreamAsync("/api/v1/events?lastEventId=0");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

		(long id, string eventType, JsonElement envelope) = await ReadFirstEventAsync(response);

		Assert.Equal("run.progress", eventType);
		Assert.Equal(id, envelope.GetProperty("seq").GetInt64());
		Assert.Equal("run.progress", envelope.GetProperty("type").GetString());
		Assert.Equal(_runId, envelope.GetProperty("run_id").GetGuid());
		Assert.Equal(JsonValueKind.Null, envelope.GetProperty("job_id").ValueKind);
		Assert.Equal("envelope-probe", envelope.GetProperty("data").GetProperty("marker").GetString());
		Assert.True(DateTimeOffset.TryParse(envelope.GetProperty("ts").GetString(), out _));
	}

	[Fact]
	public async Task LastEventId_ReplaysTheGapExactlyOnce_ThroughHttp()
	{
		await InsertEventAsync(_runId, "gap-0");
		await InsertEventAsync(_runId, "gap-1");
		await InsertEventAsync(_runId, "gap-2");
		long firstSeq = await GetSeqOfAsync("gap-0");

		using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/events");
		request.Headers.Add("Authorization", $"Bearer {_token}");
		request.Headers.Add("Last-Event-ID", firstSeq.ToString(System.Globalization.CultureInfo.InvariantCulture));
		using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

		IReadOnlyList<(long Id, string EventType, JsonElement Envelope)> events = await ReadEventsAsync(response, count: 2);
		(long id1, _, JsonElement envelope1) = events[0];
		(long id2, _, JsonElement envelope2) = events[1];

		Assert.Equal("gap-1", envelope1.GetProperty("data").GetProperty("marker").GetString());
		Assert.Equal("gap-2", envelope2.GetProperty("data").GetProperty("marker").GetString());
		Assert.True(id2 > id1);
		Assert.Equal(firstSeq + 1, id1);
	}

	[Fact]
	public async Task RunStream_IsScoped_AndAnUnknownRunIs404BeforeTheStreamStarts()
	{
		await InsertEventAsync(_otherRunId, "noise");
		await InsertEventAsync(_runId, "scoped-probe");

		using HttpResponseMessage response = await OpenStreamAsync($"/api/v1/runs/{_runId}/events?lastEventId=0");
		(_, _, JsonElement envelope) = await ReadFirstEventAsync(response);
		Assert.Equal("scoped-probe", envelope.GetProperty("data").GetProperty("marker").GetString());

		using HttpRequestMessage missingRequest = new(HttpMethod.Get, $"/api/v1/runs/{Guid.NewGuid()}/events");
		missingRequest.Headers.Add("Authorization", $"Bearer {_token}");
		using HttpResponseMessage missing = await _client.SendAsync(missingRequest);
		Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
		Assert.Contains("not_found", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	/// <summary>
	/// #374: the response-shape sweep (#370, <c>AllControllersResponseShapeTests</c>)
	/// structurally cannot cover this controller -- it returns raw
	/// <c>text/event-stream</c> bytes, no <c>[ProducesResponseType]</c> DTO to walk.
	/// The SSE surface's no-secret guarantee instead rests entirely on write-time
	/// redaction: <see cref="Waypoint.Api.Controllers.EventStreamController.WriteEventAsync"/>
	/// embeds <c>row.PayloadJson</c> verbatim on the claim that it is "already
	/// redacted at write time." This test pins that claim end to end through the real
	/// pipeline (no mocked redactor): track a canary with the process's real
	/// <see cref="ISecretTracker"/>, publish a job_events row containing it through the
	/// real <see cref="IJobEventPublisher"/> (the same redaction path
	/// <see cref="BufferedJobEventWriterTests"/> pins against the table directly), then
	/// read the raw SSE bytes over HTTP and assert the canary never appears in the
	/// wire format -- proving the redaction that protects the table also protects this
	/// specific serialization surface, not just Postgres.
	/// </summary>
	[Fact]
	public async Task ATrackedSecret_NeverReachesTheSseWireFormat()
	{
		const string canary = "invented-sse-redaction-canary-5townsend2";
		ISecretTracker tracker = _factory.Services.GetRequiredService<ISecretTracker>();
		IJobEventPublisher publisher = _factory.Services.GetRequiredService<IJobEventPublisher>();

		using (tracker.Track(canary))
		{
			await publisher.EmitAsync(
				"run.progress", null, _runId,
				JsonSerializer.Serialize(new { marker = "sse-canary-probe", secret = $"auth token {canary} rejected" }, WaypointJsonOptions.Default),
				CancellationToken.None);
		}

		using HttpResponseMessage response = await OpenStreamAsync($"/api/v1/runs/{_runId}/events?lastEventId=0");
		(_, _, JsonElement envelope) = await ReadFirstEventAsync(response);

		string rawData = envelope.GetRawText();
		Assert.DoesNotContain(canary, rawData, StringComparison.Ordinal);
		Assert.Contains("[REDACTED]", envelope.GetProperty("data").GetProperty("secret").GetString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task WithoutAuthentication_TheStreamIs401()
	{
		using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/events");
		using HttpResponseMessage response = await _client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	/// <summary>
	/// #172: <c>JobEngineOptions.SseHeartbeatInterval</c> is the injection point that
	/// makes the heartbeat path testable without 15 s of real wall time -- a quiet
	/// stream (no rows inserted) still proves liveness on a short interval.
	/// </summary>
	[Fact]
	public async Task QuietStream_EmitsAHeartbeatCommentAfterTheConfiguredInterval()
	{
		await using PostgresApiFactory heartbeatFactory = new(_fixture.ConnectionString, sseHeartbeatInterval: TimeSpan.FromMilliseconds(200));
		using HttpClient heartbeatClient = heartbeatFactory.CreateClient();

		HttpResponseMessage loginResponse = await heartbeatClient.PostAsJsonAsync(
			"/api/v1/auth/login", new { username = "admin", password = WaypointApiFactory.TestAdminPassword }, WaypointJsonOptions.Default);
		loginResponse.EnsureSuccessStatusCode();
		using JsonDocument loginDocument = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync());
		string token = loginDocument.RootElement.GetProperty("token").GetString()!;

		using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/events");
		request.Headers.Add("Authorization", $"Bearer {token}");
		using HttpResponseMessage response = await heartbeatClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

		using StreamReader reader = new(await response.Content.ReadAsStreamAsync());
		Stopwatch stopwatch = Stopwatch.StartNew();
		string? heartbeatLine = null;
		while (stopwatch.Elapsed < TimeSpan.FromSeconds(15))
		{
			string? line = await reader.ReadLineAsync();
			if (line is null)
			{
				break;
			}

			if (line.StartsWith(": heartbeat", StringComparison.Ordinal))
			{
				heartbeatLine = line;
				break;
			}
		}

		Assert.NotNull(heartbeatLine);
	}

	/// <summary>
	/// #172: a delivered event still resets the idle clock -- the preserved
	/// pre-#172 semantics ("heartbeat after an interval of silence", not a fixed
	/// cadence) -- so a stream that keeps receiving events inside the interval
	/// delivers them with no heartbeat comment interleaved.
	/// </summary>
	[Fact]
	public async Task ActiveStream_DeliversEventsWithNoHeartbeatBetweenThem()
	{
		await using PostgresApiFactory heartbeatFactory = new(_fixture.ConnectionString, sseHeartbeatInterval: TimeSpan.FromSeconds(5));
		using HttpClient heartbeatClient = heartbeatFactory.CreateClient();

		HttpResponseMessage loginResponse = await heartbeatClient.PostAsJsonAsync(
			"/api/v1/auth/login", new { username = "admin", password = WaypointApiFactory.TestAdminPassword }, WaypointJsonOptions.Default);
		loginResponse.EnsureSuccessStatusCode();
		using JsonDocument loginDocument = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync());
		string token = loginDocument.RootElement.GetProperty("token").GetString()!;

		Guid runId = await SeedRunAsync();
		await InsertEventAsync(runId, "active-probe-0");

		using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/events?lastEventId=0");
		request.Headers.Add("Authorization", $"Bearer {token}");
		using HttpResponseMessage response = await heartbeatClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

		using StreamReader reader = new(await response.Content.ReadAsStreamAsync());
		Stopwatch stopwatch = Stopwatch.StartNew();
		int dataLines = 0;
		while (stopwatch.Elapsed < TimeSpan.FromSeconds(2) && dataLines < 1)
		{
			string? line = await reader.ReadLineAsync();
			if (line is null)
			{
				break;
			}

			Assert.False(line.StartsWith(": heartbeat", StringComparison.Ordinal));
			if (line.StartsWith("data: ", StringComparison.Ordinal))
			{
				dataLines++;
			}
		}

		Assert.Equal(1, dataLines);
	}

	private async Task<HttpResponseMessage> OpenStreamAsync(string path)
	{
		HttpRequestMessage request = new(HttpMethod.Get, path);
		request.Headers.Add("Authorization", $"Bearer {_token}");
		return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
	}

	/// <summary>Reads one full SSE event frame (id/event/data); disposes the body stream.</summary>
	private static async Task<(long Id, string EventType, JsonElement Envelope)> ReadFirstEventAsync(HttpResponseMessage response)
	{
		return (await ReadEventsAsync(response, count: 1))[0];
	}

	/// <summary>Reads SSE lines from one body stream until <paramref name="count"/> full event frames have arrived.</summary>
	private static async Task<IReadOnlyList<(long Id, string EventType, JsonElement Envelope)>> ReadEventsAsync(HttpResponseMessage response, int count)
	{
		List<(long, string, JsonElement)> frames = [];
		using StreamReader reader = new(await response.Content.ReadAsStreamAsync());
		long? id = null;
		string? eventType = null;
		Stopwatch stopwatch = Stopwatch.StartNew();
		while (stopwatch.Elapsed < TimeSpan.FromSeconds(15) && frames.Count < count)
		{
			string? line = await reader.ReadLineAsync();
			if (line is null)
			{
				break;
			}

			if (line.StartsWith("id: ", StringComparison.Ordinal))
			{
				id = long.Parse(line[4..], System.Globalization.CultureInfo.InvariantCulture);
			}
			else if (line.StartsWith("event: ", StringComparison.Ordinal))
			{
				eventType = line[7..];
			}
			else if (line.StartsWith("data: ", StringComparison.Ordinal) && id is not null && eventType is not null)
			{
				frames.Add((id.Value, eventType, JsonDocument.Parse(line[6..]).RootElement.Clone()));
				id = null;
				eventType = null;
			}
		}

		Assert.Equal(count, frames.Count);
		return frames;
	}

	private async Task<string> LoginAsAdminAsync()
	{
		HttpResponseMessage response = await _client.PostAsJsonAsync(
			"/api/v1/auth/login", new { username = "admin", password = WaypointApiFactory.TestAdminPassword }, WaypointJsonOptions.Default);
		response.EnsureSuccessStatusCode();
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return document.RootElement.GetProperty("token").GetString()!;
	}

	private async Task<Guid> SeedRunAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO runs (run_type, scope, state) VALUES ('scan', '{}'::jsonb, 'pending') RETURNING id", connection);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task InsertEventAsync(Guid runId, string marker)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO job_events (run_id, event_type, payload) VALUES ($1, 'run.progress', jsonb_build_object('marker', $2::text))", connection);
		insert.Parameters.AddWithValue(runId);
		insert.Parameters.AddWithValue(marker);
		await insert.ExecuteNonQueryAsync();
	}

	private async Task<long> GetSeqOfAsync(string marker)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new("SELECT seq FROM job_events WHERE payload->>'marker' = $1", connection);
		query.Parameters.AddWithValue(marker);
		return (long)(await query.ExecuteScalarAsync())!;
	}
}
