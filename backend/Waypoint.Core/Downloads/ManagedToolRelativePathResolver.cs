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

namespace Waypoint.Core.Downloads;

/// <summary>
/// Resolves a configured path that is documented as "relative to
/// <see cref="ManagedToolOptions.LocalRepositoryPath"/>" (e.g.
/// <see cref="ManagedToolOptions.ProductVersionCatalogPath"/>,
/// <see cref="ManagedToolOptions.ProductVersionCatalogSignaturePath"/>) against its
/// root, rejecting a rooted value or one that combines to escape the root -- issue
/// #1829: this rule previously existed as two byte-identical private copies
/// (<c>BroadcomManagedToolCatalogVerifier.ResolveConfigured</c>,
/// <c>CatalogPullJobHandler.ResolveConfigured</c>) and a third consumer of the same
/// option pair (<see cref="EsxAcquisitionOptionsPostConfigure"/>) applied neither
/// check, so a rooted or <c>../</c>-escaping <c>ManagedTool:ProductVersionCatalogPath</c>
/// was validated differently depending on which consumer read it. This is the one
/// shared implementation all three call.
/// </summary>
public static class ManagedToolRelativePathResolver
{
	/// <summary>
	/// Combines <paramref name="root"/> and <paramref name="relative"/>, throwing
	/// <see cref="InvalidOperationException"/> when <paramref name="relative"/> is
	/// rooted or when the combined path resolves outside <paramref name="root"/>.
	/// </summary>
	public static string Resolve(string root, string relative)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(root);
		ArgumentException.ThrowIfNullOrWhiteSpace(relative);

		if (Path.IsPathRooted(relative))
		{
			throw new InvalidOperationException("Managed-tool catalog paths must be relative to the repository root.");
		}

		string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		string candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
		if (!candidate.StartsWith(fullRoot, StringComparison.Ordinal))
		{
			throw new InvalidOperationException("Managed-tool catalog path escapes the repository root.");
		}

		return candidate;
	}
}
