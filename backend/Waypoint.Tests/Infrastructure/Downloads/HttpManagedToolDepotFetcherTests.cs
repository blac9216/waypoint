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
using Waypoint.Core.Downloads;
using Waypoint.Infrastructure.Downloads;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// Issue #39 depot-fetch install path: <see cref="HttpManagedToolDepotFetcher"/>
/// against a stubbed transport (<see cref="DelegatingHandler"/>), the same pattern
/// <c>HttpStigManagerUploadClientTests</c> uses -- no lab depot is reachable from CI.
/// Covers success (artifact + signature both fetched, bearer header carries the
/// token), auth failure (401/403 classified distinctly, never echoing the token),
/// unreachable/timeout (bounded by <see cref="ManagedToolOptions.DepotFetchTimeout"/>,
/// degrading to <see cref="ManagedToolDepotFetchFailureKind.Unreachable"/> rather than
/// an uncaught throw), the size cap (aborted before the whole oversize body is
/// buffered, enforced on actual bytes read rather than a trusted <c>Content-Length</c>),
/// and token redaction (the token value never appears in any
/// <see cref="ManagedToolDepotFetchResult.FailureReason"/>).
/// </summary>
public sealed class HttpManagedToolDepotFetcherTests : IDisposable
{
	private const string Token = "s3cr3t-depot-token-value";

	private readonly string _destinationDirectory = Directory.CreateTempSubdirectory("wp-depot-fetch-").FullName;

	public void Dispose()
	{
		if (Directory.Exists(_destinationDirectory))
		{
			Directory.Delete(_destinationDirectory, recursive: true);
		}
	}

	/// <summary>Records every request's Authorization header and path, then answers per <see cref="Responses"/> (keyed by whether the request path ends in <c>.sig</c>) -- lets a test script the artifact and signature legs independently.</summary>
	private sealed class ScriptedHandler : DelegatingHandler
	{
		public List<string?> AuthorizationHeaders { get; } = [];

		public List<string> RequestedPaths { get; } = [];

		public Func<HttpRequestMessage, HttpResponseMessage>? OnArtifactRequest { get; set; }

		public Func<HttpRequestMessage, HttpResponseMessage>? OnSignatureRequest { get; set; }

		public TimeSpan Delay { get; set; } = TimeSpan.Zero;

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			AuthorizationHeaders.Add(request.Headers.Authorization?.Parameter);
			RequestedPaths.Add(request.RequestUri!.AbsolutePath);

			if (Delay > TimeSpan.Zero)
			{
				await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
			}

			bool isSignature = request.RequestUri.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal);
			HttpResponseMessage? response = isSignature ? OnSignatureRequest?.Invoke(request) : OnArtifactRequest?.Invoke(request);
			return response ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
		}
	}

	private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
	{
		// disposeHandler: false -- the handler is a test fixture the test method owns.
		public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
	}

	private static HttpManagedToolDepotFetcher CreateFetcher(ScriptedHandler handler, ManagedToolOptions? options = null)
	{
		FakeHttpClientFactory factory = new(handler);
		return new HttpManagedToolDepotFetcher(
			factory, Options.Create(options ?? DefaultOptions()), NullLogger<HttpManagedToolDepotFetcher>.Instance);
	}

	private static ManagedToolOptions DefaultOptions() => new()
	{
		DepotFetchUrlTemplate = "https://depot.example.internal/vcf-download-tool/{version}",
		DepotFetchTimeout = TimeSpan.FromSeconds(5),
		DepotFetchMaxBytes = 1024,
	};

	[Fact]
	public async Task FetchAsync_Success_DownloadsArtifactAndSignature_BearerCarriesTheToken()
	{
		ScriptedHandler handler = new();
		HttpManagedToolDepotFetcher fetcher = CreateFetcher(handler);

		ManagedToolDepotFetchResult result = await fetcher.FetchAsync(Token, "1.4.2", _destinationDirectory, CancellationToken.None);

		Assert.True(result.Succeeded);
		Assert.True(File.Exists(result.ArtifactPath));
		Assert.True(File.Exists(result.SignaturePath));
		Assert.Equal([1, 2, 3], File.ReadAllBytes(result.ArtifactPath!));
		Assert.Equal(2, handler.AuthorizationHeaders.Count);
		Assert.All(handler.AuthorizationHeaders, header => Assert.Equal(Token, header));
		Assert.Contains(handler.RequestedPaths, path => path.EndsWith("1.4.2", StringComparison.Ordinal));
		Assert.Contains(handler.RequestedPaths, path => path.EndsWith("1.4.2.sig", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData(HttpStatusCode.Unauthorized)]
	[InlineData(HttpStatusCode.Forbidden)]
	public async Task FetchAsync_AuthFailure_ClassifiedDistinctly_TokenNeverInReason(HttpStatusCode statusCode)
	{
		ScriptedHandler handler = new() { OnArtifactRequest = _ => new HttpResponseMessage(statusCode) };
		HttpManagedToolDepotFetcher fetcher = CreateFetcher(handler);

		ManagedToolDepotFetchResult result = await fetcher.FetchAsync(Token, null, _destinationDirectory, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Equal(ManagedToolDepotFetchFailureKind.AuthFailure, result.FailureKind);
		Assert.DoesNotContain(Token, result.FailureReason, StringComparison.Ordinal);
		Assert.Empty(Directory.GetFiles(_destinationDirectory));
	}

	[Fact]
	public async Task FetchAsync_NonAuthErrorStatus_ClassifiedAsOther()
	{
		ScriptedHandler handler = new() { OnArtifactRequest = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
		HttpManagedToolDepotFetcher fetcher = CreateFetcher(handler);

		ManagedToolDepotFetchResult result = await fetcher.FetchAsync(Token, null, _destinationDirectory, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Equal(ManagedToolDepotFetchFailureKind.Other, result.FailureKind);
	}

	[Fact]
	public async Task FetchAsync_ResponseExceedsSizeCap_AbortsBeforeBufferingTheWholeBody()
	{
		byte[] oversize = new byte[4096]; // options cap this test uses is 1024 bytes
		ScriptedHandler handler = new()
		{
			OnArtifactRequest = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(oversize) },
		};
		HttpManagedToolDepotFetcher fetcher = CreateFetcher(handler);

		ManagedToolDepotFetchResult result = await fetcher.FetchAsync(Token, null, _destinationDirectory, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Equal(ManagedToolDepotFetchFailureKind.TooLarge, result.FailureKind);
		Assert.DoesNotContain(Token, result.FailureReason, StringComparison.Ordinal);
	}

	[Fact]
	public async Task FetchAsync_TimesOut_DegradesToUnreachable_NotAnUncaughtThrow()
	{
		ScriptedHandler handler = new() { Delay = TimeSpan.FromSeconds(5) };
		ManagedToolOptions options = DefaultOptions();
		options.DepotFetchTimeout = TimeSpan.FromMilliseconds(150);
		HttpManagedToolDepotFetcher fetcher = CreateFetcher(handler, options);

		ManagedToolDepotFetchResult result = await fetcher.FetchAsync(Token, null, _destinationDirectory, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Equal(ManagedToolDepotFetchFailureKind.Unreachable, result.FailureKind);
		Assert.Contains("timed out", result.FailureReason, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task FetchAsync_TransportThrows_DegradesToUnreachable()
	{
		ScriptedHandler handler = new()
		{
			OnArtifactRequest = _ => throw new HttpRequestException("connection refused"),
		};
		HttpManagedToolDepotFetcher fetcher = CreateFetcher(handler);

		ManagedToolDepotFetchResult result = await fetcher.FetchAsync(Token, null, _destinationDirectory, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Equal(ManagedToolDepotFetchFailureKind.Unreachable, result.FailureKind);
	}

	[Fact]
	public async Task FetchAsync_NoUrlConfigured_FailsCleanlyWithoutAnyRequest()
	{
		ScriptedHandler handler = new();
		ManagedToolOptions options = DefaultOptions();
		options.DepotFetchUrlTemplate = null;
		HttpManagedToolDepotFetcher fetcher = CreateFetcher(handler, options);

		ManagedToolDepotFetchResult result = await fetcher.FetchAsync(Token, null, _destinationDirectory, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Equal(ManagedToolDepotFetchFailureKind.Other, result.FailureKind);
		Assert.Empty(handler.RequestedPaths);
	}

	[Fact]
	public async Task FetchAsync_SignatureLegFails_ArtifactFileIsCleanedUp()
	{
		ScriptedHandler handler = new()
		{
			OnSignatureRequest = _ => new HttpResponseMessage(HttpStatusCode.NotFound),
		};
		HttpManagedToolDepotFetcher fetcher = CreateFetcher(handler);

		ManagedToolDepotFetchResult result = await fetcher.FetchAsync(Token, "1.0", _destinationDirectory, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Empty(Directory.GetFiles(_destinationDirectory));
	}
}
