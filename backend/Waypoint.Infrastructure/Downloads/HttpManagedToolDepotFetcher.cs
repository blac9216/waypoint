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
/// Real network implementation of <see cref="IManagedToolDepotFetcher"/> (issue #671
/// depot-fetch install path): a bearer-credential GET of three separately configured
/// authenticated locations -- the <c>vcf-download-tool</c> artifact
/// (<see cref="ManagedToolOptions.DepotFetchUrlTemplate"/>), Broadcom's signed
/// product-version catalog (<see cref="ManagedToolOptions.DepotCatalogUrl"/>), and the
/// catalog's detached signature (<see cref="ManagedToolOptions.DepotCatalogSignatureUrl"/>).
/// The vendor does not publish a per-artifact <c>.sig</c> (issue #671's root cause in
/// the prior <c>&lt;artifact&gt;.sig</c>-guessing implementation) -- this type never
/// guesses a URL by string-appending onto the artifact URL.
///
/// The catalog and its signature are written into a staging subdirectory shaped like
/// a local repository root (<see cref="ManagedToolOptions.ProductVersionCatalogPath"/>
/// / <see cref="ManagedToolOptions.ProductVersionCatalogSignaturePath"/> relative to
/// it) so the caller can hand that root straight to
/// <see cref="IManagedToolCatalogVerifier.VerifyAsync"/> -- the exact verifier issue
/// #669 wired in for the local-repository install path -- with no separate
/// connected-mode verification logic.
///
/// Bounded like <see cref="Waypoint.Infrastructure.StigManager.HttpStigManagerUploadClient"/>:
/// a per-call linked <see cref="CancellationTokenSource"/> caps the whole fetch (all
/// three legs) at <see cref="ManagedToolOptions.DepotFetchTimeout"/>, and each
/// response body is copied to disk with an explicit running-byte-count check against
/// <see cref="ManagedToolOptions.DepotFetchMaxBytes"/> -- <c>Content-Length</c> is
/// untrusted (a depot could omit it or lie), so the cap is enforced on the actual
/// bytes read, not the declared header.
///
/// Credential handling: <paramref name="depotToken"/> arrives already decrypted by
/// the caller (<c>ManagedToolInstallJobHandler</c>, via <c>ICredentialSecretStore</c>,
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

		if (string.IsNullOrWhiteSpace(options.DepotFetchUrlTemplate)
			|| string.IsNullOrWhiteSpace(options.DepotCatalogUrl)
			|| string.IsNullOrWhiteSpace(options.DepotCatalogSignatureUrl))
		{
			return ManagedToolDepotFetchResult.Failure(
				ManagedToolDepotFetchFailureKind.Other,
				"Connected depot-fetch metadata is not fully configured for this appliance (ManagedTool:DepotFetchUrlTemplate, ManagedTool:DepotCatalogUrl, ManagedTool:DepotCatalogSignatureUrl are all required). An operator must configure all three before the depot-fetch install path can run.");
		}

		string artifactUrl = version is null
			? options.DepotFetchUrlTemplate
			: options.DepotFetchUrlTemplate.Replace("{version}", version, StringComparison.Ordinal);

		if (!Uri.TryCreate(artifactUrl, UriKind.Absolute, out Uri? artifactUri)
			|| !Uri.TryCreate(options.DepotCatalogUrl, UriKind.Absolute, out Uri? catalogUri)
			|| !Uri.TryCreate(options.DepotCatalogSignatureUrl, UriKind.Absolute, out Uri? catalogSignatureUri))
		{
			return ManagedToolDepotFetchResult.Failure(
				ManagedToolDepotFetchFailureKind.Other, "One of the configured depot-fetch metadata URLs is not a valid absolute URI.");
		}

		Directory.CreateDirectory(destinationDirectory);
		string requestId = Guid.NewGuid().ToString("N");
		string artifactPath = Path.Combine(destinationDirectory, $"{requestId}-vcf-download-tool");
		string repositoryRoot = Path.Combine(destinationDirectory, $"{requestId}-repo");
		string catalogPath = Path.Combine(repositoryRoot, options.ProductVersionCatalogPath.Replace('/', Path.DirectorySeparatorChar));
		string catalogSignaturePath = Path.Combine(repositoryRoot, options.ProductVersionCatalogSignaturePath.Replace('/', Path.DirectorySeparatorChar));

		using CancellationTokenSource callTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		callTimeout.CancelAfter(options.DepotFetchTimeout);

		HttpClient client = _httpClientFactory.CreateClient(nameof(HttpManagedToolDepotFetcher));

		ManagedToolDepotFetchResult artifactResult = await DownloadOneAsync(
			client, artifactUri, depotToken, artifactPath, options.DepotFetchMaxBytes, cancellationToken, callTimeout.Token).ConfigureAwait(false);
		if (!artifactResult.Succeeded)
		{
			CleanUp(artifactPath, repositoryRoot);
			return artifactResult;
		}

		Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
		ManagedToolDepotFetchResult catalogResult = await DownloadOneAsync(
			client, catalogUri, depotToken, catalogPath, options.DepotFetchMaxBytes, cancellationToken, callTimeout.Token).ConfigureAwait(false);
		if (!catalogResult.Succeeded)
		{
			CleanUp(artifactPath, repositoryRoot);
			return catalogResult;
		}

		Directory.CreateDirectory(Path.GetDirectoryName(catalogSignaturePath)!);
		ManagedToolDepotFetchResult signatureResult = await DownloadOneAsync(
			client, catalogSignatureUri, depotToken, catalogSignaturePath, options.DepotFetchMaxBytes, cancellationToken, callTimeout.Token).ConfigureAwait(false);
		if (!signatureResult.Succeeded)
		{
			CleanUp(artifactPath, repositoryRoot);
			return signatureResult;
		}

		return ManagedToolDepotFetchResult.Success(artifactPath, repositoryRoot);
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
					$"The depot rejected the depot-activation-code credential ({(int)response.StatusCode}).");
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

	private static void CleanUp(string artifactPath, string repositoryRoot)
	{
		try
		{
			if (File.Exists(artifactPath))
			{
				File.Delete(artifactPath);
			}

			if (Directory.Exists(repositoryRoot))
			{
				Directory.Delete(repositoryRoot, recursive: true);
			}
		}
		catch (IOException)
		{
			// Best-effort cleanup of a partial download; a stray temp file/directory
			// under the staging directory is not a correctness issue (never activated,
			// never referenced from the ledger row this attempt fails without writing).
		}
	}
}
