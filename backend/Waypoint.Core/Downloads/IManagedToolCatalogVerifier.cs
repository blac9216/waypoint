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
/// Authenticates Broadcom's product-version catalog against an independently
/// provisioned certificate, then verifies a candidate's catalog size and SHA-256.
/// </summary>
public interface IManagedToolCatalogVerifier
{
	Task<ManagedToolCatalogVerificationResult> VerifyAsync(
		string repositoryRoot, string artifactPath, string? version, CancellationToken cancellationToken);
}
