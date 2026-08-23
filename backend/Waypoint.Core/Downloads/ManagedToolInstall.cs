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
/// One row of the append-only <c>managed_tool_installs</c> ledger (issue #39, migration
/// 0037) -- one attempt to install the operator-gated <c>vcf-download-tool</c> onto the
/// managed-tool volume, whatever the outcome. Never updated after insert; a retry is a
/// new row, not a mutation of this one.
/// </summary>
public sealed record ManagedToolInstall(
	Guid Id,
	string Source,
	string SourcePath,
	string? Version,
	string? Sha256,
	string Outcome,
	string? RejectedReason,
	string InitiatedBy,
	Guid? JobId,
	DateTimeOffset CreatedAt);

/// <summary>The exact string values of <c>managed_tool_installs.source</c>, matching <c>managed_tool_installs_source_check</c>.</summary>
public static class ManagedToolInstallSources
{
	/// <summary>Issue #39 path 1: the operator-provisioned local indexed repository (<see cref="ManagedToolOptions.LocalRepositoryPath"/>). Works in both connected and disconnected mode.</summary>
	public const string LocalRepository = "local-repository";

	/// <summary>Issue #39 path 2: fetched live from the Broadcom depot using the depot-token credential. Connected mode only.</summary>
	public const string Depot = "depot";

	/// <summary>Issue #39 path 3: an operator-supplied manual upload staged via <c>POST /downloads/tool/upload</c>.</summary>
	public const string Upload = "upload";
}

/// <summary>The exact string values of <c>managed_tool_installs.outcome</c>, matching <c>managed_tool_installs_outcome_check</c>.</summary>
public static class ManagedToolInstallOutcomes
{
	/// <summary>Signature verified; the artifact was copied into <see cref="ManagedToolOptions.ToolStatePath"/> and is now the active tool.</summary>
	public const string Installed = "installed";

	/// <summary>Signature verification failed -- the candidate artifact was never copied into the managed-tool volume. Recorded, never silently dropped (issue #39 acceptance criterion).</summary>
	public const string Rejected = "rejected";

	/// <summary>Attempt failed for a reason other than signature rejection (missing source file, I/O error, etc.).</summary>
	public const string Failed = "failed";
}

/// <summary>A candidate install attempt to append to the ledger, before the outcome is known -- <see cref="IManagedToolInstallRepository.RecordAsync"/>'s input.</summary>
public sealed record ManagedToolInstallAttempt(
	string Source,
	string SourcePath,
	string? Version,
	string? Sha256,
	string Outcome,
	string? RejectedReason,
	string InitiatedBy,
	Guid? JobId);
