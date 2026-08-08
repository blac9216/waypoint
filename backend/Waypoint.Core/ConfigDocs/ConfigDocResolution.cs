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

namespace Waypoint.Core.ConfigDocs;

/// <summary>
/// One (kind, profile, target) slot's resolved value -- the EFFECTIVE card
/// (docs/api-contract.md `/config-docs/resolve`: "resolved value + supplying layer").
/// <see cref="Layer"/> is the layer that actually supplied <see cref="Body"/> (the
/// most-specific non-empty layer among global/site/target -- docs/domain-model.md: "most
/// specific wins ... not a tighten-only relationship"), or null when no layer has a doc
/// for this (kind, profile) at all.
/// </summary>
public sealed record ConfigDocResolution(
	string Kind,
	string Profile,
	string? Layer,
	string? Body,
	Guid? DocId,
	int? Version,
	string? Author,
	DateTimeOffset? UpdatedAt,
	bool AttestationExpired,
	DateTimeOffset? AttestationExpiresAt);

/// <summary>
/// Resolves the three-layer Global -> Site -> Target config-doc stack for a single
/// target, most-specific-wins, per docs/domain-model.md "STIG configuration documents"
/// and the #266 AC. Pure and dependency-free so it is unit-testable without Postgres --
/// callers (the controller) fetch the up-to-three candidate versions and hand them here.
/// </summary>
public static class ConfigDocResolver
{
	/// <summary>
	/// Picks the most specific of <paramref name="global"/>/<paramref name="site"/>/
	/// <paramref name="target"/> that has a doc at all (target wins over site wins over
	/// global -- "not tighten-only": a lower layer's *presence* wins outright, it does not
	/// merge field-by-field with a higher layer). For <c>kind == "attestation"</c>, an
	/// expired waiver (its YAML's top-level <c>expires</c> date is on or before
	/// <paramref name="now"/>) is treated as if that layer had no doc, and resolution
	/// falls through to the next-less-specific layer that does -- docs/domain-model.md:
	/// "Expired attestations are not applied: the control reports Open ... A lapsed waiver
	/// must never be applied silently." <see cref="ConfigDocResolution.AttestationExpired"/>
	/// still reports the expiry against whichever layer's doc was actually skipped, even
	/// when a less-specific layer's doc ends up supplying the effective value.
	/// </summary>
	public static ConfigDocResolution Resolve(
		string kind,
		string profile,
		ConfigDocWithLatestVersion? global,
		ConfigDocWithLatestVersion? site,
		ConfigDocWithLatestVersion? target,
		DateTimeOffset now)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(kind);
		ArgumentException.ThrowIfNullOrWhiteSpace(profile);

		bool isAttestation = kind == ConfigDocKinds.Attestation;
		bool attestationExpired = false;
		DateTimeOffset? attestationExpiresAt = null;

		foreach (ConfigDocWithLatestVersion? candidate in new[] { target, site, global })
		{
			if (candidate is null)
			{
				continue;
			}

			if (isAttestation)
			{
				DateTimeOffset? expiresAt = AttestationYaml.TryGetExpiresAt(candidate.LatestVersion.BodyYaml);
				if (expiresAt.HasValue && expiresAt.Value <= now)
				{
					// This layer's waiver has lapsed -- it must never apply silently. Record
					// the first (most specific) expiry we saw and keep falling through.
					attestationExpired = true;
					attestationExpiresAt ??= expiresAt;
					continue;
				}
			}

			return new ConfigDocResolution(
				kind, profile, FormatLayer(candidate.Doc), candidate.LatestVersion.BodyYaml,
				candidate.Doc.Id, candidate.LatestVersion.Version, candidate.LatestVersion.Author,
				candidate.Doc.UpdatedAt, attestationExpired, attestationExpiresAt);
		}

		return new ConfigDocResolution(kind, profile, null, null, null, null, null, null, attestationExpired, attestationExpiresAt);
	}

	private static string FormatLayer(ConfigDoc doc) =>
		doc.LayerRef.HasValue ? $"{doc.LayerType}:{doc.LayerRef}" : doc.LayerType;
}
