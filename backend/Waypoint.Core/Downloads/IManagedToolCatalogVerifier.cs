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

/// <summary>Result of verifying a local VCFDT artifact through Broadcom's signed product-version catalog.</summary>
public sealed record ManagedToolCatalogVerificationResult(bool Valid, string? ActualSha256, string? FailureReason)
{
	public static ManagedToolCatalogVerificationResult Ok(string actualSha256) => new(true, actualSha256, null);
	public static ManagedToolCatalogVerificationResult Fail(string reason, string? actualSha256 = null) => new(false, actualSha256, reason);
}

/// <summary>
/// Result of authenticating the Broadcom product-version catalog <em>document</em>
/// itself -- trust-chain + detached-signature + size/shape bounds -- with no
/// per-artifact size/SHA match (issue #687's connected <c>catalog-pull</c>, which
/// pulls the whole catalog rather than installing one named binary).
/// </summary>
public sealed record ManagedToolCatalogAuthenticationResult(bool Valid, string? FailureReason)
{
	public static ManagedToolCatalogAuthenticationResult Ok() => new(true, null);
	public static ManagedToolCatalogAuthenticationResult Fail(string reason) => new(false, reason);
}

/// <summary>
/// Authenticates Broadcom's product-version catalog against an independently
/// provisioned certificate. <see cref="AuthenticateCatalogAsync"/> stops after the
/// catalog document's own trust-chain/signature/shape check (the connected
/// <c>catalog-pull</c> path, issue #687); <see cref="VerifyAsync"/> additionally
/// matches a single named candidate's catalog size and SHA-256 (the install path).
/// Both share the same publisher trust anchor and signature-envelope convention.
/// </summary>
public interface IManagedToolCatalogVerifier
{
	/// <summary>
	/// Authenticates the catalog document only: the independently provisioned trust
	/// certificate, the detached signature envelope over the catalog's exact bytes,
	/// and the catalog's size/JSON-shape bounds -- no per-artifact match. Used by the
	/// connected <c>catalog-pull</c> job, which pulls and indexes the whole catalog
	/// rather than installing one named binary.
	/// </summary>
	Task<ManagedToolCatalogAuthenticationResult> AuthenticateCatalogAsync(
		string repositoryRoot, CancellationToken cancellationToken);

	Task<ManagedToolCatalogVerificationResult> VerifyAsync(
		string repositoryRoot, string artifactPath, string? version, CancellationToken cancellationToken);
}
