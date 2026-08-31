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

using Waypoint.Api.Controllers;

namespace Waypoint.Tests.Api;

/// <summary>
/// PR #1649 round 1 (S2): <see cref="ContentLibrariesController.NamePattern"/> is the
/// operator-facing 400 for a content-library name and was previously untested --
/// nothing in this test suite exercised it, even though the class remarks call it "the
/// only validation standing between an operator's input and a real filesystem path".
/// It is no longer the only guard (the repository layer validates independently, see
/// <c>ContentLibraryRepositoryPathTraversalTests</c>), but it is still the first one an
/// operator hits and must reject a traversal attempt on its own.
/// </summary>
public sealed class ContentLibrariesControllerNamePatternTests
{
	[Theory]
	[InlineData("vcsp-01")]
	[InlineData("a")]
	[InlineData("A1")]
	[InlineData("name_with-mixed")]
	public void NamePattern_accepts_valid_names(string name)
	{
		Assert.Matches(ContentLibrariesController.NamePattern, name);
	}

	[Theory]
	[InlineData("../../../etc/waypoint")]
	[InlineData("../x")]
	[InlineData("/etc/waypoint")]
	[InlineData("/etc/x")]
	[InlineData("a/b")]
	[InlineData("")]
	[InlineData(".hidden")]
	[InlineData("..")]
	[InlineData(".")]
	[InlineData("trailing.")]
	[InlineData("trailing space ")]
	[InlineData(" leading-space")]
	public void NamePattern_rejects_traversal_and_invalid_forms(string name)
	{
		Assert.DoesNotMatch(ContentLibrariesController.NamePattern, name);
	}

	[Fact]
	public void NamePattern_rejects_a_name_over_63_characters()
	{
		string tooLong = new('a', 64);
		Assert.DoesNotMatch(ContentLibrariesController.NamePattern, tooLong);
	}

	[Fact]
	public void NamePattern_accepts_a_63_character_name()
	{
		string maxLength = new('a', 63);
		Assert.Matches(ContentLibrariesController.NamePattern, maxLength);
	}
}
