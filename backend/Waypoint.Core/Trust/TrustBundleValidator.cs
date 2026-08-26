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
using System.Text;
using System.Text.RegularExpressions;

namespace Waypoint.Core.Trust;

/// <summary>
/// Validates an untrusted CA certificate/chain upload before any part of it is
/// persisted (issue #753 AC "Invalid, expired, oversized, duplicate, private-key-
/// bearing, or malformed uploads fail safely"). Pure, no I/O, no database access -- a
/// duplicate check against EXISTING bundles is intentionally NOT this class's job (it
/// has no repository access); <c>TrustBundleService</c> composes this validator's
/// per-upload result with a repository fingerprint lookup for that check.
///
/// Every failure path returns a <see cref="TrustBundleValidationResult.SafeErrorMessage"/>
/// that names the defect class only -- never a fragment of the submitted PEM text, a
/// parsed field from a MALFORMED input (which could carry attacker-controlled content),
/// or any private-key material. A private key anywhere in the input is detected before
/// any other parsing and short-circuits every other check, so it can never leak into a
/// later, more detailed error message either.
/// </summary>
public static class TrustBundleValidator
{
	/// <summary>
	/// 64 KiB. A real CA chain is a few KB per certificate; this generously bounds a
	/// multi-certificate chain while rejecting a pathological/adversarial upload long
	/// before it reaches X509 parsing (issue #753 AC "oversized ... fail safely").
	/// </summary>
	public const int MaxPemBytes = 64 * 1024;

	private const int MaxChainLength = 16;

	private static readonly Regex PrivateKeyMarker = new(
		"-----BEGIN (?:RSA |EC |DSA |ENCRYPTED )?PRIVATE KEY-----",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	public static TrustBundleValidationResult Validate(string? label, string? pemChain, DateTimeOffset now)
	{
		if (string.IsNullOrWhiteSpace(pemChain))
		{
			return Failed(TrustBundleValidationOutcome.Empty, "A PEM-encoded certificate or chain is required.");
		}

		byte[] utf8Bytes = Encoding.UTF8.GetBytes(pemChain);
		if (utf8Bytes.Length > MaxPemBytes)
		{
			return Failed(TrustBundleValidationOutcome.OversizedInput, $"The upload exceeds the {MaxPemBytes:N0}-byte limit for a CA certificate/chain.");
		}

		// Private-key rejection runs BEFORE any certificate parsing and on the raw text
		// -- issue #753 AC "a private key in an upload must be REJECTED and never
		// persisted or logged." This must never be reordered after parsing succeeds,
		// since a mixed upload (a valid chain plus an embedded private key) must still
		// be rejected wholesale, not silently split.
		if (PrivateKeyMarker.IsMatch(pemChain))
		{
			return Failed(TrustBundleValidationOutcome.ContainsPrivateKey, "The upload contains private key material, which is never accepted for a CA trust bundle.");
		}

		List<X509Certificate2> parsed;
		try
		{
			parsed = ParseChain(pemChain);
		}
		catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
		{
			// ArgumentException covers PemEncoding.Find's "No PEM encoded data found"
			// (a BEGIN CERTIFICATE marker with a body that isn't valid base64/PEM at
			// all) -- every parse failure from a hostile or truncated body collapses to
			// the same safe, non-echoing Malformed outcome regardless of which
			// framework layer objected first.
			return Failed(TrustBundleValidationOutcome.Malformed, "The upload is not a valid PEM-encoded X.509 certificate or chain.");
		}

		try
		{
			if (parsed.Count == 0)
			{
				return Failed(TrustBundleValidationOutcome.Malformed, "No CERTIFICATE blocks were found in the upload.");
			}

			if (parsed.Count > MaxChainLength)
			{
				return Failed(TrustBundleValidationOutcome.Malformed, $"The chain exceeds the {MaxChainLength}-certificate limit.");
			}

			X509Certificate2 leaf = parsed[0];

			if (now >= leaf.NotAfter)
			{
				return Failed(TrustBundleValidationOutcome.Expired, $"The certificate expired on {leaf.NotAfter:O}.");
			}

			string fingerprint = Convert.ToHexString(leaf.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();
			string resolvedLabel = string.IsNullOrWhiteSpace(label) ? leaf.Subject : label.Trim();

			return new TrustBundleValidationResult(
				TrustBundleValidationOutcome.Valid,
				resolvedLabel,
				pemChain,
				leaf.Subject,
				leaf.Issuer,
				fingerprint,
				leaf.NotBefore,
				leaf.NotAfter,
				SafeErrorMessage: null);
		}
		finally
		{
			foreach (X509Certificate2 cert in parsed)
			{
				cert.Dispose();
			}
		}
	}

	/// <summary>
	/// Splits and parses every CERTIFICATE PEM block independently (rather than relying
	/// on a single all-or-nothing chain import) so one malformed block among several
	/// valid ones is reported the same clean way as a wholly malformed upload, instead
	/// of a framework exception whose message might echo attacker-controlled bytes.
	/// </summary>
	private static List<X509Certificate2> ParseChain(string pemChain)
	{
		List<X509Certificate2> certs = [];
		int index = 0;
		while (true)
		{
			int start = pemChain.IndexOf("-----BEGIN CERTIFICATE-----", index, StringComparison.Ordinal);
			if (start < 0)
			{
				break;
			}

			int end = pemChain.IndexOf("-----END CERTIFICATE-----", start, StringComparison.Ordinal);
			if (end < 0)
			{
				throw new FormatException("Unterminated CERTIFICATE block.");
			}

			end += "-----END CERTIFICATE-----".Length;
			string block = pemChain[start..end];
			certs.Add(new X509Certificate2(DecodePemBlock(block)));
			index = end;
		}

		return certs;
	}

	/// <summary>
	/// .NET 8 has no single-call "PEM text to DER bytes" API on this target framework
	/// (<c>PemEncoding.Decode</c> and <c>X509CertificateLoader</c> both arrived in .NET
	/// 9) -- <see cref="PemEncoding.Find"/> locates the base64 payload span, which is
	/// then decoded by hand.
	/// </summary>
	private static byte[] DecodePemBlock(string pemBlock)
	{
		PemFields fields = PemEncoding.Find(pemBlock);
		return Convert.FromBase64String(pemBlock[fields.Base64Data]);
	}

	private static TrustBundleValidationResult Failed(TrustBundleValidationOutcome outcome, string message) =>
		new(outcome, null, null, null, null, null, null, null, message);
}
