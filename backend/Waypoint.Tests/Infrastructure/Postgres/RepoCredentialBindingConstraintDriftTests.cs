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

using System.Reflection;
using System.Text.RegularExpressions;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// PR review round 1 (issue #1517, PR #1650) finding 2: migration 0103's comment
/// claims <c>repo_credential_bindings_store_check</c> and (the widened)
/// <c>credentials_credential_type_check</c> are "kept in lockstep" with
/// <see cref="RepoStores"/>/<see cref="CredentialTypes"/> "by
/// RepoCredentialBindingRepositoryTests" -- but that test class only ever asserted
/// its OWN hand-copied <c>[InlineData]</c> constants, not the constraint's actual
/// value list, so a value added to either C# set without a matching migration edit
/// (or vice versa) passed silently. This is this repo's real class-killing drift
/// guard for both vocabularies, the same convention as
/// <c>SchemaMigrationTests.Migration0050_/Migration0051_CheckConstraintValueList(s)_
/// MatchTheCSharpClosedVocabulary</c> and <c>OciBundleStatusesConstraintDriftTests</c>:
/// parse the authoritative value set out of the embedded migration SQL and assert it
/// equals the C# constant, in order, so adding/removing a value on either side without
/// the other fails here.
/// </summary>
public sealed class RepoCredentialBindingConstraintDriftTests
{
	[Fact]
	public void RepoStoresAll_EqualsRepoCredentialBindingsStoreCheckConstraintValueSet()
	{
		string migration0103 = ReadMigrationSql("0103_repo_credential_purpose.sql");

		Assert.Equal(RepoStores.All, ParseCheckInList(migration0103, "repo_credential_bindings_store_check"));
	}

	/// <summary>
	/// <see cref="CredentialTypes"/>' backing CHECK (<c>credentials_credential_type_check</c>)
	/// has been widened twice via the repo's DROP/ADD idiom (0022 -&gt; 0047 -&gt; 0103), so
	/// unlike the single-declaration store check above, the authoritative value list is
	/// whichever migration declared it LAST across the fully-migrated database -- the same
	/// "scan every embedded migration, keep the latest declaration" approach
	/// <c>OciBundleStatusesConstraintDriftTests</c> uses for its own widened constraint.
	/// </summary>
	[Fact]
	public void CredentialTypesAll_EqualsCredentialsCredentialTypeCheckConstraintValueSet()
	{
		List<string> constraintValues = ParseLatestCheckAcrossMigrations("credentials_credential_type_check", "credential_type");

		Assert.Equal(CredentialTypes.All, constraintValues);
	}

	/// <summary>The raw text of one embedded migration resource, matched by its filename suffix.</summary>
	private static string ReadMigrationSql(string fileName)
	{
		Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string resourceName = Assert.Single(
			assembly.GetManifestResourceNames().Where(name => name.EndsWith(fileName, StringComparison.Ordinal)));
		using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
		using StreamReader reader = new(stream);
		return reader.ReadToEnd();
	}

	/// <summary>
	/// Extracts the single-quoted value list of a named <c>CONSTRAINT ... CHECK (col IN
	/// ('a', 'b', ...))</c> from migration SQL, in file order (matching the C# constants'
	/// own declaration order -- unlike <c>SchemaMigrationTests.ParseCheckInList</c>, this
	/// does NOT sort, since <see cref="RepoStores.All"/> is asserted in declaration order).
	/// </summary>
	private static List<string> ParseCheckInList(string sql, string constraintName)
	{
		Match constraint = Regex.Match(
			sql,
			$@"CONSTRAINT\s+{Regex.Escape(constraintName)}\s+CHECK\s*\([^)]*\bIN\s*\(([^)]*)\)",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);
		Assert.True(constraint.Success, $"Could not locate an IN-list CHECK named '{constraintName}'.");

		MatchCollection values = Regex.Matches(constraint.Groups[1].Value, "'([^']*)'");
		Assert.NotEmpty(values);
		return [.. values.Select(m => m.Groups[1].Value)];
	}

	/// <summary>
	/// Reads every embedded <c>Data/Migrations/*.sql</c> resource in migration order
	/// (ordinal on the zero-padded filename prefix, matching
	/// <see cref="NpgsqlSchemaMigrator"/>) and returns the value list of the LAST
	/// <paramref name="constraintName"/> CHECK constraint declared across them -- i.e.
	/// the constraint the fully-migrated database actually enforces.
	/// </summary>
	private static List<string> ParseLatestCheckAcrossMigrations(string constraintName, string columnName)
	{
		Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string[] resourceNames = [.. assembly.GetManifestResourceNames()
			.Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
			.OrderBy(name => name, StringComparer.Ordinal)];

		Regex checkPattern = new(
			$@"CONSTRAINT\s+{Regex.Escape(constraintName)}\s+CHECK\s*\(\s*{Regex.Escape(columnName)}\s+IN\s*\((?<values>[^)]*)\)",
			RegexOptions.IgnoreCase | RegexOptions.Singleline);
		Regex valuePattern = new(@"'(?<v>[^']*)'", RegexOptions.Singleline);

		List<string>? latest = null;
		foreach (string resourceName in resourceNames)
		{
			using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
			using StreamReader reader = new(stream);
			string sql = reader.ReadToEnd();

			foreach (Match match in checkPattern.Matches(sql))
			{
				latest = [.. valuePattern.Matches(match.Groups["values"].Value).Select(m => m.Groups["v"].Value)];
			}
		}

		Assert.NotNull(latest);
		Assert.NotEmpty(latest!);
		return latest!;
	}
}
