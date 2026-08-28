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

		// Build the lookup defensively: ToDictionary throws ArgumentException on a
		// duplicate ProfileKey in the raw entry list (a filesystem-walk artifact, e.g. a
		// case-collision or a re-enumerated path), which would abort the entire reconcile.
		// First-writer-wins keeps this pass fail-soft; the interpreter's own collision
		// guard already quarantines genuinely ambiguous component keys.
		Dictionary<string, VendorContentEntry> entriesByKey = new(StringComparer.Ordinal);
		foreach (VendorContentEntry sourceEntry in entries)
		{
			entriesByKey.TryAdd(sourceEntry.ProfileKey, sourceEntry);
		}

		// Duplicate ComponentKey detection within one (product version, kind) scope --
		// issue #729 AC "duplicate leaf names remain distinct and deterministic": the
		// vendor's own leaf basename may collide (e.g. two different "postgresql"
		// profiles under different products), but a documented family's component_key
		// derivation must never collapse two DISTINCT profiles onto one key within the
		// same scope. If it does, that is an interpreter-shape ambiguity, not a
		// catalog-authority decision, so both are quarantined rather than one silently
		// shadowing the other -- UNLESS the collision is entirely explained by the
		// candidates being DIFFERENT releases of the SAME component, in which case issue
		// #986's owner decision applies (see ResolveScope below): the newest release
		// promotes and older releases quarantine by name rather than both being rejected.
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

		// Resolve each multi-candidate scope ONCE (not per-candidate) so the winner is
		// determined deterministically regardless of the outer profile-key iteration
		// order below; the result is a lookup from ProfileKey to its resolution.
		Dictionary<string, ScopeResolution> resolutionsByProfileKey = new(StringComparer.Ordinal);
		foreach (KeyValuePair<(string ProductVersionKey, string Kind, string ComponentKey), List<SemanticCandidate>> scope in byScope)
		{
			if (scope.Value.Count <= 1)
			{
				continue;
			}

			foreach ((string profileKey, ScopeResolution resolution) in ResolveScope(scope.Key.ComponentKey, scope.Value))
			{
				resolutionsByProfileKey[profileKey] = resolution;
			}
		}

		foreach (SemanticCandidate candidate in interpretation.Candidates.OrderBy(c => c.ProfileKey, StringComparer.Ordinal))
		{
			if (resolutionsByProfileKey.TryGetValue(candidate.ProfileKey, out ScopeResolution resolution))
			{
				if (resolution.Reason is not null)
				{
					rejected.Add(new SemanticImportRejected(candidate.ProfileKey, resolution.Reason));
					continue;
				}

				// Winner: falls through to the normal vocabulary/structure gates below so
				// it flows through the SAME promotion path as any non-colliding candidate
				// (issue #986 AC "the chosen release must actually promote").
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
				// Guard the lookup rather than indexing blindly: a candidate whose
				// ProfileKey has no matching source entry is a structural inconsistency,
				// not an executable leaf, so it fails closed into quarantine rather than
				// throwing KeyNotFoundException and aborting the whole reconcile (same
				// unguarded-indexing class as the interpreter slice crash). Under normal
				// operation every candidate derives from an entry, so this never fires;
				// it exists so a future upstream shape change degrades to quarantine.
				if (!entriesByKey.TryGetValue(candidate.ProfileKey, out VendorContentEntry? entry))
				{
					rejected.Add(new SemanticImportRejected(candidate.ProfileKey,
						"executable leaf candidate has no matching source content entry -- cannot validate controls/ structure, quarantined"));
					continue;
				}

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

	/// <summary>
	/// Resolves one multi-candidate (product-version, kind, component_key) scope per
	/// issue #986's owner decision (2026-08-28): if every candidate's release parses
	/// under the closed <see cref="VendorReleaseOrder"/> vocabulary and all releases
	/// share the same form, the newest release wins -- it is returned with a
	/// <see langword="null"/> rejection reason (so it falls through to the normal
	/// promotion gates) and every older release is returned with a "superseded by"
	/// reason. If two releases TIE under the same form's ordering (issue #729's original
	/// same-release shape-ambiguity class), the whole scope fails closed into the
	/// original component_key-collides reason BYTE-FOR-BYTE, exactly as before issue
	/// #986. If ANY release fails to parse, or the releases present span both closed
	/// forms (an unresolved cross-form design hole -- see <see cref="VendorReleaseOrder"/>
	/// remarks), the scope also fails closed into that reason, extended with a
	/// parenthesized diagnostic naming what could not be ordered (these two classes are
	/// new with #986 and never produced the collision reason before it).
	/// Deterministic regardless of the input list's order: candidates are re-sorted by
	/// ProfileKey before any tie-break.
	/// </summary>
	private static Dictionary<string, ScopeResolution> ResolveScope(string componentKey, List<SemanticCandidate> scopeCandidates)
	{
		List<SemanticCandidate> ordered = [.. scopeCandidates.OrderBy(c => c.ProfileKey, StringComparer.Ordinal)];
		Dictionary<string, ScopeResolution> result = new(StringComparer.Ordinal);

		List<(SemanticCandidate Candidate, ParsedRelease Release)> parsed = [];
		foreach (SemanticCandidate candidate in ordered)
		{
			if (!VendorReleaseOrder.TryParse(candidate.ReleaseKey, out ParsedRelease release))
			{
				return GenericCollision(componentKey, ordered,
					$"release '{candidate.ReleaseKey}' does not match either closed release-ordering form (V#R# or Y##M##-srg) -- release ordering cannot be determined, quarantined");
			}

			parsed.Add((candidate, release));
		}

		bool mixedForms = parsed.Select(p => p.Release.Form).Distinct().Count() > 1;
		if (mixedForms)
		{
			return GenericCollision(componentKey, ordered,
				"cross-form release tie within the same product-version/kind/component scope (both V#R# and Y##M##-srg releases present) -- " +
				"cross-form release ordering is an unresolved design hole (issue #986), quarantined rather than guessed");
		}

		(SemanticCandidate Candidate, ParsedRelease Release) winner = parsed[0];
		foreach ((SemanticCandidate Candidate, ParsedRelease Release) candidate in parsed.Skip(1))
		{
			int comparison = VendorReleaseOrder.Compare(candidate.Release, winner.Release);
			if (comparison == 0)
			{
				// Two DISTINCT profiles resolving to the same (product-version, kind,
				// component_key) scope AND the same ordering position (whether the
				// literal release key is identical or two different keys parse to an
				// equal ordinal position) is a genuine shape ambiguity, not a
				// release-supersession case -- "newest wins" presumes a total order
				// with no ties. Fail closed exactly like the pre-#986 behavior --
				// including its reason string BYTE-FOR-BYTE (null detail), since this
				// class (issue #729's same-release collision) existed before #986 and
				// its reason is a pinned, load-bearing string.
				return GenericCollision(componentKey, ordered, detail: null);
			}

			if (comparison > 0)
			{
				winner = candidate;
			}
		}

		result[winner.Candidate.ProfileKey] = new ScopeResolution(null);
		foreach ((SemanticCandidate Candidate, ParsedRelease Release) candidate in parsed)
		{
			if (candidate.Candidate.ProfileKey == winner.Candidate.ProfileKey)
			{
				continue;
			}

			result[candidate.Candidate.ProfileKey] = new ScopeResolution(
				$"superseded by release '{winner.Candidate.ReleaseKey}' (profile '{winner.Candidate.ProfileKey}') -- newest release wins within one declared version scope (issue #986)");
		}

		return result;
	}

	/// <summary>
	/// Fail-closed fallback for every candidate in the scope when release ordering
	/// cannot pick a winner. A <see langword="null"/> <paramref name="detail"/> emits
	/// the original (pre-#986) collision reason BYTE-FOR-BYTE -- used for same-form
	/// ordering ties, which include issue #729's original same-release shape-ambiguity
	/// class, so that pre-existing reason string stays pinned exactly. A non-null
	/// <paramref name="detail"/> appends a parenthesized diagnostic and is used only for
	/// the two fail-closed classes issue #986 introduced (unparseable release form,
	/// cross-form tie), which never produced this reason before #986 at all.
	/// </summary>
	private static Dictionary<string, ScopeResolution> GenericCollision(string componentKey, List<SemanticCandidate> ordered, string? detail)
	{
		Dictionary<string, ScopeResolution> result = new(StringComparer.Ordinal);
		string suffix = detail is null ? string.Empty : $" ({detail})";
		foreach (SemanticCandidate candidate in ordered)
		{
			result[candidate.ProfileKey] = new ScopeResolution(
				$"component_key '{componentKey}' collides with {ordered.Count - 1} other profile(s) in the same product-version/kind scope: " +
				string.Join(", ", ordered.Select(c => c.ProfileKey).Where(k => k != candidate.ProfileKey).OrderBy(k => k, StringComparer.Ordinal)) +
				suffix);
		}

		return result;
	}

	/// <summary>
	/// One candidate's resolution within a multi-candidate scope: <see cref="Reason"/>
	/// null means this candidate is the scope's winner (falls through to normal
	/// promotion gates); non-null is the quarantine reason for a superseded/colliding
	/// candidate.
	/// </summary>
	private readonly record struct ScopeResolution(string? Reason);
}
