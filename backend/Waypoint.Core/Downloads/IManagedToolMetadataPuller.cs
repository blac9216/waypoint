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
/// Invokes the installed <c>vcf-download-tool</c>'s noninteractive <c>metadata
/// download</c> operation (issue #687) against a scratch depot path, mirroring the
/// sibling reference's <c>vcf-download-tool metadata download -d &lt;depot-path&gt;
/// --depot-download-activation-code-file=&lt;file&gt; --ceip=DISABLE</c> invocation.
/// Bounded, never prompts, and reuses <see cref="IDepotIdentityTool"/>'s
/// HOME/XDG_DATA_HOME identity isolation (<see cref="ManagedToolOptions.IdentityStatePath"/>)
/// so the pull authenticates as the same stable machine identity issue #691's
/// enrollment flow already established -- this interface does not re-derive
/// <c>asset_id</c>/<c>machine_id</c> itself.
/// </summary>
public interface IManagedToolMetadataPuller
{
	/// <summary>
	/// Runs <c>metadata download</c> against <paramref name="depotPath"/> using the
	/// Activation Code staged at <paramref name="activationCodePath"/> (a job-scoped
	/// temp file, never argv/env for the code value itself -- the file path is
	/// argv-visible per the tool's own CLI contract, matching the sibling reference
	/// and <see cref="IDepotIdentityTool"/>'s existing convention). Fails with an
	/// actionable, auth-classified reason if the tool is not installed, the call
	/// times out, or the tool exits non-zero.
	/// </summary>
	Task<CatalogPullResult> PullAsync(string depotPath, string activationCodePath, CancellationToken cancellationToken);
}

/// <summary>Outcome of <see cref="IManagedToolMetadataPuller.PullAsync"/>.</summary>
public sealed record CatalogPullResult(bool Succeeded, bool IsAuthFailure, string? FailureReason)
{
	public static CatalogPullResult Ok() => new(true, false, null);

	/// <summary>The tool ran and explicitly rejected the Activation Code -- an auth fact, not a runner failure.</summary>
	public static CatalogPullResult AuthFailed(string reason)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		return new(false, true, reason);
	}

	/// <summary>The call itself could not be completed (tool missing, timeout, nonzero exit not classified as auth) -- never conflated with a real Activation Code rejection.</summary>
	public static CatalogPullResult Failed(string reason)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		return new(false, false, reason);
	}
}
