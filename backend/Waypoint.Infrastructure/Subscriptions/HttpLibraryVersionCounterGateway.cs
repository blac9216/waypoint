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

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Waypoint.Core.Subscriptions;

namespace Waypoint.Infrastructure.Subscriptions;

/// <summary>Base URL for the lib.json version-counter GET (issue #1472). See <see cref="HttpLibraryVersionCounterGateway"/>.</summary>
public sealed class SubscriptionEvaluationOptions
{
	public const string SectionName = "SubscriptionEvaluation";

	/// <summary>
	/// Root URL the content-libraries-lane <c>lib.json</c> lives under; a request for
	/// product <c>P</c> on lane <c>L</c> is issued against
	/// <c>{LibJsonBaseUrl}/{L}/{P}/lib.json</c>. Placeholder default -- the real VCSP
	/// path shape is this issue's stated pending-live verification (no lab instance
	/// this repository can reach in CI).
	/// </summary>
	public string LibJsonBaseUrl { get; set; } = "https://vcsp.example.internal";
}

/// <inheritdoc cref="ILibraryVersionCounterGateway"/>
public sealed partial class HttpLibraryVersionCounterGateway : ILibraryVersionCounterGateway
{
	private readonly IHttpClientFactory _httpClientFactory;
	private readonly IOptions<SubscriptionEvaluationOptions> _options;
	private readonly ILogger<HttpLibraryVersionCounterGateway> _logger;

	public HttpLibraryVersionCounterGateway(
		IHttpClientFactory httpClientFactory, IOptions<SubscriptionEvaluationOptions> options, ILogger<HttpLibraryVersionCounterGateway> logger)
	{
		ArgumentNullException.ThrowIfNull(httpClientFactory);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);
		_httpClientFactory = httpClientFactory;
		_options = options;
		_logger = logger;
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "lib.json version-counter pre-check failed for {Product}/{Lane}: {Stage}")]
	private partial void LogPreCheckFailed(Exception exception, string product, string lane, string stage);

	public async Task<LibraryVersionCounterResult> GetVersionCounterAsync(string product, string lane, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(product);
		ArgumentException.ThrowIfNullOrWhiteSpace(lane);

		HttpClient client = _httpClientFactory.CreateClient(nameof(HttpLibraryVersionCounterGateway));
		string url = $"{_options.Value.LibJsonBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(lane)}/{Uri.EscapeDataString(product)}/lib.json";

		HttpResponseMessage response;
		try
		{
			response = await client.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
		}
		catch (HttpRequestException exception)
		{
			LogPreCheckFailed(exception, product, lane, "contacting the library-mirror host");
			return new LibraryVersionCounterResult(Success: false, VersionCounter: null, exception.Message);
		}
		catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
		{
			LogPreCheckFailed(exception, product, lane, "contacting the library-mirror host (timed out)");
			return new LibraryVersionCounterResult(Success: false, VersionCounter: null, "Request to the library-mirror host timed out.");
		}

		if (!response.IsSuccessStatusCode)
		{
			return new LibraryVersionCounterResult(Success: false, VersionCounter: null, $"lib.json GET returned {(int)response.StatusCode}.");
		}

		try
		{
			await using System.IO.Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
			using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
			if (document.RootElement.TryGetProperty("version", out JsonElement versionElement) && versionElement.TryGetInt64(out long version))
			{
				return new LibraryVersionCounterResult(Success: true, version, Error: null);
			}

			return new LibraryVersionCounterResult(Success: false, VersionCounter: null, "lib.json response had no numeric 'version' field.");
		}
		catch (JsonException exception)
		{
			LogPreCheckFailed(exception, product, lane, "parsing lib.json");
			return new LibraryVersionCounterResult(Success: false, VersionCounter: null, exception.Message);
		}
	}
}
