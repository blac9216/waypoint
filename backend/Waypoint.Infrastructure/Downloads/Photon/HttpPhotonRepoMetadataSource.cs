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
using System.Text.RegularExpressions;
using System.Xml;
using Waypoint.Core.Downloads.Photon;

namespace Waypoint.Infrastructure.Downloads.Photon;

/// <inheritdoc cref="IPhotonRepoMetadataSource"/>
/// <remarks>
/// Real network implementation. Every request this type issues is one of exactly three
/// shapes: <c>GET &lt;base&gt;/photon_cve_metadata/photon_versions.json</c> (small JSON),
/// <c>GET &lt;repoBase&gt;/repodata/repomd.xml</c> followed by, only when that document
/// resolves a <c>primary</c> data entry AND that entry's <c>href</c> passes
/// <see cref="IsValidPrimaryHref"/>, <c>GET &lt;repoBase&gt;/&lt;that entry's location&gt;</c>
/// (the compressed <c>primary.xml.gz</c> HEADER document), or a directory-existence probe
/// of <c>&lt;repoBase&gt;</c> and of <c>&lt;repoBase&gt;/repodata/</c> when <c>repomd.xml</c>
/// 404s (to distinguish "directory absent upstream" from "present, no repodata" from
/// "present, repodata mid-regeneration" -- see <see cref="ProbeDirectoryExistsAsync"/>)
/// -- never an actual RPM, ISO, OVA, or any other package/image file (this issue's AC 5).
/// <c>repomd.xml</c> is unsigned upstream (research #1029 finding 2: "Metadata signing:
/// none... repomd.xml lists no signature data entry") so this type derives no trust
/// decision from its content -- it only reads the &lt;revision&gt; and the primary
/// entry's location/size, same untrusted-input discipline
/// <c>EsxPatchStoreMetadataParser</c> applies to its own XML: DTD processing prohibited,
/// no resolver, and every read is bounded. Because <c>repomd.xml</c> is unsigned, its
/// <c>&lt;location href&gt;</c> is treated as hostile input: <see cref="IsValidPrimaryHref"/>
/// rejects anything with a scheme/authority, a rooted path, a <c>..</c> segment, or a
/// filename that is not one of the repomd metadata kinds -- a rejection is a
/// <see cref="PhotonRepomdProbeResult.Failed(string)"/> for that repo, never a fetch.
/// </remarks>
public sealed class HttpPhotonRepoMetadataSource : IPhotonRepoMetadataSource
{
	/// <summary>Bound on the versions.json document and on repomd.xml -- both are small, hand-authored documents upstream.</summary>
	public const int MaxSmallDocumentBytes = 1 * 1024 * 1024;

	/// <summary>Bound on the decompressed primary.xml document -- header-only content for tens of thousands of packages, still finite.</summary>
	public const int MaxPrimaryXmlBytes = 64 * 1024 * 1024;

	/// <summary>
	/// The closed set of repomd metadata document kinds (<c>primary</c>/<c>filelists</c>/
	/// <c>other</c>) crossed with their allowed compression suffixes -- the only shapes a
	/// <c>&lt;location href&gt;</c> filename may take for this type to fetch it.
	/// </summary>
	private static readonly Regex RepomdMetadataFilenamePattern = new(
		@"^(primary|filelists|other)\.(xml|sqlite)(\.(gz|bz2|xz|zst))?$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
		(byte[]? bytes, bool _) = await GetBoundedAsync(url, MaxSmallDocumentBytes, cancellationToken).ConfigureAwait(false);
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
		(byte[]? bytes, HttpStatusCode? notFoundStatus, bool sizeCapExceeded) = await GetBoundedOrNotFoundAsync(
			repomdUrl, MaxSmallDocumentBytes, cancellationToken).ConfigureAwait(false);

		if (notFoundStatus is HttpStatusCode.NotFound)
		{
			// A 404 on repomd.xml alone cannot tell "directory absent upstream" apart from
			// "directory present, no repodata" -- probe the directory itself to decide.
			DirectoryProbe directoryProbe = await ProbeDirectoryExistsAsync(repoBaseUrl, cancellationToken).ConfigureAwait(false);
			if (directoryProbe == DirectoryProbe.Absent)
			{
				return PhotonRepomdProbeResult.Absent;
			}

			if (directoryProbe != DirectoryProbe.Exists)
			{
				return PhotonRepomdProbeResult.Failed(
					$"repodata/repomd.xml was 404 at '{repomdUrl}' and the follow-up directory HEAD on " +
					$"'{repoBaseUrl}' failed at the transport layer, so absent-upstream and " +
					"present-without-repodata cannot be told apart -- reported as a probe error, not as absent.");
			}

			// Issue #1835 Option B: the repo directory exists, but a bare 404 on
			// repomd.xml still cannot tell "this repo genuinely publishes no repodata"
			// (a photon_snapshots-shaped directory, AC 3 of #1509) apart from "upstream
			// is mid-regeneration and repomd.xml is momentarily missing from a repodata/
			// directory that is right there". Corroborate with a HEAD on repodata/
			// itself -- the issue's own "or a successful directory listing" corroboration:
			// only an explicit 404 on repodata/ is positive evidence that this repo has
			// no repodata, and only that evidence may produce a NoRepodata row (which the
			// caller upserts as has_repodata=false, overwriting whatever a previous sweep
			// recorded). repodata/ present, or a probe that never got an answer, is
			// "could not determine" -- a probe error, which the handler skips without
			// upserting, so a previously-Found row keeps its revision and package count
			// instead of being silently downgraded by one transient sweep.
			string repodataDirUrl = $"{repoBaseUrl.TrimEnd('/')}/repodata";
			DirectoryProbe repodataProbe = await ProbeDirectoryExistsAsync(repodataDirUrl, cancellationToken).ConfigureAwait(false);
			return repodataProbe switch
			{
				DirectoryProbe.Absent => PhotonRepomdProbeResult.NotFound,
				DirectoryProbe.Exists => PhotonRepomdProbeResult.Failed(
					$"repodata/repomd.xml was 404 at '{repomdUrl}' while the repodata/ directory at " +
					$"'{repodataDirUrl}/' answered as present -- upstream is regenerating its metadata, so " +
					"whether this repo has repodata cannot be determined on this sweep. Reported as a probe " +
					"error rather than as no-repodata, so a previously-indexed row is not downgraded (#1835)."),
				_ => PhotonRepomdProbeResult.Failed(
					$"repodata/repomd.xml was 404 at '{repomdUrl}' and the follow-up HEAD on " +
					$"'{repodataDirUrl}/' failed at the transport layer, so a genuinely repodata-free repo " +
					"and an upstream mid-regeneration cannot be told apart -- reported as a probe error."),
			};
		}

		if (bytes is null)
		{
			// Issue #1834: an oversized repomd.xml (sizeCapExceeded) is a size-cap
			// rejection, not a generic "unreachable" -- the document was served and
			// read, and rejected on this type's own byte bound, the same distinction
			// the primary.xml.gz path below already makes.
			return sizeCapExceeded
				? PhotonRepomdProbeResult.Failed($"repomd.xml at '{repomdUrl}' exceeded the {MaxSmallDocumentBytes}-byte size cap.")
				: PhotonRepomdProbeResult.Failed($"repomd.xml unreachable at '{repomdUrl}'.");
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

		if (!IsValidPrimaryHref(primaryLocation))
		{
			return PhotonRepomdProbeResult.Failed(
				$"repomd.xml at '{repomdUrl}' declared a rejected primary <location href='{primaryLocation}'> " +
				"(must be a relative repodata/<kind> path with no scheme, no rooted path, no percent-encoding, " +
				"and no '..' segment) -- never fetched.");
		}

		string primaryUrl = $"{repoBaseUrl.TrimEnd('/')}/{primaryLocation.TrimStart('/')}";
		(byte[]? primaryGzBytes, bool primarySizeCapExceeded) = await GetBoundedAsync(primaryUrl, MaxPrimaryXmlBytes, cancellationToken).ConfigureAwait(false);
		if (primarySizeCapExceeded)
		{
			return PhotonRepomdProbeResult.Failed($"primary.xml.gz at '{primaryUrl}' exceeded the {MaxPrimaryXmlBytes}-byte size cap.");
		}

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

	/// <summary>
	/// Rejects a <c>repomd.xml</c> primary <c>&lt;location href&gt;</c> unless it is a
	/// plain relative path under <c>repodata/</c> with no scheme/authority, no rooted
	/// path, no <c>..</c> segment, no percent-encoding at all, and a filename matching
	/// one of the repomd metadata kinds (<see cref="RepomdMetadataFilenamePattern"/>).
	/// The blanket <c>%</c> rejection closes round-2 review note 1: a percent-encoded dot
	/// segment (<c>repodata/%2e%2e/%2e%2e/x/primary.xml.gz</c>) carries no literal
	/// <c>..</c> and would otherwise pass every other check. Real repomd metadata
	/// filenames are hex-digest-prefixed names that never contain a <c>%</c>, so nothing
	/// legitimate is lost by refusing the character outright rather than decoding first
	/// and re-checking. <c>repomd.xml</c> is unsigned
	/// upstream, so this href is hostile input by default -- a value shaped like
	/// <c>../../../other_repo/RPMS/x86_64/some-package.rpm</c> must be refused before any
	/// fetch is attempted, never normalized-and-followed (this issue's AC 5).
	/// </summary>
	internal static bool IsValidPrimaryHref(string href)
	{
		if (string.IsNullOrWhiteSpace(href))
		{
			return false;
		}

		if (href.Contains('%', StringComparison.Ordinal)
			|| href.Contains("..", StringComparison.Ordinal)
			|| href.StartsWith('/')
			|| href.StartsWith('\\')
			|| href.Contains("://", StringComparison.Ordinal)
			|| href.StartsWith("//", StringComparison.Ordinal)
			|| !href.StartsWith("repodata/", StringComparison.Ordinal))
		{
			return false;
		}

		string filename = href[(href.LastIndexOf('/') + 1)..];
		return RepomdMetadataFilenamePattern.IsMatch(filename);
	}

	/// <summary>Outcome of the repo-directory <c>HEAD</c> probe.</summary>
	private enum DirectoryProbe
	{
		/// <summary>The directory answered something other than 404 -- it exists.</summary>
		Exists,

		/// <summary>The directory answered an explicit 404 -- it is gone upstream.</summary>
		Absent,

		/// <summary>The probe never got an answer (transport failure or timeout).</summary>
		TransportError,
	}

	/// <summary>
	/// Distinguishes "the repo directory does not exist upstream" from "the directory
	/// exists but has no <c>repodata/</c>" -- both look identical from a bare 404 on
	/// <c>repodata/repomd.xml</c>. Called twice on that 404 path: once on the repo base
	/// itself, then (only when the repo base exists) on <c>&lt;repoBase&gt;/repodata</c>,
	/// which is the corroboration issue #1835's Option B asks for before a
	/// previously-<c>Found</c> row may be downgraded to <c>has_repodata = false</c>.
	/// A <c>HEAD</c> on the directory: an explicit
	/// 404 is <see cref="DirectoryProbe.Absent"/>; a 2xx or 3xx response (200, a
	/// redirect, ...) is <see cref="DirectoryProbe.Exists"/>; every other status --
	/// a 5xx, or any other non-404 4xx (403, 405, ...) -- and a transport failure or
	/// timeout are ALL <see cref="DirectoryProbe.TransportError"/> (issue #1835: a 5xx
	/// or non-404 error status is server-fault evidence, not existence evidence, and
	/// upserting a <c>NoRepodata</c> row on it would silently downgrade a
	/// previously-healthy row on nothing more than a transient blip -- round-2 review
	/// note 3 already made the same call for an outright transport failure; this
	/// widens it to cover a server-fault status too). Because this classification maps
	/// to <see cref="PhotonRepomdProbeResult.Failed(string)"/> in the caller's switch,
	/// which the job handler skips (never upserted), the previously-indexed row for a
	/// genuinely healthy repo is left untouched by a transient fault.
	/// </summary>
	private async Task<DirectoryProbe> ProbeDirectoryExistsAsync(string directoryUrl, CancellationToken cancellationToken)
	{
		HttpClient client = _httpClientFactory.CreateClient(nameof(HttpPhotonRepoMetadataSource));
		try
		{
			using HttpRequestMessage request = new(HttpMethod.Head, directoryUrl.TrimEnd('/') + "/");
			using HttpResponseMessage response = await client
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

			if (response.StatusCode == HttpStatusCode.NotFound)
			{
				return DirectoryProbe.Absent;
			}

			return response.IsSuccessStatusCode || (int)response.StatusCode is >= 300 and < 400
				? DirectoryProbe.Exists
				: DirectoryProbe.TransportError;
		}
		catch (HttpRequestException)
		{
			return DirectoryProbe.TransportError;
		}
		catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return DirectoryProbe.TransportError;
		}
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

	private async Task<(byte[]? Bytes, bool SizeCapExceeded)> GetBoundedAsync(string url, int maxBytes, CancellationToken cancellationToken)
	{
		(byte[]? bytes, HttpStatusCode? _, bool sizeCapExceeded) = await GetBoundedOrNotFoundAsync(url, maxBytes, cancellationToken).ConfigureAwait(false);
		return (bytes, sizeCapExceeded);
	}

	private async Task<(byte[]? Bytes, HttpStatusCode? NotFoundStatus, bool SizeCapExceeded)> GetBoundedOrNotFoundAsync(
		string url, int maxBytes, CancellationToken cancellationToken)
	{
		HttpClient client = _httpClientFactory.CreateClient(nameof(HttpPhotonRepoMetadataSource));
		try
		{
			using HttpResponseMessage response = await client
				.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

			if (response.StatusCode == HttpStatusCode.NotFound)
			{
				return (null, HttpStatusCode.NotFound, false);
			}

			if (!response.IsSuccessStatusCode)
			{
				return (null, response.StatusCode, false);
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
					return (null, null, true);
				}
				buffer.Write(chunk, 0, read);
			}
			return (buffer.ToArray(), null, false);
		}
		catch (HttpRequestException)
		{
			return (null, null, false);
		}
		catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return (null, null, false);
		}
	}
}
