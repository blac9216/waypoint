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
using Waypoint.Core.Downloads;
using Xunit;

namespace Waypoint.Tests.Core.Downloads;

/// <summary>
/// Issue #1480 AC3: the model's etag field is a documented, non-checksum change
/// token, and no code path in this issue treats it as a hash for integrity purposes.
/// Two independent guards: a type-level one (<see cref="VksLibraryItem.Etag"/> is
/// <see cref="VksChangeToken"/>, a distinct record type with no equality or implicit
/// conversion path to a <c>string</c> hash value -- a comparison against
/// <see cref="VksLibraryItem.Sha256"/> is therefore a compile error, not a runtime
/// bug), and a grep-based guard over this issue's own new source files as a second,
/// independent line of defense in case a future change widens the type.
/// </summary>
public sealed class VksChangeTokenGuardTests
{
	[Fact]
	public void Etag_IsADistinctChangeTokenType_NotAStringOrAnyHashRepresentation()
	{
		PropertyInfo etagProperty = typeof(VksLibraryItem).GetProperty(nameof(VksLibraryItem.Etag))!;
		PropertyInfo sha256Property = typeof(VksLibraryItem).GetProperty(nameof(VksLibraryItem.Sha256))!;

		// Etag's underlying type is VksChangeToken, never string -- so it cannot be
		// assigned from, compared against, or accidentally interchanged with Sha256
		// (a plain string?) without an explicit, deliberate conversion that does not
		// exist anywhere in this model.
		Assert.Equal(typeof(VksChangeToken), Nullable.GetUnderlyingType(etagProperty.PropertyType) ?? etagProperty.PropertyType);
		Assert.Equal(typeof(string), Nullable.GetUnderlyingType(sha256Property.PropertyType) ?? sha256Property.PropertyType);
		Assert.NotEqual(etagProperty.PropertyType, sha256Property.PropertyType);

		// VksChangeToken carries no user-defined equality/conversion operator to
		// string or to any hash-shaped type: it is a plain single-string-field
		// record, which gives it record-generated equality only against its own
		// type.
		MethodInfo[] operators = [.. typeof(VksChangeToken).GetMethods(BindingFlags.Public | BindingFlags.Static)
			.Where(m => m.Name is "op_Implicit" or "op_Explicit")];
		Assert.Empty(operators);
	}

	/// <summary>
	/// Scans this issue's own new source files for a direct comparison between an
	/// etag-named and a sha256/hash-named identifier -- the shape a future edit
	/// reintroducing the #1031-documented "etag treated as checksum" bug would take,
	/// independent of whether the type system alone would already have caught it.
	/// </summary>
	[Fact]
	public void NoNewSourceFile_ComparesEtagDirectlyAgainstASha256OrHashIdentifier()
	{
		string repoRoot = FindRepoRoot();
		string[] filesToScan =
		[
			Path.Combine(repoRoot, "backend/Waypoint.Core/Downloads/VksLibraryItem.cs"),
			Path.Combine(repoRoot, "backend/Waypoint.Core/Downloads/IVksLibraryIndexRepository.cs"),
			Path.Combine(repoRoot, "backend/Waypoint.Core/Downloads/IVksItemNameGrammarParser.cs"),
			Path.Combine(repoRoot, "backend/Waypoint.Infrastructure/Downloads/VksItemNameGrammarParser.cs"),
			Path.Combine(repoRoot, "backend/Waypoint.Infrastructure/Downloads/VksLibraryIndexRepository.cs"),
		];

		System.Text.RegularExpressions.Regex suspiciousComparison = new(
			@"(Etag|etag)\s*(==|!=|\.Equals)\s*\w*(Sha256|sha256|Hash|hash)|(Sha256|sha256|Hash|hash)\s*(==|!=|\.Equals)\s*\w*(Etag|etag)",
			System.Text.RegularExpressions.RegexOptions.None);

		foreach (string file in filesToScan)
		{
			Assert.True(File.Exists(file), $"Expected file not found: {file}");
			string content = File.ReadAllText(file);
			Assert.False(suspiciousComparison.IsMatch(content), $"{file} appears to compare an etag value directly against a sha256/hash value.");
		}
	}

	private static string FindRepoRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
		{
			directory = directory.Parent;
		}

		Assert.NotNull(directory);
		return directory!.FullName;
	}
}
