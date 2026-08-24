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

namespace Waypoint.Core.Downloads;

/// <summary>The exact classification of a failed depot fetch (issue #39 depot-fetch path) -- lets the job handler choose the right ledger outcome and error text without parsing prose.</summary>
public enum ManagedToolDepotFetchFailureKind
{
	/// <summary>The depot rejected the credential (401/403-shaped response). Never carries the token value.</summary>
	AuthFailure,

	/// <summary>The depot could not be reached at all, or the request timed out.</summary>
	Unreachable,

	/// <summary>The artifact (or its detached signature) exceeded <see cref="ManagedToolOptions.DepotFetchMaxBytes"/> -- aborted before the whole body was buffered.</summary>
	TooLarge,

	/// <summary>Any other failure (bad status code, missing configuration, malformed response).</summary>
	Other,
}

/// <summary>Result of one <see cref="IManagedToolDepotFetcher.FetchAsync"/> call.</summary>
public sealed record ManagedToolDepotFetchResult(
	bool Succeeded,
	string? ArtifactPath,
	string? RepositoryRoot,
	ManagedToolDepotFetchFailureKind? FailureKind,
	string? FailureReason)
{
	/// <param name="artifactPath">Where the fetched <c>vcf-download-tool</c> distribution archive was written.</param>
	/// <param name="repositoryRoot">
	/// A staging directory laid out like a local repository root (issue #671): the
	/// fetched catalog and catalog signature are placed at
	/// <see cref="ManagedToolOptions.ProductVersionCatalogPath"/> and
	/// <see cref="ManagedToolOptions.ProductVersionCatalogSignaturePath"/> relative to
	/// this root, so <see cref="IManagedToolCatalogVerifier.VerifyAsync"/> -- the same
	/// verifier the local-repository install path uses -- can authenticate the
	/// connected fetch without a source-specific verification path of its own.
	/// </param>
	public static ManagedToolDepotFetchResult Success(string artifactPath, string repositoryRoot) =>
		new(true, artifactPath, repositoryRoot, null, null);

	public static ManagedToolDepotFetchResult Failure(ManagedToolDepotFetchFailureKind kind, string reason)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		return new(false, null, null, kind, reason);
	}
}

/// <summary>
/// Fetches the <c>vcf-download-tool</c> artifact plus Broadcom's signed
/// product-version catalog and catalog signature from the configured depot (issue
/// #671, ADR-0015 decision 3), authenticating all three legs with the same
/// already-decrypted Activation Code value. The vendor does not publish a
/// per-artifact <c>.sig</c> (issue #671) -- integrity comes from the authenticated
/// catalog, matching the local-repository install path's verification (issue #669).
/// Connected-mode-only by the caller's contract -- this interface has no mode
/// awareness of its own; the job handler refuses before ever constructing a request.
///
/// Implementations must never let the credential value appear in
/// <see cref="ManagedToolDepotFetchResult.FailureReason"/> or in any exception
/// message that could reach a ledger row or job note -- the same "never logged,
/// never persisted" bar every other depot-credential consumer holds itself to.
/// </summary>
public interface IManagedToolDepotFetcher
{
	/// <summary>
	/// Downloads the artifact, the product-version catalog, and the catalog signature
	/// into <paramref name="destinationDirectory"/>, bounded by
	/// <see cref="ManagedToolOptions.DepotFetchMaxBytes"/> (each leg independently) and
	/// <see cref="ManagedToolOptions.DepotFetchTimeout"/> (all three legs combined).
	/// Never throws for an ordinary fetch failure (unreachable, auth failure, oversize,
	/// timeout, missing configuration) -- those come back as
	/// <see cref="ManagedToolDepotFetchResult.Succeeded"/> == false with a
	/// <see cref="ManagedToolDepotFetchFailureKind"/> so the caller can record the
	/// right ledger outcome rather than crash the job. On any failure, every temporary
	/// file already written by this call is removed before returning.
	/// </summary>
	Task<ManagedToolDepotFetchResult> FetchAsync(
		string depotToken, string? version, string destinationDirectory, CancellationToken cancellationToken);
}
