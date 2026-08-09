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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Waypoint.Core.StigManager;
using Waypoint.Infrastructure.StigManager;
using Xunit;

namespace Waypoint.Tests.Infrastructure.StigManager;

/// <summary>
/// Issue #320: <see cref="HttpStigManagerUploadClient"/> bounds each network call with
/// its own <see cref="StigManagerClientOptions.UploadTimeout"/> budget rather than
/// relying on the inherited 100 s <c>HttpClient.Timeout</c> default. These tests stub
/// the transport (<see cref="DelegatingHandler"/>) directly -- no lab STIG Manager
/// instance is reachable from CI, same constraint the class's own doc comment and
/// <c>StigManagerUploadApiTests</c> describe -- and prove: (1) a call that outlasts the
/// configured timeout degrades to <see cref="StigManagerUploadOutcome.Failed"/>, never an
/// uncaught throw, preserving <c>ScanUploadCoordinator</c>'s "never fails the scan run"
/// contract; (2) the timeout is genuinely configurable (a short injected value trips
/// promptly against a deliberately slow stub); (3) the normal fast path is unaffected by
/// the new budget.
/// </summary>
public sealed class HttpStigManagerUploadClientTests
{
	/// <summary>Delays every response by <see cref="Delay"/> before returning <see cref="StatusCode"/> with an empty JSON object body -- enough to trip a short configured timeout deterministically without a real network.</summary>
	private sealed class DelayingHandler : DelegatingHandler
	{
		public TimeSpan Delay { get; set; } = TimeSpan.Zero;

		public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

		public string ResponseBody { get; set; } = "{}";

		public int CallCount { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			CallCount++;
			if (Delay > TimeSpan.Zero)
			{
				await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
			}

			return new HttpResponseMessage(StatusCode)
			{
				Content = new StringContent(ResponseBody, System.Text.Encoding.UTF8, "application/json"),
			};
		}
	}

	private sealed class FakeHttpClientFactory : IHttpClientFactory
	{
		private readonly HttpMessageHandler _handler;

		public FakeHttpClientFactory(HttpMessageHandler handler)
		{
			_handler = handler;
		}

		// A fresh, non-owning HttpClient per call (disposeHandler: false) -- the
		// handler is a test fixture the caller manages, mirroring how
		// IHttpClientFactory hands out clients backed by a shared, pooled handler in
		// the real implementation.
		public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
	}

	private static readonly ResolvedStigManagerConnection Connection = new(
		Endpoint: "https://stigman.example.internal/api/",
		Authority: "https://keycloak.example.internal/realms/stigman/",
		Collection: "vcf-collection",
		ClientId: "waypoint-appliance",
		Scope: "stig-manager:collection",
		CredentialId: null,
		Source: StigManagerConnectionSource.Global);

	private static HttpStigManagerUploadClient CreateClient(DelayingHandler handler, TimeSpan uploadTimeout)
	{
		FakeHttpClientFactory factory = new(handler);
		IOptions<StigManagerClientOptions> options = Options.Create(new StigManagerClientOptions { UploadTimeout = uploadTimeout });
		return new HttpStigManagerUploadClient(factory, options, NullLogger<HttpStigManagerUploadClient>.Instance);
	}

	private static string CreateTempCkl()
	{
		string path = Path.Combine(Path.GetTempPath(), $"wp-stigman-upload-{Guid.NewGuid():N}.ckl");
		File.WriteAllText(path, "<CHECKLIST><STIGS/></CHECKLIST>");
		return path;
	}

	[Fact]
	public async Task UploadCklAsync_DiscoveryHangsPastTimeout_DegradesToFailedNotThrow()
	{
		// The discovery GET (the first network leg) delays far longer than a short
		// configured budget -- CancelAfter should trip well before any real network
		// stall would, and the result must be Failed, never an escaped exception.
		DelayingHandler handler = new() { Delay = TimeSpan.FromSeconds(5) };
		HttpStigManagerUploadClient client = CreateClient(handler, TimeSpan.FromMilliseconds(200));
		string cklPath = CreateTempCkl();

		try
		{
			StigManagerUploadResult result = await client.UploadCklAsync(Connection, "secret", cklPath, CancellationToken.None);

			Assert.Equal(StigManagerUploadOutcome.Failed, result.Outcome);
			Assert.NotNull(result.Detail);
			Assert.Contains("timed out", result.Detail, StringComparison.OrdinalIgnoreCase);
		}
		finally
		{
			File.Delete(cklPath);
		}
	}

	[Fact]
	public async Task UploadCklAsync_TimeoutIsConfigurable_ShorterBudgetTripsSooner()
	{
		// A stub that always delays 300ms: a 2s budget comfortably completes, a 50ms
		// budget does not -- proves UploadTimeout actually drives the observed budget
		// rather than some other fixed constant.
		DelayingHandler slowHandler = new() { Delay = TimeSpan.FromMilliseconds(300) };
		string cklPath = CreateTempCkl();

		try
		{
			HttpStigManagerUploadClient generousClient = CreateClient(slowHandler, TimeSpan.FromSeconds(2));
			StigManagerUploadResult generousResult = await generousClient.UploadCklAsync(Connection, "secret", cklPath, CancellationToken.None);
			// The discovery stub returns "{}" with no token_endpoint, so this
			// degrades to Failed too, but for a *different* reason -- proving the
			// call actually completed inside the generous budget, not that Failed
			// always follows regardless of timing.
			Assert.Equal(StigManagerUploadOutcome.Failed, generousResult.Outcome);
			Assert.DoesNotContain("timed out", generousResult.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);

			DelayingHandler tightHandlerBackingStore = new() { Delay = TimeSpan.FromMilliseconds(300) };
			HttpStigManagerUploadClient tightClient = CreateClient(tightHandlerBackingStore, TimeSpan.FromMilliseconds(50));
			StigManagerUploadResult tightResult = await tightClient.UploadCklAsync(Connection, "secret", cklPath, CancellationToken.None);
			Assert.Equal(StigManagerUploadOutcome.Failed, tightResult.Outcome);
			Assert.Contains("timed out", tightResult.Detail, StringComparison.OrdinalIgnoreCase);
		}
		finally
		{
			File.Delete(cklPath);
		}
	}

	[Fact]
	public async Task UploadCklAsync_FastDiscoveryFailure_UnaffectedByTimeoutBudget()
	{
		// Normal (fast) path: the discovery call returns immediately. Even though the
		// stub JSON has no token_endpoint (so the overall outcome is still Failed --
		// there is no real STIG Manager to authenticate against in CI), the point is
		// that it fails immediately for that reason, not because the new budget
		// interferes with a fast call.
		DelayingHandler handler = new() { Delay = TimeSpan.Zero };
		HttpStigManagerUploadClient client = CreateClient(handler, TimeSpan.FromSeconds(45));
		string cklPath = CreateTempCkl();

		try
		{
			System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
			StigManagerUploadResult result = await client.UploadCklAsync(Connection, "secret", cklPath, CancellationToken.None);
			stopwatch.Stop();

			Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"fast path took {stopwatch.Elapsed}, expected well under the 45s budget");
			Assert.Equal(StigManagerUploadOutcome.Failed, result.Outcome);
			Assert.DoesNotContain("timed out", result.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
			Assert.Equal(1, handler.CallCount);
		}
		finally
		{
			File.Delete(cklPath);
		}
	}

	[Fact]
	public async Task ResolveBenchmarkMetadataAsync_DiscoveryHangsPastTimeout_ReturnsFallbackNotThrow()
	{
		DelayingHandler handler = new() { Delay = TimeSpan.FromSeconds(5) };
		HttpStigManagerUploadClient client = CreateClient(handler, TimeSpan.FromMilliseconds(200));
		StigManagerBenchmarkMetadata fallback = new("VCSA-01", "Fallback Title", "Release: 1", "1");

		StigManagerBenchmarkMetadata result = await client.ResolveBenchmarkMetadataAsync(Connection, "secret", "VCSA-01", fallback, CancellationToken.None);

		Assert.Same(fallback, result);
	}

	[Fact]
	public async Task UploadCklAsync_CallerCancellation_PropagatesAsCancellation()
	{
		// A genuine caller cancellation (not this class's own timeout budget) must
		// still surface as cancellation, not be swallowed into Failed -- the
		// `!cancellationToken.IsCancellationRequested` guard on the catch clauses
		// exists precisely to keep these two cases distinct.
		DelayingHandler handler = new() { Delay = TimeSpan.FromSeconds(5) };
		HttpStigManagerUploadClient client = CreateClient(handler, TimeSpan.FromSeconds(30));
		string cklPath = CreateTempCkl();
		using CancellationTokenSource callerCts = new();
		callerCts.CancelAfter(TimeSpan.FromMilliseconds(100));

		try
		{
			await Assert.ThrowsAnyAsync<OperationCanceledException>(
				() => client.UploadCklAsync(Connection, "secret", cklPath, callerCts.Token));
		}
		finally
		{
			File.Delete(cklPath);
		}
	}
}
