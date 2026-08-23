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
using Microsoft.Extensions.Options;
using Waypoint.Core.Downloads;
using Waypoint.Infrastructure.Downloads;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// Issue #39's signature-verification gate. Every key/artifact here is generated
/// in-test (CLAUDE.md: no real Broadcom key material may ever appear in this public
/// repository) -- a fresh RSA keypair stands in for "the Broadcom release key" and a
/// small invented byte array stands in for "the candidate artifact."
/// </summary>
public sealed class RsaManagedToolSignatureVerifierTests : IDisposable
{
	private readonly string _tempDirectory = Directory.CreateTempSubdirectory("waypoint-tool-sig-test-").FullName;

	public void Dispose()
	{
		if (Directory.Exists(_tempDirectory))
		{
			Directory.Delete(_tempDirectory, recursive: true);
		}
	}

	private RsaManagedToolSignatureVerifier CreateVerifier(string? keyPath = null) =>
		new(Options.Create(new ManagedToolOptions { ReleasePublicKeyPath = keyPath ?? Path.Combine(_tempDirectory, "release-public-key.pem") }));

	private (string ArtifactPath, string SignaturePath, string KeyPath) WriteValidArtifact(byte[]? artifactBytes = null)
	{
		using RSA key = RSA.Create(2048);
		byte[] artifact = artifactBytes ?? [1, 2, 3, 4, 5];
		byte[] signature = key.SignData(artifact, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

		string keyPath = Path.Combine(_tempDirectory, "release-public-key.pem");
		File.WriteAllText(keyPath, key.ExportSubjectPublicKeyInfoPem());

		string artifactPath = Path.Combine(_tempDirectory, "vcf-download-tool");
		File.WriteAllBytes(artifactPath, artifact);

		string signaturePath = artifactPath + ".sig";
		File.WriteAllBytes(signaturePath, signature);

		return (artifactPath, signaturePath, keyPath);
	}

	[Fact]
	public async Task ValidSignature_Verifies()
	{
		(string artifactPath, string signaturePath, string keyPath) = WriteValidArtifact();
		RsaManagedToolSignatureVerifier verifier = CreateVerifier(keyPath);

		ManagedToolSignatureResult result = await verifier.VerifyAsync(artifactPath, signaturePath, CancellationToken.None);

		Assert.True(result.Valid);
		Assert.Null(result.FailureReason);
	}

	[Fact]
	public async Task TamperedArtifact_IsRejected()
	{
		(string artifactPath, string signaturePath, string keyPath) = WriteValidArtifact();
		File.WriteAllBytes(artifactPath, [9, 9, 9, 9, 9]); // tamper after signing
		RsaManagedToolSignatureVerifier verifier = CreateVerifier(keyPath);

		ManagedToolSignatureResult result = await verifier.VerifyAsync(artifactPath, signaturePath, CancellationToken.None);

		Assert.False(result.Valid);
		Assert.Contains("does not match", result.FailureReason, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task SignedByADifferentKey_IsRejected()
	{
		(string artifactPath, string signaturePath, _) = WriteValidArtifact();

		// A second, unrelated keypair stands in for "not actually the Broadcom key" --
		// the release public key on file does not match whoever signed this artifact.
		using RSA wrongKey = RSA.Create(2048);
		string wrongKeyPath = Path.Combine(_tempDirectory, "wrong-key.pem");
		File.WriteAllText(wrongKeyPath, wrongKey.ExportSubjectPublicKeyInfoPem());

		RsaManagedToolSignatureVerifier verifier = CreateVerifier(wrongKeyPath);
		ManagedToolSignatureResult result = await verifier.VerifyAsync(artifactPath, signaturePath, CancellationToken.None);

		Assert.False(result.Valid);
	}

	[Fact]
	public async Task MissingSignatureFile_IsRejectedWithoutThrowing()
	{
		(string artifactPath, _, string keyPath) = WriteValidArtifact();
		RsaManagedToolSignatureVerifier verifier = CreateVerifier(keyPath);

		ManagedToolSignatureResult result = await verifier.VerifyAsync(artifactPath, artifactPath + ".sig.missing", CancellationToken.None);

		Assert.False(result.Valid);
		Assert.Contains("signature not found", result.FailureReason, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task MissingReleaseKey_IsRejectedWithAnOperatorActionableMessage()
	{
		(string artifactPath, string signaturePath, _) = WriteValidArtifact();
		RsaManagedToolSignatureVerifier verifier = CreateVerifier(Path.Combine(_tempDirectory, "never-provisioned.pem"));

		ManagedToolSignatureResult result = await verifier.VerifyAsync(artifactPath, signaturePath, CancellationToken.None);

		Assert.False(result.Valid);
		Assert.Contains("not provisioned", result.FailureReason, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task MissingArtifact_IsRejectedWithoutThrowing()
	{
		(_, string signaturePath, string keyPath) = WriteValidArtifact();
		RsaManagedToolSignatureVerifier verifier = CreateVerifier(keyPath);

		ManagedToolSignatureResult result = await verifier.VerifyAsync(Path.Combine(_tempDirectory, "does-not-exist"), signaturePath, CancellationToken.None);

		Assert.False(result.Valid);
		Assert.Contains("not found", result.FailureReason, StringComparison.OrdinalIgnoreCase);
	}
}
