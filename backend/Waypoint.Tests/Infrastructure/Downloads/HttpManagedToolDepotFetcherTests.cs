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
/// Issue #671 depot-fetch install path: <see cref="HttpManagedToolDepotFetcher"/>
/// against a stubbed transport (<see cref="DelegatingHandler"/>), the same pattern
/// <c>HttpStigManagerUploadClientTests</c> uses -- no lab depot is reachable from CI.
/// Covers success (artifact, catalog, and catalog signature all fetched into a
/// repository-root shape, bearer header carries the credential on every leg), each
/// leg's independent failure (auth, unreachable/timeout, oversize, bad status),
/// missing configuration, and credential redaction (the credential value never
/// appears in any <see cref="ManagedToolDepotFetchResult.FailureReason"/>). This
/// fetcher no longer requests <c>&lt;artifact&gt;.sig</c> -- the real vendor does not
/// publish one (issue #671's root cause).
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

	/// <summary>Records every request's Authorization header and path, then answers per leg (artifact/catalog/catalog-signature, distinguished by request path) -- lets a test script each leg independently.</summary>
	private sealed class ScriptedHandler : DelegatingHandler
	{
		public List<string?> AuthorizationHeaders { get; } = [];

		public List<string> RequestedPaths { get; } = [];

		public Func<HttpRequestMessage, HttpResponseMessage>? OnArtifactRequest { get; set; }

		public Func<HttpRequestMessage, HttpResponseMessage>? OnCatalogRequest { get; set; }

		public Func<HttpRequestMessage, HttpResponseMessage>? OnCatalogSignatureRequest { get; set; }

		public TimeSpan Delay { get; set; } = TimeSpan.Zero;

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			AuthorizationHeaders.Add(request.Headers.Authorization?.Parameter);
			string path = request.RequestUri!.AbsolutePath;
			RequestedPaths.Add(path);

			if (Delay > TimeSpan.Zero)
			{
				await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
			}

			HttpResponseMessage? response = path switch
			{
				_ when path.EndsWith("/catalog.sig", StringComparison.Ordinal) => OnCatalogSignatureRequest?.Invoke(request),
				_ when path.EndsWith("/catalog.json", StringComparison.Ordinal) => OnCatalogRequest?.Invoke(request),
				_ => OnArtifactRequest?.Invoke(request),
			};
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
		DepotCatalogUrl = "https://depot.example.internal/metadata/catalog.json",
		DepotCatalogSignatureUrl = "https://depot.example.internal/metadata/catalog.sig",
		DepotFetchTimeout = TimeSpan.FromSeconds(5),
		DepotFetchMaxBytes = 1024,
		ProductVersionCatalogPath = "PROD/metadata/productVersionCatalog/v1/productVersionCatalog.json",
		ProductVersionCatalogSignaturePath = "PROD/metadata/productVersionCatalog/v1/productVersionCatalog.sig",
	};

	[Fact]
	public async Task FetchAsync_Success_DownloadsArtifactCatalogAndSignature_BearerCarriesTheCredentialOnEveryLeg()
	{
		ScriptedHandler handler = new();
		HttpManagedToolDepotFetcher fetcher = CreateFetcher(handler);

		ManagedToolDepotFetchResult result = await fetcher.FetchAsync(Token, "1.4.2", _destinationDirectory, CancellationToken.None);

		Assert.True(result.Succeeded);
		Assert.True(File.Exists(result.ArtifactPath));
		Assert.Equal([1, 2, 3], File.ReadAllBytes(result.ArtifactPath!));

		string catalogPath = Path.Combine(result.RepositoryRoot!, "PROD", "metadata", "productVersionCatalog", "v1", "productVersionCatalog.json");
		string catalogSignaturePath = Path.Combine(result.RepositoryRoot!, "PROD", "metadata", "productVersionCatalog", "v1", "productVersionCatalog.sig");
		Assert.True(File.Exists(catalogPath));
		Assert.True(File.Exists(catalogSignaturePath));

		Assert.Equal(3, handler.AuthorizationHeaders.Count);
		Assert.All(handler.AuthorizationHeaders, header => Assert.Equal(Token, header));
		Assert.Contains(handler.RequestedPaths, path => path.EndsWith("1.4.2", StringComparison.Ordinal));
		Assert.Contains(handler.RequestedPaths, path => path.EndsWith("catalog.json", StringComparison.Ordinal));
		Assert.Contains(handler.RequestedPaths, path => path.EndsWith("catalog.sig", StringComparison.Ordinal));
		Assert.DoesNotContain(handler.RequestedPaths, path => path.EndsWith(".sig", StringComparison.Ordinal) && path.Contains("vcf-download-tool", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData(HttpStatusCode.Unauthorized)]
	[InlineData(HttpStatusCode.Forbidden)]
	public async Task FetchAsync_ArtifactAuthFailure_ClassifiedDistinctly_TokenNeverInReason(HttpStatusCode statusCode)
	{
		ScriptedHandler handler = new() { OnArtifactRequest = _ => new HttpResponseMessage(statusCode) };
		HttpManagedToolDepotFetcher fetcher = CreateFetcher(handler);

		ManagedToolDepotFetchResult result = await fetcher.FetchAsync(Token, null, _destinationDirectory, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Equal(ManagedToolDepotFetchFailureKind.AuthFailure, result.FailureKind);
		Assert.DoesNotContain(Token, result.FailureReason, StringComparison.Ordinal);
		AssertStagingFullyCleanedUp();
	}

	[Fact]
	public async Task FetchAsync_CatalogAuthFailure_ClassifiedDistinctly_ArtifactAlreadyFetchedIsCleanedUp()
	{
		ScriptedHandler handler = new() { OnCatalogRequest = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) };
		HttpManagedToolDepotFetcher fetcher = CreateFetcher(handler);

		ManagedToolDepotFetchResult result = await fetcher.FetchAsync(Token, null, _destinationDirectory, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Equal(ManagedToolDepotFetchFailureKind.AuthFailure, result.FailureKind);
		Assert.DoesNotContain(Token, result.FailureReason, StringComparison.Ordinal);
		AssertStagingFullyCleanedUp();
	}

	[Fact]
	public async Task FetchAsync_CatalogSignatureAuthFailure_ClassifiedDistinctly_EarlierLegsCleanedUp()
	{
		ScriptedHandler handler = new() { OnCatalogSignatureRequest = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) };
		HttpManagedToolDepotFetcher fetcher = CreateFetcher(handler);

		ManagedToolDepotFetchResult result = await fetcher.FetchAsync(Token, null, _destinationDirectory, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Equal(ManagedToolDepotFetchFailureKind.AuthFailure, result.FailureKind);
		Assert.DoesNotContain(Token, result.FailureReason, StringComparison.Ordinal);
		AssertStagingFullyCleanedUp();
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
	public async Task FetchAsync_CatalogMissing404_ClassifiedAsOther_TokenNeverInReason()
	{
		ScriptedHandler handler = new() { OnCatalogRequest = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };
		HttpManagedToolDepotFetcher fetcher = CreateFetcher(handler);

		ManagedToolDepotFetchResult result = await fetcher.FetchAsync(Token, null, _destinationDirectory, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Equal(ManagedToolDepotFetchFailureKind.Other, result.FailureKind);
		Assert.DoesNotContain(Token, result.FailureReason, StringComparison.Ordinal);
		AssertStagingFullyCleanedUp();
	}

	[Fact]
	public async Task FetchAsync_ArtifactResponseExceedsSizeCap_AbortsBeforeBufferingTheWholeBody()
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
		AssertStagingFullyCleanedUp();
	}

	[Fact]
	public async Task FetchAsync_CatalogResponseExceedsSizeCap_AbortsAndCleansUpArtifact()
	{
		byte[] oversize = new byte[4096];
		ScriptedHandler handler = new()
		{
			OnCatalogRequest = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(oversize) },
		};
		HttpManagedToolDepotFetcher fetcher = CreateFetcher(handler);

		ManagedToolDepotFetchResult result = await fetcher.FetchAsync(Token, null, _destinationDirectory, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Equal(ManagedToolDepotFetchFailureKind.TooLarge, result.FailureKind);
		AssertStagingFullyCleanedUp();
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

	[Theory]
	[InlineData(null, "https://depot.example.internal/metadata/catalog.json", "https://depot.example.internal/metadata/catalog.sig")]
	[InlineData("https://depot.example.internal/vcf-download-tool/{version}", null, "https://depot.example.internal/metadata/catalog.sig")]
	[InlineData("https://depot.example.internal/vcf-download-tool/{version}", "https://depot.example.internal/metadata/catalog.json", null)]
	public async Task FetchAsync_AnyMetadataUrlMissing_FailsCleanlyWithoutAnyRequest(string? artifactUrl, string? catalogUrl, string? catalogSignatureUrl)
	{
		ScriptedHandler handler = new();
		ManagedToolOptions options = DefaultOptions();
		options.DepotFetchUrlTemplate = artifactUrl;
		options.DepotCatalogUrl = catalogUrl;
		options.DepotCatalogSignatureUrl = catalogSignatureUrl;
		HttpManagedToolDepotFetcher fetcher = CreateFetcher(handler, options);

		ManagedToolDepotFetchResult result = await fetcher.FetchAsync(Token, null, _destinationDirectory, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Equal(ManagedToolDepotFetchFailureKind.Other, result.FailureKind);
		Assert.Empty(handler.RequestedPaths);
	}

	[Fact]
	public async Task FetchAsync_CatalogLegFails_ArtifactFileIsCleanedUp()
	{
		ScriptedHandler handler = new()
		{
			OnCatalogRequest = _ => new HttpResponseMessage(HttpStatusCode.NotFound),
		};
		HttpManagedToolDepotFetcher fetcher = CreateFetcher(handler);

		ManagedToolDepotFetchResult result = await fetcher.FetchAsync(Token, "1.0", _destinationDirectory, CancellationToken.None);

		Assert.False(result.Succeeded);
		AssertStagingFullyCleanedUp();
	}

	[Fact]
	public async Task FetchAsync_CatalogSignatureLegFails_ArtifactAndCatalogAreCleanedUp()
	{
		ScriptedHandler handler = new()
		{
			OnCatalogSignatureRequest = _ => new HttpResponseMessage(HttpStatusCode.NotFound),
		};
		HttpManagedToolDepotFetcher fetcher = CreateFetcher(handler);

		ManagedToolDepotFetchResult result = await fetcher.FetchAsync(Token, "1.0", _destinationDirectory, CancellationToken.None);

		Assert.False(result.Succeeded);
		AssertStagingFullyCleanedUp();
	}

	/// <summary>Nothing under the destination directory should remain: no loose artifact file and no staged repository-root directory (or any of its contents).</summary>
	private void AssertStagingFullyCleanedUp()
	{
		Assert.Empty(Directory.GetFiles(_destinationDirectory));
		Assert.Empty(Directory.GetDirectories(_destinationDirectory));
	}
}
