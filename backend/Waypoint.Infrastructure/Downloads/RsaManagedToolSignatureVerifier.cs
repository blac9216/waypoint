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

namespace Waypoint.Infrastructure.Downloads;

/// <inheritdoc cref="IManagedToolSignatureVerifier"/>
/// <remarks>
/// RSA-PSS/SHA-256 over a detached signature file (issue #39). The release public key
/// is a PEM-encoded RSA public key loaded from <see cref="ManagedToolOptions.ReleasePublicKeyPath"/> --
/// never embedded in source (the project never ships the Broadcom release key or
/// anything that would let its output be mistaken for Broadcom-signed; the operator
/// provisions the actual public key out of band). Every failure mode (missing key file,
/// missing signature file, malformed key, cryptographic mismatch) is reported through
/// <see cref="ManagedToolSignatureResult.Fail"/> rather than thrown, so a bad signature
/// or a not-yet-provisioned key produces a clean <c>rejected</c> ledger row instead of
/// crashing the install job.
/// </remarks>
public sealed class RsaManagedToolSignatureVerifier : IManagedToolSignatureVerifier
{
	private readonly IOptions<ManagedToolOptions> _options;

	public RsaManagedToolSignatureVerifier(IOptions<ManagedToolOptions> options)
	{
		ArgumentNullException.ThrowIfNull(options);
		_options = options;
	}

	public async Task<ManagedToolSignatureResult> VerifyAsync(string artifactPath, string signaturePath, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(signaturePath);

		string keyPath = _options.Value.ReleasePublicKeyPath;
		if (!File.Exists(keyPath))
		{
			return ManagedToolSignatureResult.Fail(
				$"Broadcom release public key is not provisioned (expected at '{keyPath}'). An operator must install it before any tool install path can activate a candidate artifact.");
		}

		if (!File.Exists(artifactPath))
		{
			return ManagedToolSignatureResult.Fail($"Candidate artifact not found at '{artifactPath}'.");
		}

		if (!File.Exists(signaturePath))
		{
			return ManagedToolSignatureResult.Fail($"Detached signature not found at '{signaturePath}'. A candidate artifact without a signature file is rejected, never installed unsigned.");
		}

		using RSA publicKey = RSA.Create();
		try
		{
			string pem = await File.ReadAllTextAsync(keyPath, cancellationToken).ConfigureAwait(false);
			publicKey.ImportFromPem(pem);
		}
		catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
		{
			return ManagedToolSignatureResult.Fail($"Release public key at '{keyPath}' could not be parsed: {exception.Message}");
		}

		byte[] signature;
		try
		{
			signature = await File.ReadAllBytesAsync(signaturePath, cancellationToken).ConfigureAwait(false);
		}
		catch (IOException exception)
		{
			return ManagedToolSignatureResult.Fail($"Detached signature at '{signaturePath}' could not be read: {exception.Message}");
		}

		bool valid;
		try
		{
			// Synchronous VerifyData -- no Stream-accepting async overload exists on RSA
			// in .NET 8. Artifacts here are small (a single tool binary), so the
			// blocking read is not a meaningful concern on the download-runner's
			// dedicated job-execution path.
			await using FileStream artifactStream = File.OpenRead(artifactPath);
			valid = publicKey.VerifyData(artifactStream, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
		}
		catch (CryptographicException exception)
		{
			return ManagedToolSignatureResult.Fail($"Signature verification failed: {exception.Message}");
		}

		return valid
			? ManagedToolSignatureResult.Ok()
			: ManagedToolSignatureResult.Fail("Signature does not match the Broadcom release public key. Candidate artifact rejected.");
	}
}
