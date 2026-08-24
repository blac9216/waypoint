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
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Waypoint.Core.Downloads;

namespace Waypoint.Infrastructure.Downloads;

/// <summary>Verifies Broadcom's real productVersionCatalog.json/.sig distribution format.</summary>
public sealed partial class BroadcomManagedToolCatalogVerifier(IOptions<ManagedToolOptions> options) : IManagedToolCatalogVerifier
{
	private readonly IOptions<ManagedToolOptions> _options = options ?? throw new ArgumentNullException(nameof(options));

	[GeneratedRegex(@"\ASHA256\([0-9a-fA-F]+\)=\s*([0-9a-fA-F]{512})\s*\r?\n(-----BEGIN CERTIFICATE-----[\s\S]+?-----END CERTIFICATE-----)\s*\z", RegexOptions.CultureInvariant)]
	private static partial Regex EnvelopeRegex();

	/// <summary>
	/// Upper bound on the authenticated catalog document's size (issue #687 catalog-only
	/// path). The real productVersionCatalog.json is a few MB; this caps a signed-but-
	/// absurd document before the connected pull parses/indexes it, and is generous
	/// enough never to reject a genuine vendor catalog.
	/// </summary>
	private const long MaxCatalogBytes = 256L * 1024 * 1024;

	private readonly record struct AuthenticatedCatalog(byte[]? Bytes, string? FailureReason)
	{
		public bool Valid => FailureReason is null;
		public static AuthenticatedCatalog Ok(byte[] bytes) => new(bytes, null);
		public static AuthenticatedCatalog Fail(string reason) => new(null, reason);
	}

	/// <summary>
	/// Catalog-only authentication (issue #687 connected <c>catalog-pull</c>): trust
	/// chain + detached-signature-envelope check over the catalog's exact bytes + a
	/// size bound, with NO per-artifact size/SHA match. Uses the SAME publisher trust
	/// anchor and envelope convention as the install-time <see cref="VerifyAsync"/>
	/// (they both funnel through <see cref="AuthenticateCatalogDocumentAsync"/>).
	/// </summary>
	public async Task<ManagedToolCatalogAuthenticationResult> AuthenticateCatalogAsync(
		string repositoryRoot, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
		AuthenticatedCatalog authenticated = await AuthenticateCatalogDocumentAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
		return authenticated.Valid
			? ManagedToolCatalogAuthenticationResult.Ok()
			: ManagedToolCatalogAuthenticationResult.Fail(authenticated.FailureReason!);
	}

	/// <summary>
	/// Shared authentication of the catalog document itself -- the only trust check the
	/// connected <c>catalog-pull</c> path needs, and the prefix of the install-time
	/// per-artifact verification. Returns the authenticated catalog bytes on success so
	/// <see cref="VerifyAsync"/> can then match a named candidate against them.
	/// </summary>
	private async Task<AuthenticatedCatalog> AuthenticateCatalogDocumentAsync(
		string repositoryRoot, CancellationToken cancellationToken)
	{
		ManagedToolOptions configured = _options.Value;
		string catalogPath = ResolveConfigured(repositoryRoot, configured.ProductVersionCatalogPath);
		string signaturePath = ResolveConfigured(repositoryRoot, configured.ProductVersionCatalogSignaturePath);
		if (!File.Exists(catalogPath))
		{
			return AuthenticatedCatalog.Fail($"Broadcom product-version catalog not found at '{catalogPath}'.");
		}
		if (!File.Exists(signaturePath))
		{
			return AuthenticatedCatalog.Fail($"Broadcom product-version catalog signature not found at '{signaturePath}'.");
		}
		if (!File.Exists(configured.CatalogTrustCertificatePath))
		{
			return AuthenticatedCatalog.Fail($"Broadcom catalog trust certificate is not provisioned at '{configured.CatalogTrustCertificatePath}'.");
		}

		long catalogFileSize = new FileInfo(catalogPath).Length;
		if (catalogFileSize > MaxCatalogBytes)
		{
			return AuthenticatedCatalog.Fail($"Broadcom product-version catalog is implausibly large ({catalogFileSize} bytes exceeds the {MaxCatalogBytes}-byte bound).");
		}

		byte[] catalogBytes = await File.ReadAllBytesAsync(catalogPath, cancellationToken).ConfigureAwait(false);
		string envelope = await File.ReadAllTextAsync(signaturePath, cancellationToken).ConfigureAwait(false);
		Match match = EnvelopeRegex().Match(envelope);
		if (!match.Success)
		{
			return AuthenticatedCatalog.Fail("Broadcom product-version catalog signature envelope is malformed.");
		}

		try
		{
			using X509Certificate2 embedded = X509Certificate2.CreateFromPem(match.Groups[2].Value);
			using X509Certificate2 trusted = X509Certificate2.CreateFromPem(
				await File.ReadAllTextAsync(configured.CatalogTrustCertificatePath, cancellationToken).ConfigureAwait(false));
			if (!CryptographicOperations.FixedTimeEquals(embedded.RawData, trusted.RawData))
			{
				return AuthenticatedCatalog.Fail("Catalog signature certificate does not match the independently provisioned Broadcom trust certificate.");
			}
			using RSA? rsa = embedded.GetRSAPublicKey();
			if (rsa is null || !rsa.VerifyData(catalogBytes, Convert.FromHexString(match.Groups[1].Value), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
			{
				return AuthenticatedCatalog.Fail("Broadcom product-version catalog signature is invalid.");
			}
		}
		catch (Exception exception) when (exception is CryptographicException or FormatException)
		{
			return AuthenticatedCatalog.Fail($"Broadcom catalog trust material could not be parsed: {exception.Message}");
		}

		return AuthenticatedCatalog.Ok(catalogBytes);
	}

	public async Task<ManagedToolCatalogVerificationResult> VerifyAsync(
		string repositoryRoot, string artifactPath, string? version, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
		AuthenticatedCatalog authenticated = await AuthenticateCatalogDocumentAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
		if (!authenticated.Valid)
		{
			return ManagedToolCatalogVerificationResult.Fail(authenticated.FailureReason!);
		}

		byte[] catalogBytes = authenticated.Bytes!;
		CatalogCandidate[] candidates;
		try
		{
			candidates = FindCandidates(catalogBytes, Path.GetFileName(artifactPath), version).Distinct().ToArray();
		}
		catch (JsonException exception)
		{
			return ManagedToolCatalogVerificationResult.Fail($"Broadcom product-version catalog is malformed: {exception.Message}");
		}
		if (candidates.Length == 0)
		{
			return ManagedToolCatalogVerificationResult.Fail("Artifact is not present in the authenticated Broadcom VCFDT catalog for the requested version.");
		}
		if (candidates.Length != 1)
		{
			return ManagedToolCatalogVerificationResult.Fail("Authenticated Broadcom catalog contains conflicting entries for this artifact/version.");
		}

		CatalogCandidate expected = candidates[0];
		long actualSize = new FileInfo(artifactPath).Length;
		if (actualSize != expected.Size)
		{
			return ManagedToolCatalogVerificationResult.Fail($"Artifact size mismatch: catalog expects {expected.Size} bytes, actual file is {actualSize} bytes.");
		}
		await using FileStream stream = File.OpenRead(artifactPath);
		string actualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
		return string.Equals(actualSha256, expected.Sha256, StringComparison.OrdinalIgnoreCase)
			? ManagedToolCatalogVerificationResult.Ok(actualSha256)
			: ManagedToolCatalogVerificationResult.Fail($"Artifact SHA-256 mismatch: catalog expects {expected.Sha256}, actual file is {actualSha256}.", actualSha256);
	}

	private static string ResolveConfigured(string root, string relative)
	{
		if (Path.IsPathRooted(relative))
		{
			throw new InvalidOperationException("Managed-tool catalog paths must be relative to the repository root.");
		}
		string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		string candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
		if (!candidate.StartsWith(fullRoot, StringComparison.Ordinal))
		{
			throw new InvalidOperationException("Managed-tool catalog path escapes the repository root.");
		}
		return candidate;
	}

	private static CatalogCandidate[] FindCandidates(byte[] json, string fileName, string? requestedVersion)
	{
		using JsonDocument document = JsonDocument.Parse(json);
		List<CatalogCandidate> found = [];
		Walk(document.RootElement, null, fileName, requestedVersion, found);
		return [.. found];
	}

	private static void Walk(JsonElement element, string? version, string fileName, string? requestedVersion, List<CatalogCandidate> found)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			if (element.TryGetProperty("productVersion", out JsonElement versionElement) && versionElement.ValueKind == JsonValueKind.String)
			{
				version = versionElement.GetString();
			}
			if (element.TryGetProperty("fileName", out JsonElement name) && string.Equals(name.GetString(), fileName, StringComparison.Ordinal)
				&& (requestedVersion is null || string.Equals(version, requestedVersion, StringComparison.Ordinal))
				&& element.TryGetProperty("checksum", out JsonElement checksum) && element.TryGetProperty("size", out JsonElement size)
				&& checksum.GetString() is string sha && Regex.IsMatch(sha, "\\A[0-9a-fA-F]{64}\\z", RegexOptions.CultureInvariant)
				&& size.TryGetInt64(out long bytes))
			{
				found.Add(new(version, sha.ToLowerInvariant(), bytes));
			}
			foreach (JsonProperty property in element.EnumerateObject())
			{
				Walk(property.Value, version, fileName, requestedVersion, found);
			}
		}
		else if (element.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement child in element.EnumerateArray())
			{
				Walk(child, version, fileName, requestedVersion, found);
			}
		}
	}

	private sealed record CatalogCandidate(string? Version, string Sha256, long Size);
}
