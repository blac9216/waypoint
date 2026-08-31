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
/// Wire shape of a library's <c>items.json</c> (issue #1393, research #1032 Q1):
/// always a wrapper object holding full item objects, never a bare array of stubs.
/// This is the sibling repo's other known defect (design record #37's Proposed
/// Changes: "remove-old wrote a bare array") this writer never reproduces -- and it is
/// the file the atomicity acceptance criterion is about, since a subscriber polls this
/// document directly.
/// </summary>
/// <param name="Items">Every item currently published in the library, full objects (identical to what each item's own standalone <c>item.json</c> carries).</param>
public sealed record ItemsJson(IReadOnlyList<ItemJson> Items);
