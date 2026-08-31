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

/// <summary>
/// Invokes the installed <c>vcf-download-tool</c>'s <c>binaries download</c> subcommand
/// (issue #1482, epic #1181, split from #795's design record) -- the actual VCF artifact
/// acquisition, distinct from <see cref="IManagedToolMetadataPuller"/> (vendor catalog
/// metadata) and <see cref="IDepotIdentityTool"/> (enrollment). The 2026-08-28 grill
/// decision (R2-8) fixed unbounded concurrency as the model: every caller MUST pass its
/// own job-scoped <paramref name="identityHome"/>, never a shared identity directory,
/// so concurrent binaries-download jobs cannot collide on <c>machine_id</c> the way
/// issue #790 documents for the shared enrollment/catalog-pull identity home -- #790 has
/// not landed as of this issue, so this interface's contract makes isolation the
/// caller's job rather than depending on that fix landing first.
/// </summary>
public interface IBinariesDownloadTool
{
	/// <summary>
	/// Runs the bounded noninteractive <c>binaries download --id &lt;externalId&gt;
	/// --depot-store=&lt;depotStorePath&gt; --ceip=DISABLE</c> invocation (issue #1482's
	/// documented contract) with <c>HOME</c>/<c>XDG_DATA_HOME</c> pointed at
	/// <paramref name="identityHome"/>, after seeding that home's <c>machine_id</c> from
	/// <paramref name="assetId"/> (mirrors <see cref="IDepotIdentityTool"/>'s "identity
	/// follows the code" contract, issue #787). Never prompts; bounded by
	/// <see cref="ManagedToolOptions"/>'s configured timeout.
	/// </summary>
	Task<BinariesDownloadResult> DownloadAsync(
		string externalId,
		string depotStorePath,
		string activationCodePath,
		string identityHome,
		string assetId,
		CancellationToken cancellationToken);
}

/// <summary>
/// Outcome of <see cref="IBinariesDownloadTool.DownloadAsync"/>. <see cref="Stdout"/> is
/// captured verbatim and never parsed for control flow (issue #1482 AC, deferring actual
/// progress/rate/ETA computation to #1041) -- callers log it as-is.
/// </summary>
public sealed record BinariesDownloadResult(
	bool Succeeded,
	bool IsAuthFailure,
	bool IsThrottled,
	bool IsDiskFailure,
	string? FailureReason,
	string Stdout)
{
	public static BinariesDownloadResult Ok(string stdout) => new(true, false, false, false, null, stdout);

	/// <summary>The tool ran and explicitly rejected the code or reported an auth/entitlement problem.</summary>
	public static BinariesDownloadResult AuthFailed(string reason, string stdout)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		return new(false, true, false, false, reason, stdout);
	}

	/// <summary>Broadcom rate-limited/throttled this identity -- an actionable, distinct signal from a hard auth rejection or a transient network problem (issue #1482 AC: throttle-detection observability).</summary>
	public static BinariesDownloadResult Throttled(string reason, string stdout)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		return new(false, false, true, false, reason, stdout);
	}

	/// <summary>Local disk exhaustion/permission failure writing into the depot store -- never conflated with a vendor-side rejection.</summary>
	public static BinariesDownloadResult DiskFailed(string reason, string stdout)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		return new(false, false, false, true, reason, stdout);
	}

	/// <summary>The call could not be completed (tool missing, timeout) or a network/ambiguous nonzero exit.</summary>
	public static BinariesDownloadResult Failed(string reason, string stdout)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		return new(false, false, false, false, reason, stdout);
	}
}
