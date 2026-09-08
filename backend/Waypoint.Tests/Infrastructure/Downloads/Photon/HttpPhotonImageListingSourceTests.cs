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

		PhotonImageListingResult result = await source.ListImagesAsync(ChannelBaseUrl, CancellationToken.None);

		Assert.Equal(PhotonImageListingKind.Found, result.Kind);
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

		PhotonImageListingResult result = await source.ListImagesAsync(ChannelBaseUrl, CancellationToken.None);

		Assert.Equal(PhotonImageListingKind.Found, result.Kind);
		Assert.Equal(2, result.Entries.Count);
		Assert.Contains(result.Entries, e => e.RelativePath == "photon-5.0-x86_64.iso");
		Assert.Contains(result.Entries, e => e.RelativePath == "photon-5.0-x86_64.ova");
		Assert.DoesNotContain(result.Entries, e => e.RelativePath.EndsWith(".sha256", StringComparison.Ordinal));
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

		PhotonImageListingResult result = await source.ListImagesAsync(ChannelBaseUrl, CancellationToken.None);

		PhotonImageListingEntry isoEntry = Assert.Single(result.Entries, e => e.RelativePath == "photon-5.0-x86_64.iso");
		Assert.Equal(240_057_499, isoEntry.SizeBytes);
	}

	/// <summary>
	/// Round-1 review finding 2, the "definitively absent" outcome: an explicit 404 on
	/// the channel listing is the ONE failure shape that means "this channel is not
	/// published", and it is the only one that may be reported as
	/// <see cref="PhotonImageListingKind.Absent"/>.
	/// </summary>
	[Fact]
	public async Task ListImagesAsync_Listing404_IsAbsentNotIndeterminate()
	{
		ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
		HttpPhotonImageListingSource source = new(new FakeHttpClientFactory(handler));

		PhotonImageListingResult result = await source.ListImagesAsync(ChannelBaseUrl, CancellationToken.None);

		Assert.Equal(PhotonImageListingKind.Absent, result.Kind);
		Assert.Empty(result.Entries);
		Assert.Null(result.Error);
	}

	/// <summary>
	/// Round-1 review finding 2, the "could not determine" outcomes: a 403, a 5xx, and
	/// any other non-404 failure status say nothing about whether the channel is
	/// published, so each is <see cref="PhotonImageListingKind.Indeterminate"/> with an
	/// error naming the status -- never the 404's Absent, which the handler reads as
	/// "nothing to index here" and passes over without qualifying the sweep.
	/// </summary>
	[Theory]
	[InlineData(HttpStatusCode.Forbidden, "403")]
	[InlineData(HttpStatusCode.InternalServerError, "500")]
	[InlineData(HttpStatusCode.ServiceUnavailable, "503")]
	[InlineData(HttpStatusCode.MethodNotAllowed, "405")]
	public async Task ListImagesAsync_NonNotFoundFailureStatus_IsIndeterminate(HttpStatusCode status, string expectedInError)
	{
		ScriptedHandler handler = new(_ => new HttpResponseMessage(status));
		HttpPhotonImageListingSource source = new(new FakeHttpClientFactory(handler));

		PhotonImageListingResult result = await source.ListImagesAsync(ChannelBaseUrl, CancellationToken.None);

		Assert.Equal(PhotonImageListingKind.Indeterminate, result.Kind);
		Assert.Contains(expectedInError, result.Error!, StringComparison.Ordinal);
		Assert.Empty(result.Entries);
	}

	/// <summary>A transport failure never reached the server at all -- indeterminate, never absent.</summary>
	[Fact]
	public async Task ListImagesAsync_TransportFailure_IsIndeterminate()
	{
		ScriptedHandler handler = new(_ => throw new HttpRequestException("connection reset by peer"));
		HttpPhotonImageListingSource source = new(new FakeHttpClientFactory(handler));

		PhotonImageListingResult result = await source.ListImagesAsync(ChannelBaseUrl, CancellationToken.None);

		Assert.Equal(PhotonImageListingKind.Indeterminate, result.Kind);
		Assert.Contains("connection reset by peer", result.Error!, StringComparison.Ordinal);
	}

	/// <summary>A timeout (a TaskCanceledException with no caller cancellation) is indeterminate, never absent.</summary>
	[Fact]
	public async Task ListImagesAsync_Timeout_IsIndeterminate()
	{
		ScriptedHandler handler = new(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
		HttpPhotonImageListingSource source = new(new FakeHttpClientFactory(handler));

		PhotonImageListingResult result = await source.ListImagesAsync(ChannelBaseUrl, CancellationToken.None);

		Assert.Equal(PhotonImageListingKind.Indeterminate, result.Kind);
		Assert.Contains("timed out", result.Error!, StringComparison.Ordinal);
	}

	/// <summary>
	/// An autoindex document over <see cref="HttpPhotonImageListingSource.MaxListingBytes"/>
	/// was SERVED and rejected on this type's own byte bound -- issue #1834's distinction,
	/// applied here: a size-cap rejection is indeterminate and names the cap, never an
	/// absent channel.
	/// </summary>
	[Fact]
	public async Task ListImagesAsync_ListingOverSizeCap_IsIndeterminateAndNamesTheCap()
	{
		byte[] oversized = new byte[HttpPhotonImageListingSource.MaxListingBytes + 1024];
		ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(oversized) });
		HttpPhotonImageListingSource source = new(new FakeHttpClientFactory(handler));

		PhotonImageListingResult result = await source.ListImagesAsync(ChannelBaseUrl, CancellationToken.None);

		Assert.Equal(PhotonImageListingKind.Indeterminate, result.Kind);
		Assert.Contains(
			HttpPhotonImageListingSource.MaxListingBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
			result.Error!,
			StringComparison.Ordinal);
	}

	/// <summary>
	/// A listing that parsed but holds no recognized image file is <c>Found</c> with an
	/// EMPTY entry list -- a third fact again, distinct from both Absent and
	/// Indeterminate: the channel exists and is genuinely empty of images.
	/// </summary>
	[Fact]
	public async Task ListImagesAsync_ParsedListingWithNoImages_IsFoundWithNoEntries()
	{
		ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("""<html><body><a href="../">Parent Directory</a></body></html>"""),
		});
		HttpPhotonImageListingSource source = new(new FakeHttpClientFactory(handler));

		PhotonImageListingResult result = await source.ListImagesAsync(ChannelBaseUrl, CancellationToken.None);

		Assert.Equal(PhotonImageListingKind.Found, result.Kind);
		Assert.Empty(result.Entries);
		Assert.Null(result.Error);
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
