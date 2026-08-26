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

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Waypoint.Tests.Support;

/// <summary>
/// Generates throwaway, entirely invented self-signed certificates and keys for
/// issue #753's trust-bundle upload validation tests. Every certificate here is
/// generated fresh in-process for a <c>*.example.internal</c> subject
/// (RFC 2606-style reserved domain, never a real lab host) -- no real CA material,
/// no captured/committed PEM bytes, no lab identifiers. This is the ONLY source of
/// certificate/key PEM text used by this repository's trust tests.
/// </summary>
public static class InventedCertificateFactory
{
	/// <summary>One freshly generated, invented self-signed leaf certificate as PEM text (CERTIFICATE block only, no key).</summary>
	public static string CreateSelfSignedPem(string commonName = "ca.example.internal", int validDays = 3650, DateTimeOffset? notBefore = null)
	{
		using X509Certificate2 cert = CreateSelfSigned(commonName, validDays, notBefore);
		return PemEncode(cert.RawData);
	}

	/// <summary>A two-certificate invented chain (leaf + a second, differently-named self-signed "issuer" certificate) -- both freshly generated, unrelated to any real CA.</summary>
	public static string CreateSelfSignedChainPem(string leafCommonName = "leaf.example.internal", string issuerCommonName = "issuer.example.internal")
	{
		using X509Certificate2 leaf = CreateSelfSigned(leafCommonName, 3650);
		using X509Certificate2 issuer = CreateSelfSigned(issuerCommonName, 3650);
		return PemEncode(leaf.RawData) + "\n" + PemEncode(issuer.RawData);
	}

	/// <summary>An already-expired invented self-signed certificate (NotAfter in the past) for the expiry-rejection test case.</summary>
	public static string CreateExpiredSelfSignedPem(string commonName = "expired.example.internal")
	{
		DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddDays(-30);
		using X509Certificate2 cert = CreateSelfSigned(commonName, validDays: 1, notBefore);
		return PemEncode(cert.RawData);
	}

	/// <summary>An invented, freshly generated RSA private-key PEM block, for the private-key-rejection test case. Never a real key; discarded after the test process exits.</summary>
	public static string CreateInventedPrivateKeyPem()
	{
		using RSA rsa = RSA.Create(2048);
		byte[] der = rsa.ExportPkcs8PrivateKey();
		return "-----BEGIN PRIVATE KEY-----\n" + Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks) + "\n-----END PRIVATE KEY-----\n"; // gitleaks:allow -- PEM marker literal for an in-test freshly generated throwaway key, never a real secret
	}

	private static X509Certificate2 CreateSelfSigned(string commonName, int validDays, DateTimeOffset? notBefore = null)
	{
		using RSA rsa = RSA.Create(2048);
		CertificateRequest request = new($"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		DateTimeOffset start = notBefore ?? DateTimeOffset.UtcNow.AddMinutes(-5);
		return request.CreateSelfSigned(start, start.AddDays(validDays));
	}

	private static string PemEncode(byte[] der) =>
		"-----BEGIN CERTIFICATE-----\n" + Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks) + "\n-----END CERTIFICATE-----\n";
}
