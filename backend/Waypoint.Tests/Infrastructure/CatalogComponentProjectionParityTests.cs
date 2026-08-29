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
using Waypoint.Core.ComplianceContent;
using Waypoint.Infrastructure.ComplianceContent;
using Xunit;

namespace Waypoint.Tests.Infrastructure;

/// <summary>
/// Guards the defect class PR #1076 shipped and issue #743's review caught: a
/// <c>catalog_components</c> projection that selects fewer columns than
/// <c>CatalogRepository.MapComponent</c> reads back by ordinal. Nothing in the type
/// system objects -- the SQL is a string, the mapper reads by index, and the widened
/// <see cref="CatalogComponent"/> record's new parameters are defaulted -- so the
/// mismatch compiles clean, survives a rebase (the two edits sit in different regions
/// of the same file), and only appears as an IndexOutOfRangeException, i.e. an HTTP
/// 500, on whichever endpoint happens to call the stale projection.
///
/// The primary fix is structural: <c>CatalogRepository.ComponentColumnNames</c> is now
/// the single source every such projection is built from, so a projection can no
/// longer be short. These tests close the two seams that structure leaves open --
/// the list versus the record the mapper fills, and any future hand-written column
/// list creeping back into the file.
/// </summary>
public sealed class CatalogComponentProjectionParityTests
{
	/// <summary>
	/// One selected column per positional parameter of <see cref="CatalogComponent"/>.
	/// MapComponent fills the record straight from <c>offset..offset + N - 1</c>, so
	/// widening the record (as #1076 did) without widening the shared column list is
	/// exactly the mismatch that produced the 500.
	/// </summary>
	[Fact]
	public void ComponentColumnList_HasOneColumnPerMappedRecordParameter()
	{
		int recordParameters = typeof(CatalogComponent)
			.GetConstructors()
			.OrderByDescending(constructor => constructor.GetParameters().Length)
			.First()
			.GetParameters()
			.Length;

		Assert.Equal(recordParameters, CatalogRepository.ComponentColumnNames.Length);
	}

	/// <summary>
	/// No hand-written <c>catalog_components</c> column list may reappear in
	/// CatalogRepository.cs. The source is read rather than restated: a test that
	/// spells out the column list again would agree with itself no matter how far the
	/// SQL drifted -- the "test that cannot fail" pattern this repository has shipped
	/// before (see Waypoint.Infrastructure.csproj's InternalsVisibleTo note).
	/// </summary>
	[Fact]
	public void CatalogRepositorySource_BuildsEveryComponentProjectionFromTheSharedList()
	{
		string source = File.ReadAllText(RepositorySourcePath());

		// The declaration of the shared list itself is the one place the column names
		// are allowed to be literals; everything after it must interpolate.
		int declarationEnd = source.IndexOf("];", source.IndexOf("ComponentColumnNames", StringComparison.Ordinal), StringComparison.Ordinal);
		Assert.True(declarationEnd > 0, "ComponentColumnNames declaration not found");
		string body = source[declarationEnd..];

		// A read projection is recognisable by the tail of the column order the mapper
		// consumes; matching on that (rather than on any two adjacent column names)
		// leaves UpsertComponentAsync's INSERT column list -- legitimately a different,
		// narrower list of writable columns -- alone.
		Match handWritten = Regex.Match(
			body, @"(?:\w+\.)?component_key,\s*(?:\w+\.)?display_name,\s*(?:\w+\.)?transport,\s*(?:\w+\.)?selector_kind,\s*(?:\w+\.)?selector_name,\s*(?:\w+\.)?created_at");
		Assert.False(
			handWritten.Success,
			$"Hand-written catalog_components column list found in CatalogRepository.cs: '{handWritten.Value}'. "
			+ "Build the projection from CatalogRepository.ComponentColumnNames instead.");
	}

	private static string RepositorySourcePath()
	{
		string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
		while (!File.Exists(Path.Combine(directory, "Waypoint.sln")))
		{
			directory = Path.GetDirectoryName(directory)
				?? throw new InvalidOperationException("Waypoint.sln not found above the test assembly");
		}

		return Path.Combine(directory, "Waypoint.Infrastructure", "ComplianceContent", "CatalogRepository.cs");
	}
}
