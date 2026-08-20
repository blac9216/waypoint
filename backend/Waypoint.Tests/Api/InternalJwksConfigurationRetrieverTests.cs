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
using Waypoint.Api.Authentication;

namespace Waypoint.Tests.Api;

/// <summary>
/// Issue #536: pins <see cref="InternalJwksConfigurationRetriever"/>'s rewrite of
/// <c>jwks_uri</c> in isolation, using a fake <see cref="IDocumentRetriever"/> instead
/// of a real Keycloak -- proves the retriever redirects the signing-key fetch to the
/// backend's internal <c>Authority</c>-derived host even when the discovery document
/// itself reports a completely different (browser-facing, KC_HOSTNAME-pinned)
/// <c>jwks_uri</c>, and that it fetches from whatever address it is actually given
/// (recording every address requested lets the test assert the JWKS fetch specifically
/// went to the rewritten URL, not the document's original one).
/// </summary>
public sealed class InternalJwksConfigurationRetrieverTests
{
	private static readonly string[] ExpectedRequestedAddresses =
	{
		"http://keycloak:8080/auth/realms/waypoint/.well-known/openid-configuration",
		"http://keycloak:8080/auth/realms/waypoint/protocol/openid-connect/certs",
	};

	private const string DiscoveryDocument = """
		{
			"issuer": "https://localhost:8443/auth/realms/waypoint",
			"jwks_uri": "https://localhost:8443/auth/realms/waypoint/protocol/openid-connect/certs",
			"authorization_endpoint": "https://localhost:8443/auth/realms/waypoint/protocol/openid-connect/auth",
			"token_endpoint": "https://localhost:8443/auth/realms/waypoint/protocol/openid-connect/token"
		}
		""";

	private const string JwksDocument = """
		{
			"keys": [
				{
					"kty": "RSA",
					"use": "sig",
					"kid": "test-key-1",
					"n": "sXchOb0aQ0hUq_hAsKN7XZ0G6z6Q7Sd7l4qjw3lqZk3g",
					"e": "AQAB"
				}
			]
		}
		""";

	[Fact]
	public async Task GetConfigurationAsync_RewritesJwksUriToTheInternalAuthority_BeforeFetchingKeys()
	{
		RecordingDocumentRetriever retriever = new(DiscoveryDocument, JwksDocument);
		InternalJwksConfigurationRetriever sut = new("http://keycloak:8080/auth/realms/waypoint");

		OpenIdConnectConfiguration configuration = await sut.GetConfigurationAsync(
			"http://keycloak:8080/auth/realms/waypoint/.well-known/openid-configuration",
			retriever,
			CancellationToken.None);

		// The rewritten JwksUri on the returned configuration -- NOT the document's own
		// browser-facing value -- is what a caller (and JwtBearerHandler) sees.
		Assert.Equal("http://keycloak:8080/auth/realms/waypoint/protocol/openid-connect/certs", configuration.JwksUri);

		// The discovery document's other self-reported fields are left untouched --
		// only the key-fetch address is redirected, not the document's own reported
		// identity (deliberate: other consumers of this document should still see
		// Keycloak's true public issuer).
		Assert.Equal("https://localhost:8443/auth/realms/waypoint", configuration.Issuer);

		// The actual JWKS fetch went to the REWRITTEN address, not the document's
		// original browser-facing jwks_uri -- this is the core issue #536 assertion:
		// live-verified that without this rewrite, the key fetch targets an address
		// only a browser can reach and the backend gets "Connection refused".
		Assert.Equal(ExpectedRequestedAddresses, retriever.RequestedAddresses);

		Assert.Single(configuration.SigningKeys);
	}

	[Fact]
	public async Task GetConfigurationAsync_TrimsATrailingSlashOnAuthorityBeforeAppending()
	{
		RecordingDocumentRetriever retriever = new(DiscoveryDocument, JwksDocument);
		InternalJwksConfigurationRetriever sut = new("http://keycloak:8080/auth/realms/waypoint/");

		OpenIdConnectConfiguration configuration = await sut.GetConfigurationAsync(
			"http://keycloak:8080/auth/realms/waypoint/.well-known/openid-configuration",
			retriever,
			CancellationToken.None);

		Assert.Equal("http://keycloak:8080/auth/realms/waypoint/protocol/openid-connect/certs", configuration.JwksUri);
	}

	private sealed class RecordingDocumentRetriever : IDocumentRetriever
	{
		private readonly string _discoveryDocument;
		private readonly string _jwksDocument;

		public RecordingDocumentRetriever(string discoveryDocument, string jwksDocument)
		{
			_discoveryDocument = discoveryDocument;
			_jwksDocument = jwksDocument;
		}

		public List<string> RequestedAddresses { get; } = new();

		public Task<string> GetDocumentAsync(string address, CancellationToken cancel)
		{
			RequestedAddresses.Add(address);
			return Task.FromResult(address.EndsWith("certs", StringComparison.Ordinal) ? _jwksDocument : _discoveryDocument);
		}
	}
}
