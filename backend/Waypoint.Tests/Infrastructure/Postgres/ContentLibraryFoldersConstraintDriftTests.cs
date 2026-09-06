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
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1389 (migration 0113): a recurring review finding in this repo is a test
/// class NAMING a lockstep relationship without actually parsing the SQL that would
/// prove it (<see cref="RepoCredentialBindingConstraintDriftTests"/>'s own doc
/// comment cites the exact prior incident). <see cref="ContentLibraryFolderRepository"/>'s
/// cycle rejection, sibling-name-conflict handling, and cascade-delete behavior all
/// rely on specific constraints existing in 0113's SQL -- this parses the embedded
/// migration text directly rather than trusting the repository's own passing tests
/// (which would keep passing even if a constraint were silently dropped, so long as
/// no test happened to race it).
/// </summary>
public sealed class ContentLibraryFoldersConstraintDriftTests
{
	private static readonly string Migration0113 = ReadMigrationSql("0113_content_library_folders.sql");

	[Fact]
	public void ContentLibraryFolders_HasASiblingNameUniqueConstraint_CoveringNonRootFolders()
	{
		Assert.Matches(
			@"CONSTRAINT\s+content_library_folders_sibling_name_key\s+UNIQUE\s*\(\s*library_id\s*,\s*parent_folder_id\s*,\s*name\s*\)",
			Migration0113);
	}

	/// <summary>
	/// The gap a plain composite UNIQUE constraint leaves open (two root folders --
	/// both <c>parent_folder_id IS NULL</c> -- never collide under standard SQL NULL-
	/// distinctness) must be closed by a SEPARATE partial unique index, not assumed
	/// away: <see cref="ContentLibraryFolderRepositoryTests.CreateAsync_RejectsADuplicateRootName_EvenThoughParentFolderIdIsNullForBoth"/>
	/// only proves the code's current behavior, not that this migration is what
	/// causes it.
	/// </summary>
	[Fact]
	public void ContentLibraryFolders_HasAPartialUniqueIndex_ClosingTheNullParentNameGap()
	{
		Assert.Matches(
			@"CREATE\s+UNIQUE\s+INDEX\s+IF\s+NOT\s+EXISTS\s+content_library_folders_root_name_key\s*\n?\s*ON\s+content_library_folders\s*\(\s*library_id\s*,\s*name\s*\)\s*\n?\s*WHERE\s+parent_folder_id\s+IS\s+NULL",
			Migration0113);
	}

	[Fact]
	public void ContentLibraryFolders_LibraryIdForeignKey_CascadesOnLibraryDelete()
	{
		Assert.Matches(
			@"library_id\s+UUID\s+NOT\s+NULL\s+REFERENCES\s+content_libraries\s*\(\s*id\s*\)\s+ON\s+DELETE\s+CASCADE",
			Migration0113);
	}

	[Fact]
	public void ContentLibraryFolders_ParentFolderIdSelfForeignKey_CascadesOnParentDelete()
	{
		Assert.Matches(
			@"parent_folder_id\s+UUID\s+REFERENCES\s+content_library_folders\s*\(\s*id\s*\)\s+ON\s+DELETE\s+CASCADE",
			Migration0113);
	}

	[Fact]
	public void ContentLibraryItemFolders_HasAOnePerItemUniqueConstraint()
	{
		Assert.Matches(
			@"CONSTRAINT\s+content_library_item_folders_item_key\s+UNIQUE\s*\(\s*library_id\s*,\s*item_id\s*\)",
			Migration0113);
	}

	[Fact]
	public void ContentLibraryItemFolders_FolderIdForeignKey_CascadesOnFolderDelete()
	{
		Assert.Matches(
			@"folder_id\s+UUID\s+NOT\s+NULL\s+REFERENCES\s+content_library_folders\s*\(\s*id\s*\)\s+ON\s+DELETE\s+CASCADE",
			Migration0113);
	}

	/// <summary>There is deliberately NO foreign key on <c>item_id</c> -- see the migration's own header for why (no items table yet, #1396).</summary>
	[Fact]
	public void ContentLibraryItemFolders_ItemIdColumn_HasNoForeignKey()
	{
		Match itemIdColumn = Regex.Match(Migration0113, @"item_id\s+UUID\s+NOT\s+NULL[^,\n]*");
		Assert.True(itemIdColumn.Success);
		Assert.DoesNotContain("REFERENCES", itemIdColumn.Value, StringComparison.Ordinal);
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
