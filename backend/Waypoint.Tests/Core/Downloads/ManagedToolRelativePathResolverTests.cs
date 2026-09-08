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

using Waypoint.Core.Downloads;
using Xunit;

namespace Waypoint.Tests.Core.Downloads;

/// <summary>
/// Issue #1829: <see cref="ManagedToolRelativePathResolver"/> is now the one shared
/// implementation of the rooted/escape guard that used to be duplicated verbatim in
/// <c>BroadcomManagedToolCatalogVerifier.ResolveConfigured</c> and
/// <c>CatalogPullJobHandler.ResolveConfigured</c>, and that
/// <see cref="EsxAcquisitionOptionsPostConfigure"/> did not apply at all.
/// </summary>
public sealed class ManagedToolRelativePathResolverTests
{
	[Fact]
	public void Resolve_PlainRelativePath_CombinesUnderRoot()
	{
		string root = Path.Combine(Path.GetTempPath(), "wp-managed-tool-root");

		string resolved = ManagedToolRelativePathResolver.Resolve(root, Path.Combine("metadata", "productVersionCatalog.json"));

		Assert.Equal(Path.GetFullPath(Path.Combine(root, "metadata", "productVersionCatalog.json")), resolved);
	}

	[Theory]
	[InlineData("/etc/passwd")]
	[InlineData("C:\\Windows\\System32")]
	public void Resolve_RootedRelativeValue_ThrowsInvalidOperationException(string rooted)
	{
		string root = Path.Combine(Path.GetTempPath(), "wp-managed-tool-root");

		// Path.IsPathRooted's notion of "rooted" is platform-specific; only assert the
		// throw on a platform where this particular literal actually is rooted.
		if (!Path.IsPathRooted(rooted))
		{
			return;
		}

		InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => ManagedToolRelativePathResolver.Resolve(root, rooted));
		Assert.Contains("relative to the repository root", exception.Message);
	}

	[Fact]
	public void Resolve_EscapingRelativeValue_ThrowsInvalidOperationException()
	{
		string root = Path.Combine(Path.GetTempPath(), "wp-managed-tool-root");

		InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
			() => ManagedToolRelativePathResolver.Resolve(root, Path.Combine("..", "..", "escape.json")));
		Assert.Contains("escapes the repository root", exception.Message);
	}
}
