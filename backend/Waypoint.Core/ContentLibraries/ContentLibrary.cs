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
/// One named VCSP content library (migration 0090, issue #1391, epic #1185, design
/// record #16 section 6): a unique name and the single flat-on-disk directory it owns
/// -- "flat" meaning this library IS one directory, never a directory tree of nested
/// libraries (a sub-issue's items live inside it, not another <see cref="ContentLibrary"/>).
/// Deliberately inert: this record carries no VCSP file state (<c>lib.json</c>/
/// <c>items.json</c>) -- that is #1393's remainder, written into the directory this
/// row names.
/// </summary>
/// <param name="Id">Stable identity, never reused.</param>
/// <param name="Name">Operator-chosen, unique, and the directory's own leaf name under <see cref="ContentLibraryOptions.RootPath"/> -- see that type for why the disk path is derived, not freely chosen.</param>
/// <param name="DiskPath">Absolute path of the one directory this library owns. Never shared with any other library row.</param>
/// <param name="CreatedAt">Row insert time.</param>
/// <param name="UpdatedAt">Row's own <c>updated_at</c> trigger value -- unused until a future rename/description slice, present now only for symmetry with every other registry table in this codebase.</param>
public sealed record ContentLibrary(Guid Id, string Name, string DiskPath, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
