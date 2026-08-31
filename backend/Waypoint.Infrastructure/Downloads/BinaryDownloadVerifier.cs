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

namespace Waypoint.Infrastructure.Downloads;

/// <inheritdoc cref="IBinaryDownloadVerifier"/>
public sealed class BinaryDownloadVerifier : IBinaryDownloadVerifier
{
	public async Task<BinaryDownloadVerificationResult> VerifyAsync(
		DepotArtifact artifact, string depotStorePath, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(artifact);
		ArgumentException.ThrowIfNullOrWhiteSpace(depotStorePath);

		string? resolvedPath = ResolveConfinedPath(depotStorePath, artifact.ExternalId);
		if (resolvedPath is null)
		{
			return BinaryDownloadVerificationResult.Fail(
				$"Refusing to verify '{artifact.ExternalId}': resolves outside the configured depot store root.");
		}

		if (!File.Exists(resolvedPath))
		{
			return BinaryDownloadVerificationResult.Fail(
				$"Downloaded file for '{artifact.ExternalId}' was not found at the expected depot store path '{resolvedPath}'.");
		}

		long actualSize = new FileInfo(resolvedPath).Length;
		if (artifact.SizeBytes is long expectedSize && actualSize != expectedSize)
		{
			return BinaryDownloadVerificationResult.Fail(
				$"Size mismatch for '{artifact.ExternalId}': catalog expects {expectedSize} bytes, downloaded file is {actualSize} bytes.",
				resolvedPath);
		}

		// Grill decision Q8: ALWAYS self-hash SHA-256 at download, regardless of
		// whether the catalog already carries one, so every artifact ends up with a
		// standard database shape -- never skip this to "save work" when the catalog
		// hash is present, and never skip it entirely when the catalog hash is absent.
		string actualSha256;
		await using (FileStream stream = File.OpenRead(resolvedPath))
		{
			byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
			actualSha256 = Convert.ToHexString(hash).ToLowerInvariant();
		}

		if (!string.IsNullOrWhiteSpace(artifact.Sha256)
			&& !string.Equals(actualSha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
		{
			return BinaryDownloadVerificationResult.Fail(
				$"SHA-256 mismatch for '{artifact.ExternalId}': catalog expects {artifact.Sha256}, downloaded file is {actualSha256}.",
				resolvedPath);
		}

		return BinaryDownloadVerificationResult.Ok(actualSha256, resolvedPath);
	}

	/// <summary>
	/// <c>depotStorePath</c> is the same root <c>BinariesDownloadJobHandler</c> passes
	/// the tool as <c>--depot-store</c> -- <c>relativePath</c> comes from the
	/// authenticated catalog (migration 0100/#1488), not from user input on this call
	/// path, but a defense-in-depth confinement check costs nothing and mirrors
	/// <c>RetentionSweepService</c>'s identical guard over the same column. Returns
	/// null (never throws) when the resolved path escapes the root.
	/// </summary>
	private static string? ResolveConfinedPath(string depotStorePath, string relativePath)
	{
		string fullRoot = Path.GetFullPath(depotStorePath);
		string fullPath = Path.GetFullPath(Path.Combine(depotStorePath, relativePath));
		bool confined = fullPath.StartsWith(fullRoot, StringComparison.Ordinal)
			&& (fullPath.Length == fullRoot.Length || fullPath[fullRoot.Length] == Path.DirectorySeparatorChar);
		return confined ? fullPath : null;
	}
}
