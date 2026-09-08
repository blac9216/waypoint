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
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
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
/// guard for both vocabularies, but the two tests below resolve the authoritative
/// value set two different ways (PR #1832 review round 1, F6):
/// <see cref="RepoStoresAll_EqualsRepoCredentialBindingsStoreCheckConstraintValueSet"/>
/// parses it straight out of the embedded migration SQL (the
/// <c>SchemaMigrationTests.Migration0050_/Migration0051_CheckConstraintValueList(s)_
/// MatchTheCSharpClosedVocabulary</c>/<c>OciBundleStatusesConstraintDriftTests</c>
/// convention), while
/// <see cref="CredentialTypesAll_EqualsCredentialsCredentialTypeCheckConstraintValueSet"/>
/// reads it live out of <c>pg_get_constraintdef</c> after actually applying every
/// migration (issue #1660) -- see that test's own doc comment for why. Both compare
/// in DECLARATION order, not as unordered sets: 0103's own <c>ADD CONSTRAINT</c>
/// literal for <c>credentials_credential_type_check</c> lists its values in exactly
/// <see cref="CredentialTypes.All"/>'s order, and Postgres's
/// <c>pg_get_constraintdef</c> renders an <c>ARRAY[...]</c> CHECK in the order it was
/// declared, so an ordered comparison is available on this side too and is preferred
/// over an unordered one -- losing order (and duplicate-value) drift detection would
/// be a real coverage regression for no gain, since neither guard needs to tolerate a
/// live database that reorders the array.
/// </summary>
[Collection("Postgres")]
public sealed class RepoCredentialBindingConstraintDriftTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;

	public RepoCredentialBindingConstraintDriftTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public void RepoStoresAll_EqualsRepoCredentialBindingsStoreCheckConstraintValueSet()
	{
		string migration0103 = ReadMigrationSql("0103_repo_credential_purpose.sql");

		Assert.Equal(RepoStores.All, ParseCheckInList(migration0103, "repo_credential_bindings_store_check"));
	}

	/// <summary>
	/// <see cref="CredentialTypes"/>' backing CHECK (<c>credentials_credential_type_check</c>)
	/// has been widened twice via the repo's DROP/ADD idiom (0022 -&gt; 0047 -&gt; 0103).
	/// Issue #1660: parsing embedded migration files in ordinal-filename order and
	/// keeping the last declaration (the approach this test used to take, and that
	/// <c>OciBundleStatusesConstraintDriftTests</c> still uses) silently picks the
	/// wrong declaration once a migration is slotted BELOW an already-merged
	/// declaration but applied to an existing deployment AFTER it (this repo reserves
	/// low slots for concurrently in-flight work, so ordinal file order is not always
	/// application order -- see #1660's Motivation). Reading the CHECK definition back
	/// out of <c>pg_constraint</c> after actually applying every embedded migration,
	/// via <see cref="NpgsqlSchemaMigrator"/>, in its real applied order removes the
	/// ordering-of-MIGRATIONS question entirely: this asserts against what the
	/// fully-migrated database actually enforces, not a guess about which file "looks
	/// latest". Separately, <c>pg_get_constraintdef</c> renders the CHECK's own
	/// <c>ARRAY[...]</c> literal in the order it was declared (PR #1832 review round
	/// 1, F6), so the VALUE list itself is still compared in order against
	/// <see cref="CredentialTypes.All"/>, exactly as
	/// <see cref="RepoStoresAll_EqualsRepoCredentialBindingsStoreCheckConstraintValueSet"/>
	/// compares its own value list -- reading the definition live only removes the
	/// migration-ordering hazard, it does not trade away value-order/duplicate drift
	/// detection.
	/// </summary>
	[Fact]
	public async Task CredentialTypesAll_EqualsCredentialsCredentialTypeCheckConstraintValueSet()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand command = new(
			"""
			SELECT pg_get_constraintdef(oid) FROM pg_constraint
			WHERE conname = 'credentials_credential_type_check' AND conrelid = 'credentials'::regclass
			""", connection);

		string? definition = (string?)await command.ExecuteScalarAsync();
		Assert.NotNull(definition);

		List<string> schemaTypes = [.. Regex
			.Matches(definition!, "'([^']+)'::text", RegexOptions.None, TimeSpan.FromSeconds(5))
			.Select(match => match.Groups[1].Value)];

		Assert.NotEmpty(schemaTypes);
		Assert.Equal(CredentialTypes.All, schemaTypes);
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
	/// This helper is safe for <c>repo_credential_bindings_store_check</c> because that
	/// constraint has exactly one declaration (0103) -- no "latest across migrations"
	/// resolution is needed, so the ordering hazard #1660 fixed for the widened
	/// <c>credentials_credential_type_check</c> guard above does not apply here.
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
}
