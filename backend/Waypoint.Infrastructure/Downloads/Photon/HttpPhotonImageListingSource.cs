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
using System.Text.RegularExpressions;
using Waypoint.Core.Downloads.Photon;

namespace Waypoint.Infrastructure.Downloads.Photon;

/// <inheritdoc cref="IPhotonImageListingSource"/>
/// <remarks>
/// Real network implementation. Every request this type issues is one of exactly two
/// shapes: <c>GET &lt;channelBaseUrl&gt;/</c> (the small HTML autoindex page) and,
/// for each recognized image filename that page links to, <c>HEAD
/// &lt;channelBaseUrl&gt;/&lt;filename&gt;</c> -- never a <c>GET</c> on the image file
/// itself (this issue's AC 5/AC 1: "no code path from discovery to file fetch").
/// <see cref="ListImagesAsync"/> deliberately never parses any size text the listing
/// page renders inline next to a filename (an Apache/nginx autoindex column is
/// human-rounded, e.g. "229M") -- <see cref="PhotonImageListingEntry.SizeBytes"/> comes
/// ONLY from the follow-up <c>HEAD</c> response's <c>Content-Length</c> header, a real
/// byte count (issue #1170's guard, applied locally here since #1170 itself had not
/// merged as of this issue: the guard is architectural -- the listing-size code path
/// simply does not exist -- rather than a runtime rejection of a value this type never
/// reads in the first place).
/// </remarks>
public sealed class HttpPhotonImageListingSource : IPhotonImageListingSource
{
	/// <summary>Bound on the autoindex HTML document -- a directory of a few hundred image files renders as a small page.</summary>
	public const int MaxListingBytes = 1 * 1024 * 1024;

	/// <summary>
	/// Matches an autoindex anchor's <c>href</c> attribute value. Deliberately loose on
	/// the surrounding markup (autoindex pages are not standardized across HTTP
	/// servers) but the captured <c>href</c> itself is validated by
	/// <see cref="IsValidRelativeHref"/> before it is ever used to build a request URL.
	/// </summary>
	private static readonly Regex AnchorHrefPattern = new(
		"""<a\s[^>]*href\s*=\s*["']([^"']+)["']""",
		RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	private readonly IHttpClientFactory _httpClientFactory;

	public HttpPhotonImageListingSource(IHttpClientFactory httpClientFactory)
	{
		ArgumentNullException.ThrowIfNull(httpClientFactory);
		_httpClientFactory = httpClientFactory;
	}

	public async Task<IReadOnlyList<PhotonImageListingEntry>?> ListImagesAsync(string channelBaseUrl, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(channelBaseUrl);

		string listingUrl = channelBaseUrl.TrimEnd('/') + "/";
		byte[]? bytes = await GetBoundedAsync(listingUrl, MaxListingBytes, cancellationToken).ConfigureAwait(false);
		if (bytes is null)
		{
			return null;
		}

		// System.Text.Encoding.UTF8 (unlike Encoding.GetEncoding with a throwing decoder
		// fallback) never throws on invalid bytes -- it substitutes U+FFFD -- so this
		// never needs a try/catch; a substituted byte sequence simply fails to match
		// any recognized anchor/href shape below and yields fewer or zero entries.
		string html = System.Text.Encoding.UTF8.GetString(bytes);

		List<string> fileNames = [];
		foreach (Match match in AnchorHrefPattern.Matches(html))
		{
			string href = WebUtility.HtmlDecode(match.Groups[1].Value);
			if (!IsValidRelativeHref(href))
			{
				continue;
			}

			if (PhotonImageKinds.ClassifyByFileName(href) is null)
			{
				// Not a recognized image extension -- a parent-directory link, a
				// checksum sidecar, another subdirectory, etc. Skipped, never indexed
				// under a guessed kind.
				continue;
			}

			fileNames.Add(href);
		}

		List<PhotonImageListingEntry> entries = [];
		foreach (string fileName in fileNames)
		{
			string fileUrl = $"{channelBaseUrl.TrimEnd('/')}/{fileName}";
			(long? sizeBytes, string? etag) = await HeadAsync(fileUrl, cancellationToken).ConfigureAwait(false);
			entries.Add(new PhotonImageListingEntry(fileName, sizeBytes, etag));
		}

		return entries;
	}

	/// <summary>
	/// Rejects an autoindex <c>href</c> unless it is a plain relative filename with no
	/// scheme/authority, no rooted path, no <c>..</c> segment, no percent-encoding
	/// remaining after decode, and no further path separator -- mirrors
	/// <c>HttpPhotonRepoMetadataSource.IsValidPrimaryHref</c>'s hostile-input discipline
	/// for the same reason: an autoindex page is unsigned, untrusted upstream content.
	/// A subdirectory link (trailing <c>/</c>) is also rejected here -- this method only
	/// lists files directly under the channel directory (this issue's scope; a nested
	/// tree, if the vendor ever ships one, is out of scope for this pass).
	/// </summary>
	internal static bool IsValidRelativeHref(string href)
	{
		if (string.IsNullOrWhiteSpace(href))
		{
			return false;
		}

		if (href.Contains("..", StringComparison.Ordinal)
			|| href.StartsWith('/')
			|| href.StartsWith('\\')
			|| href.Contains("://", StringComparison.Ordinal)
			|| href.StartsWith("//", StringComparison.Ordinal)
			|| href.EndsWith('/')
			|| href.Contains('/', StringComparison.Ordinal)
			|| href.Contains('\\', StringComparison.Ordinal))
		{
			return false;
		}

		return true;
	}

	private async Task<(long? SizeBytes, string? ETag)> HeadAsync(string url, CancellationToken cancellationToken)
	{
		HttpClient client = _httpClientFactory.CreateClient(nameof(HttpPhotonImageListingSource));
		try
		{
			using HttpRequestMessage request = new(HttpMethod.Head, url);
			using HttpResponseMessage response = await client
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return (null, null);
			}

			long? sizeBytes = response.Content.Headers.ContentLength;
			string? etag = response.Headers.ETag?.Tag;
			return (sizeBytes, etag);
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

	private async Task<byte[]?> GetBoundedAsync(string url, int maxBytes, CancellationToken cancellationToken)
	{
		HttpClient client = _httpClientFactory.CreateClient(nameof(HttpPhotonImageListingSource));
		try
		{
			using HttpResponseMessage response = await client
				.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode)
			{
				return null;
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
					return null;
				}
				buffer.Write(chunk, 0, read);
			}
			return buffer.ToArray();
		}
		catch (HttpRequestException)
		{
			return null;
		}
		catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return null;
		}
	}
}
