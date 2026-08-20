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

using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Waypoint.Api.Authentication;

/// <summary>
/// Issue #536: fetches the discovery document the same way
/// <see cref="OpenIdConnectConfigurationRetriever"/> does, but rewrites <c>jwks_uri</c>
/// back onto the backend's internal discovery host BEFORE fetching signing keys —
/// deliberately not a thin wrapper around <see cref="OpenIdConnectConfigurationRetriever.GetAsync(string, IDocumentRetriever, System.Threading.CancellationToken)"/>,
/// because that method fetches the JWKS internally, using the document's own
/// (unrewritten) <c>jwks_uri</c>, before any wrapper gets a chance to intercept it —
/// live-verified: an outer wrapper that patched <c>JwksUri</c> only after that call
/// returned was already too late, the JWKS fetch had already failed by then
/// (<c>Connection refused</c> against the browser-facing host/port).
///
/// The reason this rewrite is needed at all, not just <see cref="JwtBearerOptions.MetadataAddress"/>
/// pointing at the internal address: pinning Keycloak's <c>KC_HOSTNAME</c> (the fix for
/// the issuer mismatch this issue is about) makes Keycloak render EVERY
/// self-referential URL in the discovery document — including <c>jwks_uri</c> — as the
/// browser-facing canonical issuer, regardless of which network path the discovery
/// fetch itself came in on. Keycloak serves byte-identical JWKS content at either path
/// (live-verified: the internal <c>keycloak:8080</c> and browser-facing nginx-proxied
/// URLs return the same key set), so redirecting the key fetch back onto
/// <see cref="_authority"/> is correct, not just a workaround — the discovery
/// document's own `issuer`/other fields (anyone who inspects it directly, e.g. a
/// debugging tool) still faithfully reports Keycloak's true public identity; only the
/// backend's OWN internal key fetch is redirected.
/// </summary>
public sealed class InternalJwksConfigurationRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
{
	private readonly string _authority;

	public InternalJwksConfigurationRetriever(string authority)
	{
		_authority = authority.TrimEnd('/');
	}

	public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
		string address,
		IDocumentRetriever retriever,
		CancellationToken cancel)
	{
		string discoveryDocument = await retriever.GetDocumentAsync(address, cancel).ConfigureAwait(false);
		OpenIdConnectConfiguration configuration = OpenIdConnectConfiguration.Create(discoveryDocument);

		configuration.JwksUri = $"{_authority}/protocol/openid-connect/certs";

		if (!string.IsNullOrEmpty(configuration.JwksUri))
		{
			string keys = await retriever.GetDocumentAsync(configuration.JwksUri, cancel).ConfigureAwait(false);
			configuration.JsonWebKeySet = JsonWebKeySet.Create(keys);
			foreach (SecurityKey key in configuration.JsonWebKeySet.GetSigningKeys())
			{
				configuration.SigningKeys.Add(key);
			}
		}

		return configuration;
	}
}
