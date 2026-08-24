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
	private readonly string _trust;

	public BroadcomManagedToolCatalogVerifierTests()
	{
		_metadata = Path.Combine(_root, "PROD", "metadata", "productVersionCatalog", "v1");
		_artifact = Path.Combine(_root, "PROD", "COMP", "VCFDT", "vcf-download-tool-9.1.0.0400.25570101.tar.gz");
		_trust = Path.Combine(_root, "catalog-trust.cert");
		Directory.CreateDirectory(_metadata);
		Directory.CreateDirectory(Path.GetDirectoryName(_artifact)!);
		File.WriteAllBytes(_artifact, [1, 2, 3, 4]);
		WriteCatalogAndSignature(SHA256.HashData([1, 2, 3, 4]), 4);
	}

	public void Dispose() => Directory.Delete(_root, recursive: true);

	private BroadcomManagedToolCatalogVerifier CreateVerifier() => new(Options.Create(new ManagedToolOptions
	{
		LocalRepositoryPath = _root,
		CatalogTrustCertificatePath = _trust,
	}));

	private void WriteCatalogAndSignature(byte[] expectedHash, long size, RSA? signingKey = null, bool trustSigner = true, bool duplicateConflict = false)
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
			if (trustSigner)
			{
				File.WriteAllText(_trust, certificate.ExportCertificatePem());
			}
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

	[Fact]
	public async Task DifferentTrustCertificate_IsRejected()
	{
		using RSA other = RSA.Create(2048);
		CertificateRequest request = new("CN=Wrong", other, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
		File.WriteAllText(_trust, certificate.ExportCertificatePem());
		ManagedToolCatalogVerificationResult result = await CreateVerifier().VerifyAsync(_root, _artifact, null, CancellationToken.None);
		Assert.False(result.Valid);
		Assert.Contains("does not match", result.FailureReason, StringComparison.OrdinalIgnoreCase);
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
	public async Task MissingIndependentTrustCertificate_IsRejected()
	{
		File.Delete(_trust);
		ManagedToolCatalogVerificationResult result = await CreateVerifier().VerifyAsync(_root, _artifact, null, CancellationToken.None);
		Assert.False(result.Valid);
		Assert.Contains("not provisioned", result.FailureReason, StringComparison.OrdinalIgnoreCase);
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
