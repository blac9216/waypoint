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
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Core.Downloads;

/// <summary>
/// Issue #1464, migration 0131: <c>ConsumerViewRepository.TranslateUniqueViolation</c>
/// matches a Postgres 23505 by its constraint/index NAME
/// (<c>idx_consumer_views_name_unique</c> for a duplicate view name,
/// <c>idx_consumer_views_default_unique</c> for a second default) to raise the right
/// typed exception -- <see cref="Waypoint.Core.Downloads.ConsumerViewNameConflictException"/>
/// vs <see cref="Waypoint.Core.Downloads.ConsumerViewDefaultConflictException"/>. A
/// rename of either index in the migration, without a matching update to the
/// repository's constants, would silently fall through to an unmapped 500 instead of
/// the documented 409 -- this test scans the embedded migration text and asserts both
/// exact index names are declared, so that drift fails loudly here rather than as a
/// live 500. Table-scoped (matches only <c>consumer_views</c>'s own migration file),
/// same "grep the authoritative SQL, never a hand-copied guess" convention as
/// <c>RepoCredentialBindingConstraintDriftTests</c>/<c>VmToolsConstraintDriftTests</c>.
/// </summary>
public sealed class ConsumerViewConstraintDriftTests
{
	[Fact]
	public void Migration0131_DeclaresTheNameUniqueIndexNameTheRepositoryTranslates()
	{
		string migration = ReadMigrationSql("0131_consumer_views.sql");
		Assert.Contains("idx_consumer_views_name_unique", migration, StringComparison.Ordinal);
	}

	[Fact]
	public void Migration0131_DeclaresTheDefaultUniqueIndexNameTheRepositoryTranslates()
	{
		string migration = ReadMigrationSql("0131_consumer_views.sql");
		Assert.Contains("idx_consumer_views_default_unique", migration, StringComparison.Ordinal);
	}

	/// <summary>
	/// The default-unique index must be a PARTIAL index (<c>WHERE is_default</c>) --
	/// a plain unique index on the boolean column would forbid more than one
	/// <c>is_default = false</c> row too, breaking every other view.
	/// </summary>
	[Fact]
	public void Migration0131_DefaultUniqueIndexIsPartialOnIsDefaultTrueOnly()
	{
		string migration = ReadMigrationSql("0131_consumer_views.sql");
		Assert.Contains("idx_consumer_views_default_unique", migration, StringComparison.Ordinal);
		Assert.Contains("WHERE is_default", migration, StringComparison.Ordinal);
	}

	[Fact]
	public void Migration0131_DeclaresTheNameNotBlankCheckConstraint()
	{
		string migration = ReadMigrationSql("0131_consumer_views.sql");
		Assert.Contains("consumer_views_name_not_blank_check", migration, StringComparison.Ordinal);
	}

	private static string ReadMigrationSql(string fileName)
	{
		Assembly assembly = typeof(NpgsqlSchemaMigrator).Assembly;
		string resourceName = Assert.Single(
			assembly.GetManifestResourceNames().Where(name => name.EndsWith(fileName, StringComparison.Ordinal)));
		using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
		using StreamReader reader = new(stream);
		return reader.ReadToEnd();
	}
}
