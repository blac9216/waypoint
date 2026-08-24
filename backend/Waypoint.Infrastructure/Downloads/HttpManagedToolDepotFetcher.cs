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
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Waypoint.Core.Downloads;

namespace Waypoint.Infrastructure.Downloads;

/// <summary>
/// Real network implementation of <see cref="IManagedToolDepotFetcher"/> (issue #39
/// depot-fetch install path): a bearer-token GET of the configured
/// <see cref="ManagedToolOptions.DepotFetchUrlTemplate"/> for the artifact, and the
/// same URL with <c>.sig</c> appended for its detached signature -- mirroring every
/// other install path's artifact/signature pairing
/// (<c>ManagedToolInstallJobHandler</c>'s local-repository and upload paths both
/// expect a sibling <c>.sig</c> file next to the artifact).
///
/// Bounded like <see cref="Waypoint.Infrastructure.StigManager.HttpStigManagerUploadClient"/>:
/// a per-call linked <see cref="CancellationTokenSource"/> caps the whole fetch
/// (both legs) at <see cref="ManagedToolOptions.DepotFetchTimeout"/>, and the
/// response body is copied to disk with an explicit running-byte-count check against
/// <see cref="ManagedToolOptions.DepotFetchMaxBytes"/> -- <c>Content-Length</c> is
/// untrusted (a depot could omit it or lie), so the cap is enforced on the actual
/// bytes read, not the declared header.
///
/// Token handling: <paramref name="depotToken"/> arrives already decrypted by the
/// caller (<c>ManagedToolInstallJobHandler</c>, via <c>ICredentialSecretStore</c>,
/// which has already registered it with <c>ISecretRedactor</c> for the duration of
/// the call) -- this type only ever places it in the <c>Authorization</c> header,
/// never in a URL, a log line, or a <see cref="ManagedToolDepotFetchResult.FailureReason"/>.
/// </summary>
public sealed partial class HttpManagedToolDepotFetcher : IManagedToolDepotFetcher
{
	private readonly IHttpClientFactory _httpClientFactory;
	private readonly IOptions<ManagedToolOptions> _options;
	private readonly ILogger<HttpManagedToolDepotFetcher> _logger;

	public HttpManagedToolDepotFetcher(
		IHttpClientFactory httpClientFactory, IOptions<ManagedToolOptions> options, ILogger<HttpManagedToolDepotFetcher> logger)
	{
		ArgumentNullException.ThrowIfNull(httpClientFactory);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);
		_httpClientFactory = httpClientFactory;
		_options = options;
		_logger = logger;
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "Depot-fetch tool-install failed: {Stage}")]
	private partial void LogFetchFailed(Exception exception, string stage);

	public async Task<ManagedToolDepotFetchResult> FetchAsync(
		string depotToken, string? version, string destinationDirectory, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(depotToken);
		ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

		ManagedToolOptions options = _options.Value;
		string? urlTemplate = options.DepotFetchUrlTemplate;
		if (string.IsNullOrWhiteSpace(urlTemplate))
		{
			return ManagedToolDepotFetchResult.Failure(
				ManagedToolDepotFetchFailureKind.Other,
				"No depot-fetch URL is configured for this appliance (ManagedTool:DepotFetchUrlTemplate). An operator must configure it before the depot-fetch install path can run.");
		}

		string artifactUrl = version is null ? urlTemplate : urlTemplate.Replace("{version}", version, StringComparison.Ordinal);
		if (!Uri.TryCreate(artifactUrl, UriKind.Absolute, out Uri? artifactUri))
		{
			return ManagedToolDepotFetchResult.Failure(
				ManagedToolDepotFetchFailureKind.Other, "The configured depot-fetch URL is not a valid absolute URI.");
		}

		Uri signatureUri = new(artifactUri.ToString() + ".sig");

		Directory.CreateDirectory(destinationDirectory);
		string artifactPath = Path.Combine(destinationDirectory, $"{Guid.NewGuid():N}-vcf-download-tool");
		string signaturePath = artifactPath + ".sig";

		using CancellationTokenSource callTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		callTimeout.CancelAfter(options.DepotFetchTimeout);

		HttpClient client = _httpClientFactory.CreateClient(nameof(HttpManagedToolDepotFetcher));

		ManagedToolDepotFetchResult artifactResult = await DownloadOneAsync(
			client, artifactUri, depotToken, artifactPath, options.DepotFetchMaxBytes, cancellationToken, callTimeout.Token).ConfigureAwait(false);
		if (!artifactResult.Succeeded)
		{
			CleanUp(artifactPath, signaturePath);
			return artifactResult;
		}

		ManagedToolDepotFetchResult signatureResult = await DownloadOneAsync(
			client, signatureUri, depotToken, signaturePath, options.DepotFetchMaxBytes, cancellationToken, callTimeout.Token).ConfigureAwait(false);
		if (!signatureResult.Succeeded)
		{
			CleanUp(artifactPath, signaturePath);
			return signatureResult;
		}

		return ManagedToolDepotFetchResult.Success(artifactPath, signaturePath);
	}

	private async Task<ManagedToolDepotFetchResult> DownloadOneAsync(
		HttpClient client, Uri uri, string depotToken, string destinationPath, long maxBytes,
		CancellationToken callerToken, CancellationToken callTimeoutToken)
	{
		try
		{
			using HttpRequestMessage request = new(HttpMethod.Get, uri);
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", depotToken);

			using HttpResponseMessage response = await client
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, callTimeoutToken)
				.ConfigureAwait(false);

			if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
			{
				return ManagedToolDepotFetchResult.Failure(
					ManagedToolDepotFetchFailureKind.AuthFailure,
					$"The depot rejected the depot-token credential ({(int)response.StatusCode}).");
			}

			if (!response.IsSuccessStatusCode)
			{
				return ManagedToolDepotFetchResult.Failure(
					ManagedToolDepotFetchFailureKind.Other, $"The depot returned {(int)response.StatusCode} for '{uri.AbsolutePath}'.");
			}

			await using Stream responseStream = await response.Content.ReadAsStreamAsync(callTimeoutToken).ConfigureAwait(false);
			await using FileStream fileStream = File.Create(destinationPath);

			byte[] buffer = new byte[81920];
			long total = 0;
			int read;
			while ((read = await responseStream.ReadAsync(buffer, callTimeoutToken).ConfigureAwait(false)) > 0)
			{
				total += read;
				if (total > maxBytes)
				{
					return ManagedToolDepotFetchResult.Failure(
						ManagedToolDepotFetchFailureKind.TooLarge,
						$"The depot response for '{uri.AbsolutePath}' exceeded the {maxBytes}-byte cap and was aborted.");
				}

				await fileStream.WriteAsync(buffer.AsMemory(0, read), callTimeoutToken).ConfigureAwait(false);
			}

			return ManagedToolDepotFetchResult.Success(destinationPath, destinationPath);
		}
		catch (HttpRequestException exception)
		{
			LogFetchFailed(exception, $"contacting the depot for '{uri.AbsolutePath}'");
			return ManagedToolDepotFetchResult.Failure(ManagedToolDepotFetchFailureKind.Unreachable, "The depot could not be reached.");
		}
		catch (TaskCanceledException exception) when (!callerToken.IsCancellationRequested)
		{
			LogFetchFailed(exception, $"fetching '{uri.AbsolutePath}' (timed out)");
			return ManagedToolDepotFetchResult.Failure(ManagedToolDepotFetchFailureKind.Unreachable, "The depot-fetch request timed out.");
		}
		catch (IOException exception)
		{
			LogFetchFailed(exception, $"writing '{destinationPath}' to disk");
			return ManagedToolDepotFetchResult.Failure(ManagedToolDepotFetchFailureKind.Other, "The fetched artifact could not be written to disk.");
		}
	}

	private static void CleanUp(string artifactPath, string signaturePath)
	{
		try
		{
			if (File.Exists(artifactPath))
			{
				File.Delete(artifactPath);
			}

			if (File.Exists(signaturePath))
			{
				File.Delete(signaturePath);
			}
		}
		catch (IOException)
		{
			// Best-effort cleanup of a partial download; a stray temp file under the
			// staging directory is not a correctness issue (never activated, never
			// referenced from the ledger row this attempt fails without writing).
		}
	}
}
