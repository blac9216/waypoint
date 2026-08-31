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

namespace Waypoint.Core.ContentLibraries;

/// <summary>
/// Configuration for the content-library registry (issue #1391). Matches
/// <c>Waypoint.Core.Downloads.DownloadOptions</c>'s convention: a single configurable
/// root, no volume wired in <c>deploy/compose.yaml</c> yet (deferred alongside the
/// VCSP writer, #1393, which is the first thing that actually needs the mount to
/// survive a container recreate) -- tests point this at a temp directory.
/// </summary>
public sealed class ContentLibraryOptions
{
	public const string SectionName = "ContentLibraries";

	/// <summary>
	/// Root directory every library's own directory is created directly under.
	/// <see cref="Waypoint.Infrastructure.ContentLibraries.ContentLibraryRepository"/>
	/// derives each library's disk path as <c>RootPath/{name}</c> -- the operator
	/// chooses the name, never a free-form path, so a library can never resolve
	/// outside this root and two libraries can never collide on the same directory
	/// (the DB's own unique constraint on <c>name</c> already forbids two rows
	/// sharing a leaf).
	/// </summary>
	public string RootPath { get; set; } = "/var/lib/waypoint/content-libraries";
}
