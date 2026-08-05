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

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Waypoint.Api.Diagnostics;
using Waypoint.Tests.Support;

namespace Waypoint.Tests.Api;

/// <summary>
/// Shared HttpListener fixture for probe verdict tests.
/// 
/// A single listener is bound for the lifetime of the collection so tests cannot race on
/// port reservation / listener lifecycle (issue #145 — <c>HttpListenerException: Address
/// already in use</c> during disposal). Tests in the collection are serialized by
/// xUnit default, and each test queues the canned response it wants the listener to serve.
/// </summary>
#pragma warning disable CA1001 // Type owns disposable field '_listener' — disposed via IAsyncLifetime.DisposeAsync
public sealed class HealthCheckListenerFixture : IAsyncLifetime
#pragma warning restore CA1001
{
	private HttpListener? _listener;

	/// <summary>The base URL (including trailing slash) of the running listener.</summary>
	public string Prefix { get; } = $"http://127.0.0.1:{ApiProcess.GetFreePort()}/";

	/// <summary>Queue of responses the listener should serve, one per incoming request.</summary>
	private readonly ConcurrentQueue<(HttpStatusCode Code, string Body)> _responses = new();

	public Task InitializeAsync()
	{
		_listener = new HttpListener();
		_listener.Prefixes.Add(Prefix);
		_listener.Start();

		_ = Task.Run(async () =>
		{
			while (_listener.IsListening)
			{
				try
				{
					HttpListenerContext context = await _listener.GetContextAsync();
					_responses.TryDequeue(out var response);
					byte[] payload = Encoding.UTF8.GetBytes(response.Body);
					context.Response.StatusCode = (int)response.Code;
					context.Response.ContentType = "application/json; charset=utf-8";
					context.Response.ContentLength64 = payload.Length;
					await context.Response.OutputStream.WriteAsync(payload);
					context.Response.Close();
				}
				catch
				{
					// Listener stopped during GetContextAsync — expected during DisposeAsync.
					break;
				}
			}
		});

		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		_listener?.Stop();
		// Allow the serve loop to exit cleanly after Stop().
		await Task.Delay(250).ConfigureAwait(false);
	}

	/// <summary>Enqueue a canned response for the next incoming request.</summary>
	public void EnqueueResponse(HttpStatusCode code, string body)
	{
		_responses.Enqueue((code, body));
	}
}

/// <summary>Binds the verdict tests to the shared listener collection.</summary>
[CollectionDefinition("HealthCheckProbeVerdict")]
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
public sealed class HealthCheckProbeVerdictCollection : ICollectionFixture<HealthCheckListenerFixture>
#pragma warning restore CA1711
{
}

/// <summary>
/// The probe's verdict logic, exercised in-process against a stub HTTP server so every
/// branch (non-200, wrong payload, unparseable payload, unreachable) is asserted without
/// spawning a process per case. <see cref="HealthCheckProbeTests"/> covers the same
/// mechanism end-to-end through the real binary's exit code.
/// </summary>
[Collection("HealthCheckProbeVerdict")]
public sealed class HealthCheckProbeVerdictTests(HealthCheckListenerFixture fixture)
{
	[Fact]
	public async Task Healthy200_ReturnsZero()
	{
		int verdict = await ProbeAsync(fixture, HttpStatusCode.OK, "{\"status\":\"ok\",\"version\":\"0.0.0-dev\"}");

		Assert.Equal(0, verdict);
	}

	[Fact]
	public async Task NonSuccessStatus_ReturnsOne()
	{
		int verdict = await ProbeAsync(fixture, HttpStatusCode.ServiceUnavailable, "{\"status\":\"ok\"}");

		Assert.Equal(1, verdict);
	}

	[Fact]
	public async Task Status200ButNotOk_ReturnsOne()
	{
		// A 200 is not enough — the payload has to actually say the app is healthy.
		int verdict = await ProbeAsync(fixture, HttpStatusCode.OK, "{\"status\":\"degraded\"}");

		Assert.Equal(1, verdict);
	}

	[Fact]
	public async Task Status200WithUnparseableBody_ReturnsOne()
	{
		int verdict = await ProbeAsync(fixture, HttpStatusCode.OK, "not json at all");

		Assert.Equal(1, verdict);
	}

	[Fact]
	public async Task Status200WithoutAStatusField_ReturnsOne()
	{
		int verdict = await ProbeAsync(fixture, HttpStatusCode.OK, "{\"version\":\"0.0.0-dev\"}");

		Assert.Equal(1, verdict);
	}

	[Fact]
	public async Task NothingListening_ReturnsOne()
	{
		int verdict = await HealthCheckProbe.RunAsync(
			$"http://127.0.0.1:{ApiProcess.GetFreePort()}/api/v1/health");

		Assert.Equal(1, verdict);
	}

	/// <summary>Serves one canned response from the shared listener and returns the probe verdict.</summary>
	private static async Task<int> ProbeAsync(
		HealthCheckListenerFixture fixture, HttpStatusCode statusCode, string body)
	{
		fixture.EnqueueResponse(statusCode, body);
		return await HealthCheckProbe.RunAsync($"{fixture.Prefix}api/v1/health");
	}
}
