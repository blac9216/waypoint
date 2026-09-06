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
using System.Text.Json;
using System.Xml;
using Waypoint.Core.Downloads.Photon;

namespace Waypoint.Infrastructure.Downloads.Photon;

/// <inheritdoc cref="IPhotonRepoMetadataSource"/>
/// <remarks>
/// Real network implementation. Every request this type issues is one of exactly two
/// shapes: <c>GET &lt;base&gt;/photon_cve_metadata/photon_versions.json</c> (small JSON) or
/// <c>GET &lt;repoBase&gt;/repodata/repomd.xml</c> followed by, only when that document
/// resolves a <c>primary</c> data entry, <c>GET &lt;repoBase&gt;/&lt;that entry's location&gt;</c>
/// (the compressed <c>primary.xml.gz</c> HEADER document) -- never an actual RPM, ISO,
/// OVA, or any other package/image file (this issue's AC 5). <c>repomd.xml</c> is
/// unsigned upstream (research #1029 finding 2: "Metadata signing: none... repomd.xml
/// lists no signature data entry") so this type derives no trust decision from its
/// content -- it only reads the &lt;revision&gt; and the primary entry's location/size,
/// same untrusted-input discipline <c>EsxPatchStoreMetadataParser</c> applies to its own
/// XML: DTD processing prohibited, no resolver, and every read is bounded.
/// </remarks>
public sealed class HttpPhotonRepoMetadataSource : IPhotonRepoMetadataSource
{
	/// <summary>Bound on the versions.json document and on repomd.xml -- both are small, hand-authored documents upstream.</summary>
	public const int MaxSmallDocumentBytes = 1 * 1024 * 1024;

	/// <summary>Bound on the decompressed primary.xml document -- header-only content for tens of thousands of packages, still finite.</summary>
	public const int MaxPrimaryXmlBytes = 64 * 1024 * 1024;

	private static readonly XmlReaderSettings SafeXmlSettings = new()
	{
		DtdProcessing = DtdProcessing.Prohibit,
		XmlResolver = null,
	};

	private readonly IHttpClientFactory _httpClientFactory;

	public HttpPhotonRepoMetadataSource(IHttpClientFactory httpClientFactory)
	{
		ArgumentNullException.ThrowIfNull(httpClientFactory);
		_httpClientFactory = httpClientFactory;
	}

	public async Task<IReadOnlyList<string>?> GetVersionBranchesAsync(string baseUrl, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

		string url = $"{baseUrl.TrimEnd('/')}/photon_cve_metadata/photon_versions.json";
		byte[]? bytes = await GetBoundedAsync(url, MaxSmallDocumentBytes, cancellationToken).ConfigureAwait(false);
		if (bytes is null)
		{
			return null;
		}

		try
		{
			using JsonDocument document = JsonDocument.Parse(bytes);
			if (!document.RootElement.TryGetProperty("branches", out JsonElement branches)
				|| branches.ValueKind != JsonValueKind.Array)
			{
				return null;
			}

			return [.. branches.EnumerateArray()
				.Where(element => element.ValueKind == JsonValueKind.String)
				.Select(element => element.GetString()!)];
		}
		catch (JsonException)
		{
			return null;
		}
	}

	public async Task<PhotonRepomdProbeResult> TryGetRepomdRevisionAndPackageCountAsync(string repoBaseUrl, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(repoBaseUrl);

		string repomdUrl = $"{repoBaseUrl.TrimEnd('/')}/repodata/repomd.xml";
		(byte[]? bytes, HttpStatusCode? notFoundStatus) = await GetBoundedOrNotFoundAsync(
			repomdUrl, MaxSmallDocumentBytes, cancellationToken).ConfigureAwait(false);

		if (notFoundStatus is HttpStatusCode.NotFound)
		{
			return PhotonRepomdProbeResult.NotFound;
		}

		if (bytes is null)
		{
			return PhotonRepomdProbeResult.Failed($"repomd.xml unreachable at '{repomdUrl}'.");
		}

		string? revision;
		string? primaryLocation;
		try
		{
			(revision, primaryLocation) = ParseRepomd(bytes);
		}
		catch (XmlException exception)
		{
			return PhotonRepomdProbeResult.Failed($"repomd.xml at '{repomdUrl}' failed to parse: {exception.Message}");
		}

		if (revision is null || primaryLocation is null)
		{
			return PhotonRepomdProbeResult.Failed($"repomd.xml at '{repomdUrl}' has no usable <revision>/primary <location>.");
		}

		string primaryUrl = $"{repoBaseUrl.TrimEnd('/')}/{primaryLocation.TrimStart('/')}";
		byte[]? primaryGzBytes = await GetBoundedAsync(primaryUrl, MaxPrimaryXmlBytes, cancellationToken).ConfigureAwait(false);
		if (primaryGzBytes is null)
		{
			return PhotonRepomdProbeResult.Failed($"primary.xml.gz unreachable at '{primaryUrl}'.");
		}

		int packageCount;
		try
		{
			packageCount = CountPackages(primaryGzBytes);
		}
		catch (Exception exception) when (exception is XmlException or InvalidDataException)
		{
			return PhotonRepomdProbeResult.Failed($"primary.xml.gz at '{primaryUrl}' failed to parse: {exception.Message}");
		}

		return PhotonRepomdProbeResult.Found(revision, packageCount);
	}

	private static (string? Revision, string? PrimaryLocation) ParseRepomd(byte[] xmlBytes)
	{
		using MemoryStream stream = new(xmlBytes);
		using XmlReader reader = XmlReader.Create(stream, SafeXmlSettings);

		string? revision = null;
		string? primaryLocation = null;
		bool inPrimaryEntry = false;

		while (reader.Read())
		{
			if (reader.NodeType != XmlNodeType.Element)
			{
				continue;
			}

			switch (reader.LocalName)
			{
				case "revision" when revision is null:
					revision = reader.ReadElementContentAsString();
					break;
				case "data":
					inPrimaryEntry = string.Equals(reader.GetAttribute("type"), "primary", StringComparison.Ordinal);
					break;
				case "location" when inPrimaryEntry && primaryLocation is null:
					primaryLocation = reader.GetAttribute("href");
					break;
			}
		}

		return (revision, primaryLocation);
	}

	private static int CountPackages(byte[] gzBytes)
	{
		using MemoryStream compressed = new(gzBytes);
		using GZipStream gzip = new(compressed, CompressionMode.Decompress);
		using MemoryStream decompressed = new();

		byte[] buffer = new byte[81920];
		int total = 0;
		int read;
		while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
		{
			total += read;
			if (total > MaxPrimaryXmlBytes)
			{
				throw new InvalidDataException($"Decompressed primary.xml exceeded the {MaxPrimaryXmlBytes}-byte bound.");
			}
			decompressed.Write(buffer, 0, read);
		}
		decompressed.Position = 0;

		using XmlReader reader = XmlReader.Create(decompressed, SafeXmlSettings);
		int count = 0;
		while (reader.Read())
		{
			if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "package")
			{
				count++;
			}
		}
		return count;
	}

	private async Task<byte[]?> GetBoundedAsync(string url, int maxBytes, CancellationToken cancellationToken)
	{
		(byte[]? bytes, HttpStatusCode? _) = await GetBoundedOrNotFoundAsync(url, maxBytes, cancellationToken).ConfigureAwait(false);
		return bytes;
	}

	private async Task<(byte[]? Bytes, HttpStatusCode? NotFoundStatus)> GetBoundedOrNotFoundAsync(
		string url, int maxBytes, CancellationToken cancellationToken)
	{
		HttpClient client = _httpClientFactory.CreateClient(nameof(HttpPhotonRepoMetadataSource));
		try
		{
			using HttpResponseMessage response = await client
				.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

			if (response.StatusCode == HttpStatusCode.NotFound)
			{
				return (null, HttpStatusCode.NotFound);
			}

			if (!response.IsSuccessStatusCode)
			{
				return (null, response.StatusCode);
			}

			await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
			using MemoryStream buffer = new();
			byte[] chunk = new byte[16384];
			int read;
			int total = 0;
			while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
			{
				total += read;
				if (total > maxBytes)
				{
					return (null, null);
				}
				buffer.Write(chunk, 0, read);
			}
			return (buffer.ToArray(), null);
		}
		catch (HttpRequestException)
		{
			return (null, null);
		}
		catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return (null, null);
		}
	}
}
