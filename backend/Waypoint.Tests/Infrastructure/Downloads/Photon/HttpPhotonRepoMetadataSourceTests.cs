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
/// (revision + package count parsed from the gzipped primary document), the
/// <c>photon_snapshots</c>-shaped no-repodata classification (a 404 on
/// <c>repodata/repomd.xml</c> whose repo directory nonetheless exists, never an
/// exception), the "directory absent upstream" classification (a 404 on both
/// <c>repodata/repomd.xml</c> AND the repo directory itself, round-0 review finding
/// #4), and PR #1791 round-0 review finding #2's guard: a <c>repomd.xml</c> primary
/// <c>&lt;location href&gt;</c> that traverses out of the repo directory (or otherwise
/// fails <see cref="HttpPhotonRepoMetadataSource.IsValidPrimaryHref"/>) is refused
/// before any fetch is attempted, including a permanent regression fixture for the
/// exact <c>.rpm</c>-escaping href the review's mutation proved was followed verbatim.
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

	/// <summary>
	/// The photon_snapshots-shaped classification: a 404 on repomd.xml, but the repo
	/// directory itself exists (a 200 on the directory-existence probe) -- never an
	/// exception.
	/// </summary>
	[Fact]
	public async Task TryGetRepomdRevisionAndPackageCountAsync_NoRepodata_DirectoryExists_ReturnsNotFoundClassification()
	{
		ScriptedHandler handler = new(request => request.RequestUri!.ToString().EndsWith("repomd.xml", StringComparison.Ordinal)
			? new HttpResponseMessage(HttpStatusCode.NotFound)
			: new HttpResponseMessage(HttpStatusCode.OK));
		HttpPhotonRepoMetadataSource source = new(new FakeHttpClientFactory(handler));

		PhotonRepomdProbeResult result = await source.TryGetRepomdRevisionAndPackageCountAsync(
			$"{BaseUrl}/5.0/photon_snapshots_5.0_x86_64", CancellationToken.None);

		Assert.Equal(PhotonRepomdProbeKind.NoRepodata, result.Kind);
		Assert.Null(result.Revision);
		Assert.Null(result.PackageCount);
		// repomd.xml (404) then the directory-existence probe (200) -- never primary.xml.gz.
		Assert.Equal(2, handler.RequestedUrls.Count);
	}

	/// <summary>
	/// Round-0 review finding #4: a repo directory that does not exist upstream at all
	/// (404 on both repomd.xml AND the directory-existence probe) is a distinct outcome
	/// from "photon_snapshots-shaped, no repodata" -- the caller must not index it.
	/// </summary>
	[Fact]
	public async Task TryGetRepomdRevisionAndPackageCountAsync_DirectoryAbsentUpstream_ReturnsAbsentClassification()
	{
		ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
		HttpPhotonRepoMetadataSource source = new(new FakeHttpClientFactory(handler));

		PhotonRepomdProbeResult result = await source.TryGetRepomdRevisionAndPackageCountAsync(
			$"{BaseUrl}/5.0/photon_debuginfo_5.0_aarch64", CancellationToken.None);

		Assert.Equal(PhotonRepomdProbeKind.Absent, result.Kind);
		Assert.Null(result.Revision);
		Assert.Null(result.PackageCount);
		Assert.Null(result.Error);
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

	private static string RepomdXmlWithHref(string href) => $"""
		<?xml version="1.0" encoding="UTF-8"?>
		<repomd xmlns="http://linux.duke.edu/metadata/repo">
		  <revision>1700000000</revision>
		  <data type="primary">
		    <checksum type="sha256">deadbeef</checksum>
		    <location href="{href}"/>
		  </data>
		</repomd>
		""";

	/// <summary>
	/// Permanent regression fixture for round-0 review finding #2's mutation: a
	/// <c>repomd.xml</c> primary <c>&lt;location href&gt;</c> that escapes the probed
	/// repo directory into a real package file must be refused -- a
	/// <see cref="PhotonRepomdProbeKind.Error"/> result, never a fetch of that URL.
	/// </summary>
	[Fact]
	public async Task TryGetRepomdRevisionAndPackageCountAsync_TraversingHref_RefusesAndNeverFetches()
	{
		const string traversingHref = "../../../photon_release_5.0_x86_64/RPMS/x86_64/fake-package-1.0-1.x86_64.rpm";
		ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(RepomdXmlWithHref(traversingHref)),
		});
		HttpPhotonRepoMetadataSource source = new(new FakeHttpClientFactory(handler));

		PhotonRepomdProbeResult result = await source.TryGetRepomdRevisionAndPackageCountAsync(
			$"{BaseUrl}/5.0/photon_release_5.0_x86_64", CancellationToken.None);

		Assert.Equal(PhotonRepomdProbeKind.Error, result.Kind);
		Assert.NotNull(result.Error);
		Assert.DoesNotContain(handler.RequestedUrls, url => url.EndsWith(".rpm", StringComparison.Ordinal));
		Assert.Single(handler.RequestedUrls, url => url.EndsWith("repomd.xml", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("http://evil.example.internal/repodata/primary.xml.gz")]
	[InlineData("//evil.example.internal/repodata/primary.xml.gz")]
	[InlineData("/repodata/primary.xml.gz")]
	[InlineData("repodata/../../../etc/passwd")]
	[InlineData("repodata/primary.rpm")]
	[InlineData("repodata/../primary.xml.gz")]
	[InlineData("other/primary.xml.gz")]
	// Round-2 review note 1: percent-encoded dot segments carry no literal ".." .
	[InlineData("repodata/%2e%2e/%2e%2e/photon_release_5.0_x86_64/repodata/primary.xml.gz")]
	[InlineData("repodata/%2E%2E/primary.xml.gz")]
	[InlineData("repodata/%2f%2e%2e%2fprimary.xml.gz")]
	public async Task TryGetRepomdRevisionAndPackageCountAsync_RejectedHref_RefusesAndNeverFetches(string rejectedHref)
	{
		ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(RepomdXmlWithHref(rejectedHref)),
		});
		HttpPhotonRepoMetadataSource source = new(new FakeHttpClientFactory(handler));

		PhotonRepomdProbeResult result = await source.TryGetRepomdRevisionAndPackageCountAsync(
			$"{BaseUrl}/5.0/photon_release_5.0_x86_64", CancellationToken.None);

		Assert.Equal(PhotonRepomdProbeKind.Error, result.Kind);
		// Exactly one request, and it is the repomd.xml GET: the rejected href is never fetched,
		// not even in a form the transport would normalize (round-2 review note 1).
		Assert.Single(handler.RequestedUrls);
		Assert.EndsWith("repodata/repomd.xml", handler.RequestedUrls[0], StringComparison.Ordinal);
	}

	/// <summary>
	/// Round-2 review note 1: <c>repodata/%2e%2e/%2e%2e/x/primary.xml.gz</c> contains no
	/// literal <c>..</c>, is <c>repodata/</c>-prefixed, and ends in a valid metadata
	/// filename -- it passed every pre-note check. The guard now refuses any <c>%</c> in
	/// an href outright (repomd metadata filenames never contain one), so the encoded
	/// traversal is a probe error and NOTHING beyond <c>repomd.xml</c> is ever fetched.
	/// </summary>
	[Fact]
	public async Task TryGetRepomdRevisionAndPackageCountAsync_PercentEncodedTraversingHref_RefusesAndNeverFetches()
	{
		const string encodedTraversingHref = "repodata/%2e%2e/%2e%2e/photon_release_5.0_x86_64/repodata/primary.xml.gz";
		ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(RepomdXmlWithHref(encodedTraversingHref)),
		});
		HttpPhotonRepoMetadataSource source = new(new FakeHttpClientFactory(handler));

		PhotonRepomdProbeResult result = await source.TryGetRepomdRevisionAndPackageCountAsync(
			$"{BaseUrl}/5.0/photon_release_5.0_x86_64", CancellationToken.None);

		Assert.Equal(PhotonRepomdProbeKind.Error, result.Kind);
		Assert.NotNull(result.Error);
		// The repomd.xml GET is the ONLY request the source is allowed to have made.
		Assert.Single(handler.RequestedUrls);
		Assert.EndsWith("repodata/repomd.xml", handler.RequestedUrls[0], StringComparison.Ordinal);
		Assert.False(HttpPhotonRepoMetadataSource.IsValidPrimaryHref(encodedTraversingHref));
	}

	/// <summary>
	/// Round-2 review note 3: a transport failure on the directory <c>HEAD</c> probe used
	/// to degrade to <c>Absent</c>, which (with the round-2 finding-1 <c>indexed == 0</c>
	/// gate) would let a transient blip be counted as "the repo is gone upstream". It is
	/// a probe <see cref="PhotonRepomdProbeKind.Error"/> instead: unknown, not absent.
	/// </summary>
	[Fact]
	public async Task TryGetRepomdRevisionAndPackageCountAsync_DirectoryProbeTransportFailure_IsAnErrorNotAbsent()
	{
		ScriptedHandler handler = new(request => request.Method == HttpMethod.Head
			? throw new HttpRequestException("simulated transport failure")
			: new HttpResponseMessage(HttpStatusCode.NotFound));
		HttpPhotonRepoMetadataSource source = new(new FakeHttpClientFactory(handler));

		PhotonRepomdProbeResult result = await source.TryGetRepomdRevisionAndPackageCountAsync(
			$"{BaseUrl}/5.0/photon_debuginfo_5.0_aarch64", CancellationToken.None);

		Assert.Equal(PhotonRepomdProbeKind.Error, result.Kind);
		Assert.NotNull(result.Error);
		Assert.Contains("transport", result.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData("repodata/primary.xml.gz")]
	[InlineData("repodata/filelists.xml.gz")]
	[InlineData("repodata/other.xml.zst")]
	[InlineData("repodata/primary.sqlite.bz2")]
	public void IsValidPrimaryHref_AcceptsRepodataMetadataKinds(string href) =>
		Assert.True(HttpPhotonRepoMetadataSource.IsValidPrimaryHref(href));

	/// <summary>Round-0 review finding #3, note 3: a size-cap hit must be reported distinctly from "unreachable".</summary>
	[Fact]
	public async Task TryGetRepomdRevisionAndPackageCountAsync_PrimaryXmlGzOverSizeCap_ReportsSizeCapRejection()
	{
		byte[] oversizedContent = new byte[HttpPhotonRepoMetadataSource.MaxPrimaryXmlBytes + 1];
		ScriptedHandler handler = new(request => request.RequestUri!.ToString().EndsWith("repomd.xml", StringComparison.Ordinal)
			? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RepomdXml) }
			: new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(oversizedContent) });
		HttpPhotonRepoMetadataSource source = new(new FakeHttpClientFactory(handler));

		PhotonRepomdProbeResult result = await source.TryGetRepomdRevisionAndPackageCountAsync(
			$"{BaseUrl}/5.0/photon_release_5.0_x86_64", CancellationToken.None);

		Assert.Equal(PhotonRepomdProbeKind.Error, result.Kind);
		Assert.NotNull(result.Error);
		Assert.Contains("size cap", result.Error, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("unreachable", result.Error, StringComparison.OrdinalIgnoreCase);
	}
}
