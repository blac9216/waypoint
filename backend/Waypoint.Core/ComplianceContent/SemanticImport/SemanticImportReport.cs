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

namespace Waypoint.Core.ComplianceContent.SemanticImport;

/// <summary>One candidate that passed hierarchy interpretation and closed-vocabulary reconciliation.</summary>
public sealed record SemanticImportAccepted(SemanticCandidate Candidate);

/// <summary>
/// One candidate that passed interpretation but carries a non-fatal warning (e.g. no
/// declared inputs, no controls directory on an executable leaf) -- still accepted, but
/// flagged for operator visibility per issue #729 deliverable 5.
/// </summary>
public sealed record SemanticImportWarning(string ProfileKey, string Message);

/// <summary>
/// One candidate/entry that failed hierarchy interpretation or closed-vocabulary
/// reconciliation and is quarantined rather than imported (issue #729 AC "unknown/new
/// layouts are quarantined with actionable diagnostics rather than guessed").
/// </summary>
public sealed record SemanticImportRejected(string ProfileKey, string Reason);

/// <summary>
/// The deterministic import report a semantic import run produces (issue #729
/// deliverable 5: "source commit/digest, accepted entries, warnings, and rejected
/// entries"). <see cref="SourceCommit"/>/<see cref="SourceDigest"/> identify exactly
/// which content revision this report describes; persisting this report
/// (<c>catalog_import_reports</c> or similar) is explicitly deferred to the follow-up
/// slice that also wires this importer into <c>ContentPullJobHandler</c> -- see this
/// PR's body for the exact remainder. Every list here is deterministically ordered
/// (profile key, ordinal) so two reports over byte-identical input are structurally
/// identical, never dependent on filesystem or dictionary enumeration order.
/// </summary>
public sealed record SemanticImportReport(
	string SourceCommit,
	string SourceDigest,
	IReadOnlyList<SemanticImportAccepted> Accepted,
	IReadOnlyList<SemanticImportWarning> Warnings,
	IReadOnlyList<SemanticImportRejected> Rejected);
