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
using Waypoint.Core.Downloads.Photon;
using Waypoint.Infrastructure.Downloads.Photon;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads.Photon;

/// <summary>
/// <see cref="HttpPhotonImageListingSource"/> against a stubbed transport, mirroring
/// <c>HttpPhotonRepoMetadataSourceTests</c>'s own <see cref="DelegatingHandler"/>
/// pattern. Every fixture here is INVENTED: a fabricated Apache-style autoindex page
/// served from <c>https://photon.example.internal/photon</c>, never a real vendor
/// mirror capture.
/// </summary>
public sealed class HttpPhotonImageListingSourceTests
{
	private const string ChannelBaseUrl = "https://photon.example.internal/photon/5.0/ga";

	private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : DelegatingHandler
	{
		public List<(HttpMethod Method, string Url)> Requests { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add((request.Method, request.RequestUri!.ToString()));
			return Task.FromResult(respond(request));
		}
	}

	private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
	{
		public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
	}

	private const string AutoindexHtml = """
		<html><body>
		<a href="../">Parent Directory</a>
		<a href="photon-5.0-x86_64.iso">photon-5.0-x86_64.iso</a>   229M
		<a href="photon-5.0-x86_64.ova">photon-5.0-x86_64.ova</a>   198M
		<a href="photon-5.0-x86_64.iso.sha256">photon-5.0-x86_64.iso.sha256</a>   1K
		</body></html>
		""";

	/// <summary>
	/// AC 5 (no-fetch): this type must never issue a <c>GET</c> against a discovered
	/// image file's own body -- only <c>GET</c> the listing page itself and
	/// <c>HEAD</c> each entry. A mutation that swapped the per-file probe from
	/// <c>HEAD</c> to <c>GET</c> would fetch the (fake, but oversized-if-real) payload
	/// this handler stubs, and this test would go red the instant it did.
	/// </summary>
	[Fact]
	public async Task ListImagesAsync_NeverIssuesAGetAgainstAnImageFileBody()
	{
		ScriptedHandler handler = new(request =>
		{
			if (request.RequestUri!.ToString().EndsWith(".iso", StringComparison.Ordinal)
				|| request.RequestUri!.ToString().EndsWith(".ova", StringComparison.Ordinal))
			{
				if (request.Method == HttpMethod.Get)
				{
					throw new InvalidOperationException(
						$"Test fixture forbids GET against an image file body: {request.RequestUri}");
				}

				return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
			}

			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AutoindexHtml) };
		});
		HttpPhotonImageListingSource source = new(new FakeHttpClientFactory(handler));

		IReadOnlyList<PhotonImageListingEntry>? entries = await source.ListImagesAsync(ChannelBaseUrl, CancellationToken.None);

		Assert.NotNull(entries);
		Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Head && r.Url.EndsWith(".iso", StringComparison.Ordinal));
		Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Head && r.Url.EndsWith(".ova", StringComparison.Ordinal));
		Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Get && r.Url.EndsWith(".ova", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ListImagesAsync_ClassifiesRecognizedExtensionsAndSkipsOthers()
	{
		ScriptedHandler handler = new(request => request.Method == HttpMethod.Head
			? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }
			: new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AutoindexHtml) });
		HttpPhotonImageListingSource source = new(new FakeHttpClientFactory(handler));

		IReadOnlyList<PhotonImageListingEntry>? entries = await source.ListImagesAsync(ChannelBaseUrl, CancellationToken.None);

		Assert.NotNull(entries);
		Assert.Equal(2, entries!.Count);
		Assert.Contains(entries, e => e.RelativePath == "photon-5.0-x86_64.iso");
		Assert.Contains(entries, e => e.RelativePath == "photon-5.0-x86_64.ova");
		Assert.DoesNotContain(entries, e => e.RelativePath.EndsWith(".sha256", StringComparison.Ordinal));
	}

	/// <summary>
	/// Issue #1170's guard: SizeBytes must come only from the real HEAD
	/// Content-Length, never from the "229M"/"198M" text the fabricated listing page
	/// renders inline next to each filename -- this type's parser never even looks at
	/// that column, so a listing whose only size signal is the rounded inline text
	/// produces null SizeBytes, not a value derived from "229M".
	/// </summary>
	[Fact]
	public async Task ListImagesAsync_SizeBytesComesOnlyFromHeadContentLength_NeverFromListingText()
	{
		ScriptedHandler handler = new(request =>
		{
			if (request.Method == HttpMethod.Head)
			{
				HttpResponseMessage headResponse = new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
				headResponse.Content.Headers.ContentLength = 240_057_499; // the REAL byte count behind the rounded "229M" label
				return headResponse;
			}
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AutoindexHtml) };
		});
		HttpPhotonImageListingSource source = new(new FakeHttpClientFactory(handler));

		IReadOnlyList<PhotonImageListingEntry>? entries = await source.ListImagesAsync(ChannelBaseUrl, CancellationToken.None);

		PhotonImageListingEntry isoEntry = Assert.Single(entries!, e => e.RelativePath == "photon-5.0-x86_64.iso");
		Assert.Equal(240_057_499, isoEntry.SizeBytes);
	}

	[Fact]
	public async Task ListImagesAsync_ListingUnreachable_ReturnsNull()
	{
		ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
		HttpPhotonImageListingSource source = new(new FakeHttpClientFactory(handler));

		IReadOnlyList<PhotonImageListingEntry>? entries = await source.ListImagesAsync(ChannelBaseUrl, CancellationToken.None);

		Assert.Null(entries);
	}

	[Theory]
	[InlineData("../escape.iso")]
	[InlineData("/rooted.iso")]
	[InlineData("sub/dir.iso")]
	[InlineData("https://evil.example.internal/x.iso")]
	public void IsValidRelativeHref_RejectsHostileHrefs(string href) =>
		Assert.False(HttpPhotonImageListingSource.IsValidRelativeHref(href));

	[Fact]
	public void IsValidRelativeHref_AcceptsPlainFileName() =>
		Assert.True(HttpPhotonImageListingSource.IsValidRelativeHref("photon-5.0-x86_64.iso"));
}
