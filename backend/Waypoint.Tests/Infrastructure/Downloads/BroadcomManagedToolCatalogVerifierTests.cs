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
using Microsoft.Extensions.Options;
using Waypoint.Core.Downloads;
using Waypoint.Infrastructure.Downloads;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

public sealed class BroadcomManagedToolCatalogVerifierTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("waypoint-vcfdt-catalog-").FullName;
	private readonly string _metadata;
	private readonly string _artifact;

	public BroadcomManagedToolCatalogVerifierTests()
	{
		_metadata = Path.Combine(_root, "PROD", "metadata", "productVersionCatalog", "v1");
		_artifact = Path.Combine(_root, "PROD", "COMP", "VCFDT", "vcf-download-tool-9.1.0.0400.25570101.tar.gz");
		Directory.CreateDirectory(_metadata);
		Directory.CreateDirectory(Path.GetDirectoryName(_artifact)!);
		File.WriteAllBytes(_artifact, [1, 2, 3, 4]);
		WriteCatalogAndSignature(SHA256.HashData([1, 2, 3, 4]), 4);
	}

	public void Dispose() => Directory.Delete(_root, recursive: true);

	private BroadcomManagedToolCatalogVerifier CreateVerifier() => new(Options.Create(new ManagedToolOptions
	{
		LocalRepositoryPath = _root,
	}));

	private void WriteCatalogAndSignature(byte[] expectedHash, long size, RSA? signingKey = null, bool duplicateConflict = false)
	{
		bool ownsKey = signingKey is null;
		signingKey ??= RSA.Create(2048);
		try
		{
			string duplicate = duplicateConflict
				? ", {\"fileName\":\"vcf-download-tool-9.1.0.0400.25570101.tar.gz\",\"checksum\":\"" + new string('a', 64) + "\",\"size\":4}"
				: string.Empty;
			string json = "{\"patches\":{\"VCFDT\":[{\"productVersion\":\"9.1.0.0400.25570101\",\"artifacts\":{\"bundles\":[{\"binaries\":[{\"fileName\":\"vcf-download-tool-9.1.0.0400.25570101.tar.gz\",\"checksum\":\"" + Convert.ToHexString(expectedHash).ToLowerInvariant() + "\",\"size\":" + size + "}" + duplicate + "]}]}}]}}";
			byte[] bytes = Encoding.UTF8.GetBytes(json);
			File.WriteAllBytes(Path.Combine(_metadata, "productVersionCatalog.json"), bytes);
			CertificateRequest request = new("CN=VMware Catalog Test", signingKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
			using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
			byte[] signature = signingKey.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
			File.WriteAllText(Path.Combine(_metadata, "productVersionCatalog.sig"), $"SHA256(2f431d2654aeecbc058dd054d0dbb7ce)= {Convert.ToHexString(signature).ToLowerInvariant()}\n{certificate.ExportCertificatePem()}");
		}
		finally
		{
			if (ownsKey)
			{
				signingKey.Dispose();
			}
		}
	}

	[Fact]
	public async Task ValidSignedCatalogAndMatchingArtifact_Verifies()
	{
		ManagedToolCatalogVerificationResult result = await CreateVerifier().VerifyAsync(_root, _artifact, "9.1.0.0400.25570101", CancellationToken.None);
		Assert.True(result.Valid, result.FailureReason);
		Assert.Equal(Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])).ToLowerInvariant(), result.ActualSha256);
	}

	[Fact]
	public async Task TamperedCatalog_IsRejectedBeforeChecksumUse()
	{
		File.AppendAllText(Path.Combine(_metadata, "productVersionCatalog.json"), " ");
		ManagedToolCatalogVerificationResult result = await CreateVerifier().VerifyAsync(_root, _artifact, null, CancellationToken.None);
		Assert.False(result.Valid);
		Assert.Contains("signature is invalid", result.FailureReason, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Issue #798: there is no independent trust anchor. A catalog consistently signed
	/// by ANY certificate -- not just a pinned/well-known publisher one -- verifies, as
	/// long as the signature matches the catalog bytes and the envelope's own embedded
	/// certificate. This is the deliberate integrity-only model (no provenance claim).
	/// </summary>
	[Fact]
	public async Task CatalogSignedByArbitraryNonPinnedCertificate_StillVerifies()
	{
		using RSA arbitrarySigner = RSA.Create(2048);
		WriteCatalogAndSignature(SHA256.HashData([1, 2, 3, 4]), 4, arbitrarySigner);
		ManagedToolCatalogVerificationResult result = await CreateVerifier().VerifyAsync(_root, _artifact, "9.1.0.0400.25570101", CancellationToken.None);
		Assert.True(result.Valid, result.FailureReason);
	}

	/// <summary>
	/// Issue #798 AC2: a signature envelope whose embedded certificate does not match
	/// the key that produced the signature -- e.g. an envelope reassembled from two
	/// different signed catalogs, or corrupted in transit -- still fails closed before
	/// promotion/indexing, even with no independent anchor to compare against.
	/// </summary>
	[Fact]
	public async Task EnvelopeCertificateSubstituted_IsRejected()
	{
		using RSA signer = RSA.Create(2048);
		WriteCatalogAndSignature(SHA256.HashData([1, 2, 3, 4]), 4, signer);
		string signatureLine = File.ReadAllLines(Path.Combine(_metadata, "productVersionCatalog.sig"))[0];

		using RSA otherSigner = RSA.Create(2048);
		CertificateRequest request = new("CN=Substituted Signer", otherSigner, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		using X509Certificate2 substituted = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
		File.WriteAllText(Path.Combine(_metadata, "productVersionCatalog.sig"), $"{signatureLine}\n{substituted.ExportCertificatePem()}");

		ManagedToolCatalogVerificationResult result = await CreateVerifier().VerifyAsync(_root, _artifact, null, CancellationToken.None);
		Assert.False(result.Valid);
		Assert.Contains("signature is invalid", result.FailureReason, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ArtifactHashMismatch_IsRejected()
	{
		File.WriteAllBytes(_artifact, [9, 9, 9, 9]);
		ManagedToolCatalogVerificationResult result = await CreateVerifier().VerifyAsync(_root, _artifact, null, CancellationToken.None);
		Assert.False(result.Valid);
		Assert.Contains("SHA-256 mismatch", result.FailureReason, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ArtifactSizeMismatch_IsRejectedBeforeHashing()
	{
		WriteCatalogAndSignature(SHA256.HashData([1, 2, 3, 4]), 99);
		ManagedToolCatalogVerificationResult result = await CreateVerifier().VerifyAsync(_root, _artifact, null, CancellationToken.None);
		Assert.False(result.Valid);
		Assert.Null(result.ActualSha256);
		Assert.Contains("size mismatch", result.FailureReason, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ConflictingDuplicateCatalogEntries_AreRejected()
	{
		WriteCatalogAndSignature(SHA256.HashData([1, 2, 3, 4]), 4, duplicateConflict: true);
		ManagedToolCatalogVerificationResult result = await CreateVerifier().VerifyAsync(_root, _artifact, null, CancellationToken.None);
		Assert.False(result.Valid);
		Assert.Contains("conflicting", result.FailureReason, StringComparison.OrdinalIgnoreCase);
	}
}
