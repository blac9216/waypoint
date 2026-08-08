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

using System.Net.Http.Json;
using System.Text.Json;
using Waypoint.Core.StigManager;

namespace Waypoint.Infrastructure.StigManager;

/// <summary>
/// The OIDC client-credentials token acquisition <see cref="HttpStigManagerProbe"/>
/// (issue #310) and <see cref="HttpStigManagerUploadClient"/> (issue #311) both need --
/// extracted here rather than duplicated so the two network boundaries share one
/// implementation of the discovery + grant call (mirrors the sibling
/// <c>vmware-stig-docker</c> module.benchmarks.ps1's <c>Get-StigManagerToken</c>, the
/// single resolver its own upload and benchmark-sync paths both use).
/// </summary>
internal static class StigManagerAuth
{
	/// <summary>Discovers the token endpoint from <c>{Authority}/.well-known/openid-configuration</c> and performs the client-credentials grant. Returns null (not throws) when the authority is reachable but issues no token -- the caller reports that as an auth failure, not an unreachable one.</summary>
	public static async Task<string?> AcquireTokenAsync(HttpClient client, ResolvedStigManagerConnection connection, string? clientSecret, CancellationToken cancellationToken)
	{
		Uri discoveryUri = CombineUri(connection.Authority, ".well-known/openid-configuration");
		using JsonDocument discovery = await client.GetFromJsonAsync<JsonDocument>(discoveryUri, cancellationToken).ConfigureAwait(false)
			?? throw new HttpRequestException("The OIDC discovery document was empty.");

		if (!discovery.RootElement.TryGetProperty("token_endpoint", out JsonElement tokenEndpointElement) || tokenEndpointElement.GetString() is not string tokenEndpoint)
		{
			throw new HttpRequestException("The OIDC discovery document did not contain a token_endpoint.");
		}

		Dictionary<string, string> form = new()
		{
			["grant_type"] = "client_credentials",
			["client_id"] = connection.ClientId,
			["scope"] = connection.Scope,
		};
		if (!string.IsNullOrEmpty(clientSecret))
		{
			form["client_secret"] = clientSecret;
		}

		using FormUrlEncodedContent content = new(form);
		using HttpResponseMessage tokenResponse = await client.PostAsync(tokenEndpoint, content, cancellationToken).ConfigureAwait(false);
		if (!tokenResponse.IsSuccessStatusCode)
		{
			return null;
		}

		using JsonDocument tokenDocument = JsonDocument.Parse(await tokenResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
		return tokenDocument.RootElement.TryGetProperty("access_token", out JsonElement accessTokenElement) ? accessTokenElement.GetString() : null;
	}

	public static Uri CombineUri(string baseUri, string relativePath)
	{
		string normalizedBase = baseUri.EndsWith('/') ? baseUri : baseUri + "/";
		return new Uri(new Uri(normalizedBase), relativePath);
	}
}
