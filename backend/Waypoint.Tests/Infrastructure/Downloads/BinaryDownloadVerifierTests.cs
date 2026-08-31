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
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Infrastructure.Downloads;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// The <c>binaries-download</c> post-download verifier (issue #1486): a downloaded
/// binary is checked against the authenticated catalog's size/SHA-256 (grill decision
/// Q8) before <c>BinariesDownloadJobHandler</c> is allowed to report it present. Every
/// case here uses a real temp file and real SHA-256 -- the whole point of this class is
/// bytes-on-disk vs. catalog-metadata comparison, so faking the hash would test nothing.
/// </summary>
public sealed class BinaryDownloadVerifierTests : IDisposable
{
	private readonly string _depotRoot = Directory.CreateTempSubdirectory("binary-download-verifier-tests-").FullName;
	private readonly BinaryDownloadVerifier _verifier = new();

	public void Dispose()
	{
		try
		{
			Directory.Delete(_depotRoot, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	private static DepotArtifact ArtifactWith(string externalId, string? sha256, long? sizeBytes) =>
		new(Guid.NewGuid(), externalId, sha256, "downloading", "vcf", "9.1.0.0400", "{}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, sizeBytes);

	private string WriteFile(string relativePath, byte[] bytes)
	{
		string fullPath = Path.Combine(_depotRoot, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
		File.WriteAllBytes(fullPath, bytes);
		return fullPath;
	}

	private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

	[Fact]
	public async Task MatchingSizeAndHash_Verifies()
	{
		byte[] bytes = "genuine-vcf-binary"u8.ToArray();
		WriteFile("PROD/COMP/VCFDT/9.1.0.0400/bundle.tar", bytes);
		DepotArtifact artifact = ArtifactWith("PROD/COMP/VCFDT/9.1.0.0400/bundle.tar", Sha256Hex(bytes), bytes.Length);

		BinaryDownloadVerificationResult result = await _verifier.VerifyAsync(artifact, _depotRoot, CancellationToken.None);

		Assert.True(result.Verified);
		Assert.Equal(Sha256Hex(bytes), result.Sha256);
		Assert.Null(result.FailureReason);
	}

	[Fact]
	public async Task SizeMismatch_FailsVerification_RegardlessOfHash()
	{
		byte[] bytes = "genuine-vcf-binary"u8.ToArray();
		string filePath = WriteFile("bundle.tar", bytes);
		DepotArtifact artifact = ArtifactWith("bundle.tar", Sha256Hex(bytes), bytes.Length + 1);

		BinaryDownloadVerificationResult result = await _verifier.VerifyAsync(artifact, _depotRoot, CancellationToken.None);

		Assert.False(result.Verified);
		Assert.Null(result.Sha256);
		Assert.Contains("Size mismatch", result.FailureReason);
		// Issue #1486 review round 1, finding 1: ResolvedPath must be populated on a
		// content mismatch (the file exists, at a confined path) so a caller can
		// quarantine it -- distinct from the missing-file/path-escape cases below.
		Assert.Equal(filePath, result.ResolvedPath);
	}

	[Fact]
	public async Task Sha256Mismatch_FailsVerification()
	{
		byte[] bytes = "genuine-vcf-binary"u8.ToArray();
		string filePath = WriteFile("bundle.tar", bytes);
		DepotArtifact artifact = ArtifactWith("bundle.tar", new string('0', 64), bytes.Length);

		BinaryDownloadVerificationResult result = await _verifier.VerifyAsync(artifact, _depotRoot, CancellationToken.None);

		Assert.False(result.Verified);
		Assert.Contains("SHA-256 mismatch", result.FailureReason);
		Assert.Equal(filePath, result.ResolvedPath);
	}

	[Fact]
	public async Task NeverReportsPresentDataOnFailure()
	{
		// AC: "A verification failure never results in the artifact being reported
		// present" -- a failed result carries no Sha256, so a caller cannot
		// accidentally upsert it as though verification passed.
		byte[] bytes = "genuine-vcf-binary"u8.ToArray();
		WriteFile("bundle.tar", bytes);
		DepotArtifact artifact = ArtifactWith("bundle.tar", Sha256Hex(bytes), bytes.Length + 1);

		BinaryDownloadVerificationResult result = await _verifier.VerifyAsync(artifact, _depotRoot, CancellationToken.None);

		Assert.False(result.Verified);
		Assert.Null(result.Sha256);
	}

	[Fact]
	public async Task MissingCatalogSha256_SelfHashesAndVerifiesOnSizeAlone()
	{
		// Grill decision Q8: "accept vendor size-only where nothing better exists" --
		// a null catalog Sha256 must not block verification, but the self-hash is
		// still always computed and returned.
		byte[] bytes = "genuine-vcf-binary"u8.ToArray();
		WriteFile("bundle.tar", bytes);
		DepotArtifact artifact = ArtifactWith("bundle.tar", sha256: null, bytes.Length);

		BinaryDownloadVerificationResult result = await _verifier.VerifyAsync(artifact, _depotRoot, CancellationToken.None);

		Assert.True(result.Verified);
		Assert.Equal(Sha256Hex(bytes), result.Sha256);
	}

	[Fact]
	public async Task MissingCatalogSize_SkipsSizeCheckButStillHashMatches()
	{
		byte[] bytes = "genuine-vcf-binary"u8.ToArray();
		WriteFile("bundle.tar", bytes);
		DepotArtifact artifact = ArtifactWith("bundle.tar", Sha256Hex(bytes), sizeBytes: null);

		BinaryDownloadVerificationResult result = await _verifier.VerifyAsync(artifact, _depotRoot, CancellationToken.None);

		Assert.True(result.Verified);
	}

	[Fact]
	public async Task NoCatalogMetadataAtAll_StillSelfHashesAndVerifies()
	{
		byte[] bytes = "genuine-vcf-binary"u8.ToArray();
		WriteFile("bundle.tar", bytes);
		DepotArtifact artifact = ArtifactWith("bundle.tar", sha256: null, sizeBytes: null);

		BinaryDownloadVerificationResult result = await _verifier.VerifyAsync(artifact, _depotRoot, CancellationToken.None);

		Assert.True(result.Verified);
		Assert.Equal(Sha256Hex(bytes), result.Sha256);
	}

	[Fact]
	public async Task MissingFile_FailsVerification()
	{
		DepotArtifact artifact = ArtifactWith("never-downloaded.tar", "abc", 123);

		BinaryDownloadVerificationResult result = await _verifier.VerifyAsync(artifact, _depotRoot, CancellationToken.None);

		Assert.False(result.Verified);
		Assert.Contains("not found", result.FailureReason);
		// Nothing safe to quarantine when there was never a file at the resolved path.
		Assert.Null(result.ResolvedPath);
	}

	[Fact]
	public async Task PathEscapingDepotRoot_FailsVerification_NeverReadsOutsideRoot()
	{
		// The catalog is authenticated, but a defense-in-depth confinement check
		// mirrors RetentionSweepService's identical guard -- a relative_path that
		// resolves outside the configured depot store must never be treated as
		// verified, whatever is actually sitting at that escaped location.
		string outsideDir = Directory.CreateTempSubdirectory("binary-download-verifier-outside-").FullName;
		try
		{
			byte[] bytes = "not-really-in-the-depot"u8.ToArray();
			File.WriteAllBytes(Path.Combine(outsideDir, "escaped.tar"), bytes);
			DepotArtifact artifact = ArtifactWith("../" + Path.GetFileName(outsideDir) + "/escaped.tar", Sha256Hex(bytes), bytes.Length);

			BinaryDownloadVerificationResult result = await _verifier.VerifyAsync(artifact, _depotRoot, CancellationToken.None);

			Assert.False(result.Verified);
			Assert.Contains("outside the configured depot store root", result.FailureReason);
			// Never resolve (let alone quarantine) a path that escaped the depot root.
			Assert.Null(result.ResolvedPath);
		}
		finally
		{
			Directory.Delete(outsideDir, recursive: true);
		}
	}
}
