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
using System.Text;

namespace Waypoint.Core.ComplianceContent.SemanticImport;

/// <summary>
/// Reconciles <see cref="VendorHierarchyInterpreter"/> candidates with the closed
/// capability vocabulary (<see cref="CatalogVocabularyValidator"/>) and produces the
/// deterministic <see cref="SemanticImportReport"/> (issue #729 deliverables 4-5).
///
/// This is a pure, in-process, no-I/O pass: it does not invoke <c>inspec check</c>
/// itself (deliverable 3's bounded runner work is a separate execution-boundary
/// concern -- see this PR's body for the exact remainder) but DOES apply the structural
/// checks that do not require actually running the InSpec binary: an executable leaf
/// candidate with no <c>controls/</c> directory, or a manifest that failed to parse,
/// fails closed here rather than being silently accepted and only caught later by a
/// runner failure.
/// </summary>
public static class SemanticImportReconciler
{
	/// <summary>
	/// Reconciles one full interpretation pass into a deterministic report.
	/// <paramref name="sourceCommit"/> is the vendor repository commit this content was
	/// checked out at (issue #729 deliverable 5 "source commit/digest").
	/// </summary>
	public static SemanticImportReport Reconcile(
		string sourceCommit, VendorHierarchyInterpretation interpretation, IReadOnlyList<VendorContentEntry> entries)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceCommit);
		ArgumentNullException.ThrowIfNull(interpretation);
		ArgumentNullException.ThrowIfNull(entries);

		List<SemanticImportAccepted> accepted = [];
		List<SemanticImportWarning> warnings = [];
		List<SemanticImportRejected> rejected = [.. interpretation.Rejections
			.Select(r => new SemanticImportRejected(r.ProfileKey, r.Reason))];

		IReadOnlyDictionary<string, VendorContentEntry> entriesByKey = entries.ToDictionary(e => e.ProfileKey, StringComparer.Ordinal);

		// Duplicate ComponentKey detection within one (product version, kind) scope --
		// issue #729 AC "duplicate leaf names remain distinct and deterministic": the
		// vendor's own leaf basename may collide (e.g. two different "postgresql"
		// profiles under different products), but a documented family's component_key
		// derivation must never collapse two DISTINCT profiles onto one key within the
		// same scope. If it does, that is an interpreter-shape ambiguity, not a
		// catalog-authority decision, so both are quarantined rather than one silently
		// shadowing the other.
		Dictionary<(string ProductVersionKey, string Kind, string ComponentKey), List<SemanticCandidate>> byScope = new();
		foreach (SemanticCandidate candidate in interpretation.Candidates)
		{
			(string, string, string) scopeKey = (candidate.ProductVersionKey, candidate.Kind, candidate.ComponentKey);
			if (!byScope.TryGetValue(scopeKey, out List<SemanticCandidate>? list))
			{
				list = [];
				byScope[scopeKey] = list;
			}

			list.Add(candidate);
		}

		foreach (SemanticCandidate candidate in interpretation.Candidates.OrderBy(c => c.ProfileKey, StringComparer.Ordinal))
		{
			(string, string, string) scopeKey = (candidate.ProductVersionKey, candidate.Kind, candidate.ComponentKey);
			if (byScope[scopeKey].Count > 1)
			{
				rejected.Add(new SemanticImportRejected(candidate.ProfileKey,
					$"component_key '{candidate.ComponentKey}' collides with {byScope[scopeKey].Count - 1} other profile(s) in the same product-version/kind scope: " +
					string.Join(", ", byScope[scopeKey].Select(c => c.ProfileKey).Where(k => k != candidate.ProfileKey).OrderBy(k => k, StringComparer.Ordinal))));
				continue;
			}

			IReadOnlyList<string> vocabularyErrors = CatalogVocabularyValidator.ValidateComponent(candidate.Transport, candidate.SelectorKind, candidate.SelectorName);
			IReadOnlyList<string> kindErrors = CatalogVocabularyValidator.ValidateKind(candidate.Kind);
			if (vocabularyErrors.Count > 0 || kindErrors.Count > 0)
			{
				rejected.Add(new SemanticImportRejected(candidate.ProfileKey,
					string.Join("; ", vocabularyErrors.Concat(kindErrors))));
				continue;
			}

			if (candidate.IsExecutableLeaf)
			{
				VendorContentEntry entry = entriesByKey[candidate.ProfileKey];
				if (!entry.HasControlsDirectory || entry.ControlFileNames.Count == 0)
				{
					rejected.Add(new SemanticImportRejected(candidate.ProfileKey,
						"executable leaf profile has no controls/*.rb files -- structure validation (issue #729 deliverable 3) requires at least one control for a runnable profile"));
					continue;
				}

				if (string.IsNullOrWhiteSpace(candidate.ManifestVersion))
				{
					warnings.Add(new SemanticImportWarning(candidate.ProfileKey, "inspec.yml declares no version -- profile_version cannot be populated from source metadata"));
				}

				if (candidate.Inputs.Count == 0)
				{
					warnings.Add(new SemanticImportWarning(candidate.ProfileKey, "inspec.yml declares no inputs"));
				}
			}

			accepted.Add(new SemanticImportAccepted(candidate));
		}

		string sourceDigest = ComputeSourceDigest(sourceCommit, interpretation.Candidates);

		return new SemanticImportReport(
			sourceCommit,
			sourceDigest,
			[.. accepted.OrderBy(a => a.Candidate.ProfileKey, StringComparer.Ordinal)],
			[.. warnings.OrderBy(w => w.ProfileKey, StringComparer.Ordinal)],
			[.. rejected.OrderBy(r => r.ProfileKey, StringComparer.Ordinal)]);
	}

	/// <summary>
	/// Deterministic whole-import digest: a stable hash over every accepted candidate's
	/// own per-profile <see cref="SemanticCandidate.ContentDigest"/>, in profile-key
	/// order, plus the source commit. Two import runs over byte-identical content and
	/// the same commit always produce the same <see cref="SemanticImportReport.SourceDigest"/>
	/// regardless of filesystem enumeration order (issue #729 AC "deterministic import
	/// report").
	/// </summary>
	private static string ComputeSourceDigest(string sourceCommit, IReadOnlyList<SemanticCandidate> candidates)
	{
		StringBuilder builder = new();
		builder.Append(sourceCommit).Append('\n');
		foreach (SemanticCandidate candidate in candidates.OrderBy(c => c.ProfileKey, StringComparer.Ordinal))
		{
			builder.Append(candidate.ProfileKey).Append(':').Append(candidate.ContentDigest).Append(';');
		}

		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
		return Convert.ToHexString(hash).ToLowerInvariant();
	}
}
