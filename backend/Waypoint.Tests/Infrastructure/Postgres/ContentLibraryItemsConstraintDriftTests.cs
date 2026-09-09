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

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1396 (migration 0133): scoped to <c>content_library_items</c> and the FK it
/// adds onto <c>content_library_item_folders.item_id</c> -- the name-only scan class
/// shape this repo has been burned by three times (see
/// <see cref="ContentLibraryFoldersConstraintDriftTests"/>'s own doc comment); this
/// parses the embedded migration text directly rather than trusting the repository's
/// own passing tests.
/// </summary>
public sealed class ContentLibraryItemsConstraintDriftTests
{
	private static readonly string Migration0133 = ReadMigrationSql("0133_content_library_items.sql");

	[Fact]
	public void ContentLibraryItems_HasADirectoryNameUniqueConstraint_PerLibrary()
	{
		Assert.Matches(
			@"CONSTRAINT\s+content_library_items_directory_key\s+UNIQUE\s*\(\s*library_id\s*,\s*directory_name\s*\)",
			Migration0133);
	}

	[Fact]
	public void ContentLibraryItems_LibraryIdForeignKey_CascadesOnLibraryDelete()
	{
		Assert.Matches(
			@"library_id\s+UUID\s+NOT\s+NULL\s+REFERENCES\s+content_libraries\s*\(\s*id\s*\)\s+ON\s+DELETE\s+CASCADE",
			Migration0133);
	}

	[Fact]
	public void ContentLibraryItems_HasANamedTypeCheckConstraint_MatchingTheClosedVocabulary()
	{
		Assert.Matches(
			@"CONSTRAINT\s+content_library_items_type_check\s+CHECK\s*\(\s*type\s+IN\s*\(\s*'vcsp\.ovf'\s*,\s*'vcsp\.iso'\s*,\s*'vcsp\.other'\s*\)\s*\)",
			Migration0133);
	}

	[Fact]
	public void ContentLibraryItems_HasAPositiveVersionCheck()
	{
		Assert.Matches(
			@"CONSTRAINT\s+content_library_items_version_positive\s+CHECK\s*\(\s*version\s*>\s*0\s*\)",
			Migration0133);
	}

	/// <summary>The FK gap 0113 deliberately left open on <c>content_library_item_folders.item_id</c> is closed here -- see 0133's own header for the CASCADE-not-RESTRICT rationale.</summary>
	[Fact]
	public void ContentLibraryItemFolders_ItemIdForeignKey_IsAddedAndCascadesOnItemDelete()
	{
		Assert.Matches(
			@"ALTER\s+TABLE\s+content_library_item_folders\s*\n?\s*ADD\s+CONSTRAINT\s+content_library_item_folders_item_id_fkey\s*\n?\s*FOREIGN\s+KEY\s*\(\s*item_id\s*\)\s+REFERENCES\s+content_library_items\s*\(\s*id\s*\)\s+ON\s+DELETE\s+CASCADE",
			Migration0133);
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
