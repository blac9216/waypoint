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

using System.IO.Compression;
using System.Net;
using System.Text;
using Waypoint.Core.Downloads.Photon;
using Waypoint.Infrastructure.Downloads.Photon;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads.Photon;

/// <summary>
/// <see cref="HttpPhotonRepoMetadataSource"/> against a stubbed transport (the same
/// <see cref="DelegatingHandler"/> pattern <c>HttpManagedToolDepotFetcherTests"/> uses
/// -- no real Photon host is reachable from CI). Every fixture here is INVENTED: a
/// fabricated <c>repomd.xml</c>/<c>primary.xml.gz</c>/<c>photon_versions.json</c> with
/// neutral names, served from <c>https://photon.example.internal/photon</c>, never a
/// real vendor mirror capture. Covers version-branch enumeration, a found repo
/// (revision + package count parsed from the gzipped primary document), and the
/// <c>photon_snapshots</c>-shaped no-repodata classification (a plain 404 on
/// <c>repodata/repomd.xml</c>, never an exception).
/// </summary>
public sealed class HttpPhotonRepoMetadataSourceTests
{
	private const string BaseUrl = "https://photon.example.internal/photon";

	private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : DelegatingHandler
	{
		public List<string> RequestedUrls { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			RequestedUrls.Add(request.RequestUri!.ToString());
			return Task.FromResult(respond(request));
		}
	}

	private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
	{
		public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
	}

	private static byte[] GzipUtf8(string text)
	{
		using MemoryStream output = new();
		using (GZipStream gzip = new(output, CompressionLevel.Fastest, leaveOpen: true))
		{
			byte[] bytes = Encoding.UTF8.GetBytes(text);
			gzip.Write(bytes, 0, bytes.Length);
		}
		return output.ToArray();
	}

	private const string RepomdXml = """
		<?xml version="1.0" encoding="UTF-8"?>
		<repomd xmlns="http://linux.duke.edu/metadata/repo">
		  <revision>1700000000</revision>
		  <data type="primary">
		    <checksum type="sha256">deadbeef</checksum>
		    <location href="repodata/primary.xml.gz"/>
		  </data>
		</repomd>
		""";

	private const string PrimaryXml = """
		<?xml version="1.0" encoding="UTF-8"?>
		<metadata xmlns="http://linux.duke.edu/metadata/common" packages="3">
		  <package type="rpm"><name>fake-pkg-a</name><arch>x86_64</arch></package>
		  <package type="rpm"><name>fake-pkg-b</name><arch>x86_64</arch></package>
		  <package type="rpm"><name>fake-pkg-c</name><arch>x86_64</arch></package>
		</metadata>
		""";

	[Fact]
	public async Task GetVersionBranchesAsync_ParsesBranchesArray()
	{
		ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("""{"branches": ["4.0", "5.0"]}"""),
		});
		HttpPhotonRepoMetadataSource source = new(new FakeHttpClientFactory(handler));

		IReadOnlyList<string>? branches = await source.GetVersionBranchesAsync(BaseUrl, CancellationToken.None);

		Assert.Equal(["4.0", "5.0"], branches);
		Assert.Single(handler.RequestedUrls, url => url.EndsWith("photon_cve_metadata/photon_versions.json", StringComparison.Ordinal));
	}

	[Fact]
	public async Task GetVersionBranchesAsync_MalformedJson_ReturnsNull()
	{
		ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") });
		HttpPhotonRepoMetadataSource source = new(new FakeHttpClientFactory(handler));

		Assert.Null(await source.GetVersionBranchesAsync(BaseUrl, CancellationToken.None));
	}

	[Fact]
	public async Task TryGetRepomdRevisionAndPackageCountAsync_FoundRepo_ParsesRevisionAndCountsPackages()
	{
		ScriptedHandler handler = new(request => request.RequestUri!.ToString().EndsWith("repomd.xml", StringComparison.Ordinal)
			? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RepomdXml) }
			: new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(GzipUtf8(PrimaryXml)) });
		HttpPhotonRepoMetadataSource source = new(new FakeHttpClientFactory(handler));

		PhotonRepomdProbeResult result = await source.TryGetRepomdRevisionAndPackageCountAsync(
			$"{BaseUrl}/5.0/photon_release_5.0_x86_64", CancellationToken.None);

		Assert.Equal(PhotonRepomdProbeKind.Found, result.Kind);
		Assert.Equal("1700000000", result.Revision);
		Assert.Equal(3, result.PackageCount);
		Assert.Equal(2, handler.RequestedUrls.Count);
		Assert.Contains(handler.RequestedUrls, url => url.EndsWith("repodata/repomd.xml", StringComparison.Ordinal));
		Assert.Contains(handler.RequestedUrls, url => url.EndsWith("repodata/primary.xml.gz", StringComparison.Ordinal));
	}

	/// <summary>The photon_snapshots-shaped classification: a plain 404 on repomd.xml, never an exception.</summary>
	[Fact]
	public async Task TryGetRepomdRevisionAndPackageCountAsync_NoRepodata_ReturnsNotFoundClassification()
	{
		ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
		HttpPhotonRepoMetadataSource source = new(new FakeHttpClientFactory(handler));

		PhotonRepomdProbeResult result = await source.TryGetRepomdRevisionAndPackageCountAsync(
			$"{BaseUrl}/5.0/photon_snapshots_5.0_x86_64", CancellationToken.None);

		Assert.Equal(PhotonRepomdProbeKind.NoRepodata, result.Kind);
		Assert.Null(result.Revision);
		Assert.Null(result.PackageCount);
		// Never fetched primary.xml.gz for a repo with no repodata at all.
		Assert.Single(handler.RequestedUrls);
	}

	[Fact]
	public async Task TryGetRepomdRevisionAndPackageCountAsync_ServerError_ReturnsErrorClassification()
	{
		ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
		HttpPhotonRepoMetadataSource source = new(new FakeHttpClientFactory(handler));

		PhotonRepomdProbeResult result = await source.TryGetRepomdRevisionAndPackageCountAsync(
			$"{BaseUrl}/5.0/photon_updates_5.0_x86_64", CancellationToken.None);

		Assert.Equal(PhotonRepomdProbeKind.Error, result.Kind);
		Assert.NotNull(result.Error);
	}
}
