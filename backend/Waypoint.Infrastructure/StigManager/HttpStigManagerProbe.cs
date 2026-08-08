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

using Microsoft.Extensions.Logging;
using Waypoint.Core.StigManager;

namespace Waypoint.Infrastructure.StigManager;

/// <summary>
/// The real network implementation of <see cref="IStigManagerProbe"/>: OIDC
/// client-credentials discovery + token request (mirroring the sibling
/// <c>vmware-stig-docker</c> module.benchmarks.ps1's <c>Get-StigManagerToken</c> /
/// <c>Invoke-StigManagerApi</c>), then one GET against STIG Manager's own
/// <c>/api-version</c> to confirm the token authenticates -- no asset, collection
/// membership, or upload is created, satisfying issue #310's "without side effects" AC.
/// This class is exercised in integration tests only through <see cref="IStigManagerProbe"/>
/// substitutes (see that interface's doc comment) -- there is no lab STIG Manager
/// instance this repository can reach in CI, so a live run of this exact code path is a
/// manual owner step against a real instance.
/// </summary>
public sealed partial class HttpStigManagerProbe : IStigManagerProbe
{
	private readonly IHttpClientFactory _httpClientFactory;
	private readonly ILogger<HttpStigManagerProbe> _logger;

	public HttpStigManagerProbe(IHttpClientFactory httpClientFactory, ILogger<HttpStigManagerProbe> logger)
	{
		ArgumentNullException.ThrowIfNull(httpClientFactory);
		ArgumentNullException.ThrowIfNull(logger);
		_httpClientFactory = httpClientFactory;
		_logger = logger;
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "STIG Manager reachability check failed: {Stage}")]
	private partial void LogProbeFailed(Exception exception, string stage);

	public async Task<StigManagerProbeResult> ProbeAsync(ResolvedStigManagerConnection connection, string? clientSecret, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(connection);
		HttpClient client = _httpClientFactory.CreateClient(nameof(HttpStigManagerProbe));

		string? accessToken;
		try
		{
			accessToken = await StigManagerAuth.AcquireTokenAsync(client, connection, clientSecret, cancellationToken).ConfigureAwait(false);
		}
		catch (HttpRequestException exception)
		{
			LogProbeFailed(exception, "contacting the OIDC authority");
			return new StigManagerProbeResult(Reachable: false, AuthOk: false, ApiVersion: null, Detail: exception.Message);
		}
		catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
		{
			LogProbeFailed(exception, "contacting the OIDC authority (timed out)");
			return new StigManagerProbeResult(Reachable: false, AuthOk: false, ApiVersion: null, Detail: "Request to the OIDC authority timed out.");
		}

		if (accessToken is null)
		{
			return new StigManagerProbeResult(Reachable: true, AuthOk: false, ApiVersion: null, Detail: "The OIDC authority did not issue an access token for the configured client.");
		}

		try
		{
			using HttpRequestMessage request = new(HttpMethod.Get, StigManagerAuth.CombineUri(connection.Endpoint, "api-version"));
			request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
			using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

			if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
			{
				return new StigManagerProbeResult(Reachable: true, AuthOk: false, ApiVersion: null, Detail: $"STIG Manager rejected the token ({(int)response.StatusCode}).");
			}

			if (!response.IsSuccessStatusCode)
			{
				return new StigManagerProbeResult(Reachable: true, AuthOk: false, ApiVersion: null, Detail: $"STIG Manager returned {(int)response.StatusCode}.");
			}

			string apiVersion = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
			return new StigManagerProbeResult(Reachable: true, AuthOk: true, ApiVersion: apiVersion, Detail: null);
		}
		catch (HttpRequestException exception)
		{
			LogProbeFailed(exception, "contacting the API endpoint");
			return new StigManagerProbeResult(Reachable: false, AuthOk: false, ApiVersion: null, Detail: exception.Message);
		}
		catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
		{
			LogProbeFailed(exception, "contacting the API endpoint (timed out)");
			return new StigManagerProbeResult(Reachable: false, AuthOk: false, ApiVersion: null, Detail: "Request to the API endpoint timed out.");
		}
	}

}
