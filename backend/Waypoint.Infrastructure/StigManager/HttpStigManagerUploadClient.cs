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
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Waypoint.Core.StigManager;

namespace Waypoint.Infrastructure.StigManager;

/// <summary>
/// Real network implementation of <see cref="IStigManagerUploadClient"/> (issue #311):
/// a multipart POST of the CKL to STIG Manager's own checklist-import route
/// (<c>/collections/{collection}/checklists</c>, importOptions=merge) for upload, and a
/// GET of the installed <c>/stigs</c> list for enrichment -- the same two calls the
/// sibling <c>vmware-stig-docker</c> module.benchmarks.ps1's <c>Get-StigManagerStigs</c>
/// and the watcher's own cargo upload make, reimplemented directly against the REST API
/// instead of shelling out to the Node watcher process (issue #25's "replacing the
/// watcher-directory pattern with direct API upload where practical"). Like
/// <see cref="HttpStigManagerProbe"/>, this class has no lab STIG Manager instance to
/// exercise in CI -- integration tests substitute <see cref="IStigManagerUploadClient"/>;
/// verifying the real HTTP path is a manual owner step.
/// </summary>
public sealed partial class HttpStigManagerUploadClient : IStigManagerUploadClient
{
	private readonly IHttpClientFactory _httpClientFactory;
	private readonly ILogger<HttpStigManagerUploadClient> _logger;

	public HttpStigManagerUploadClient(IHttpClientFactory httpClientFactory, ILogger<HttpStigManagerUploadClient> logger)
	{
		ArgumentNullException.ThrowIfNull(httpClientFactory);
		ArgumentNullException.ThrowIfNull(logger);
		_httpClientFactory = httpClientFactory;
		_logger = logger;
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "STIG Manager CKL upload failed: {Stage}")]
	private partial void LogUploadFailed(Exception exception, string stage);

	[LoggerMessage(Level = LogLevel.Warning, Message = "STIG Manager benchmark metadata enrichment skipped: {Reason}")]
	private partial void LogEnrichmentSkipped(string reason);

	public async Task<StigManagerUploadResult> UploadCklAsync(
		ResolvedStigManagerConnection connection, string? clientSecret, string cklPath, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(connection);
		ArgumentException.ThrowIfNullOrWhiteSpace(cklPath);

		HttpClient client = _httpClientFactory.CreateClient(nameof(HttpStigManagerUploadClient));

		string? accessToken;
		try
		{
			accessToken = await StigManagerAuth.AcquireTokenAsync(client, connection, clientSecret, cancellationToken).ConfigureAwait(false);
		}
		catch (HttpRequestException exception)
		{
			LogUploadFailed(exception, "contacting the OIDC authority");
			return new StigManagerUploadResult(StigManagerUploadOutcome.Failed, "The OIDC authority could not be reached.");
		}
		catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
		{
			LogUploadFailed(exception, "contacting the OIDC authority (timed out)");
			return new StigManagerUploadResult(StigManagerUploadOutcome.Failed, "Request to the OIDC authority timed out.");
		}

		if (accessToken is null)
		{
			return new StigManagerUploadResult(StigManagerUploadOutcome.Failed, "The OIDC authority did not issue an access token for the configured client.");
		}

		try
		{
			using MultipartFormDataContent form = new();
			await using FileStream fileStream = File.OpenRead(cklPath);
			using StreamContent fileContent = new(fileStream);
			fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");
			form.Add(fileContent, "importFile", Path.GetFileName(cklPath));
			form.Add(new StringContent("merge"), "importOptions");

			Uri uploadUri = StigManagerAuth.CombineUri(connection.Endpoint, $"collections/{Uri.EscapeDataString(connection.Collection)}/checklists");
			using HttpRequestMessage request = new(HttpMethod.Post, uploadUri) { Content = form };
			request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

			using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

			if (response.StatusCode == HttpStatusCode.Conflict)
			{
				return new StigManagerUploadResult(StigManagerUploadOutcome.Conflict, "STIG Manager reported a conflicting asset/checklist for this collection.");
			}

			if (!response.IsSuccessStatusCode)
			{
				return new StigManagerUploadResult(StigManagerUploadOutcome.Failed, $"STIG Manager returned {(int)response.StatusCode}.");
			}

			return new StigManagerUploadResult(StigManagerUploadOutcome.Uploaded, null);
		}
		catch (HttpRequestException exception)
		{
			LogUploadFailed(exception, "posting the checklist");
			return new StigManagerUploadResult(StigManagerUploadOutcome.Failed, "STIG Manager could not be reached to receive the checklist.");
		}
		catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
		{
			LogUploadFailed(exception, "posting the checklist (timed out)");
			return new StigManagerUploadResult(StigManagerUploadOutcome.Failed, "Uploading the checklist timed out.");
		}
		catch (IOException exception)
		{
			LogUploadFailed(exception, "reading the CKL file");
			return new StigManagerUploadResult(StigManagerUploadOutcome.Failed, "The CKL file could not be read for upload.");
		}
	}

	public async Task<StigManagerBenchmarkMetadata> ResolveBenchmarkMetadataAsync(
		ResolvedStigManagerConnection connection, string? clientSecret, string benchmarkId, StigManagerBenchmarkMetadata fallback, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(connection);
		ArgumentNullException.ThrowIfNull(fallback);

		if (string.IsNullOrWhiteSpace(benchmarkId))
		{
			return fallback;
		}

		try
		{
			HttpClient client = _httpClientFactory.CreateClient(nameof(HttpStigManagerUploadClient));
			string? accessToken = await StigManagerAuth.AcquireTokenAsync(client, connection, clientSecret, cancellationToken).ConfigureAwait(false);
			if (accessToken is null)
			{
				LogEnrichmentSkipped("no access token issued");
				return fallback;
			}

			using HttpRequestMessage request = new(HttpMethod.Get, StigManagerAuth.CombineUri(connection.Endpoint, "stigs"));
			request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
			using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				LogEnrichmentSkipped($"STIG Manager returned {(int)response.StatusCode} for /stigs");
				return fallback;
			}

			using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
			foreach (JsonElement stig in document.RootElement.EnumerateArray())
			{
				if (!stig.TryGetProperty("benchmarkId", out JsonElement idElement) || !string.Equals(idElement.GetString(), benchmarkId, StringComparison.Ordinal))
				{
					continue;
				}

				string? title = stig.TryGetProperty("title", out JsonElement titleElement) ? titleElement.GetString() : null;
				(string? version, string? releaseInfo) = ParseRevision(
					stig.TryGetProperty("lastRevisionStr", out JsonElement revisionElement) ? revisionElement.GetString() : null,
					stig.TryGetProperty("lastRevisionDate", out JsonElement dateElement) ? dateElement.GetString() : null);

				return new StigManagerBenchmarkMetadata(
					benchmarkId,
					title ?? fallback.Title,
					releaseInfo ?? fallback.ReleaseInfo,
					version ?? fallback.Version);
			}

			LogEnrichmentSkipped($"benchmark '{benchmarkId}' is not installed in STIG Manager");
			return fallback;
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
		{
			LogUploadFailed(exception, "resolving benchmark metadata");
			return fallback;
		}
	}

	/// <summary>Mirrors <c>ConvertTo-StigReleaseInfo</c>: <c>lastRevisionStr</c> ("V2R3") splits into CKL <c>version</c> ("2") and a composed <c>releaseinfo</c> string; either is null when not derivable, in which case the caller keeps the static fallback value.</summary>
	private static (string? Version, string? ReleaseInfo) ParseRevision(string? revisionStr, string? revisionDate)
	{
		if (string.IsNullOrEmpty(revisionStr))
		{
			return (null, null);
		}

		System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(revisionStr, "^[vV](\\d+)[rR](\\d+)$");
		if (!match.Success)
		{
			return (null, null);
		}

		string version = match.Groups[1].Value;
		string releaseNumber = match.Groups[2].Value;
		string releaseInfo = $"Release: {releaseNumber}";
		if (!string.IsNullOrEmpty(revisionDate) && DateTime.TryParse(revisionDate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime parsedDate))
		{
			releaseInfo += $" Benchmark Date: {parsedDate.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}";
		}

		return (version, releaseInfo);
	}
}
