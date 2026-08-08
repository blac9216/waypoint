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

namespace Waypoint.Core.SystemState;

/// <summary>
/// Disk-usage figures for one artifact store, in bytes, as of the moment of the read
/// (issue #226). <see cref="TotalBytes"/> is the volume/filesystem's total capacity,
/// not the store's own consumption -- Waypoint does not have exclusive use of the
/// mounted volume in every deployment, so "free" is what the filesystem reports, not a
/// quota derived from the store's own contents.
/// </summary>
public sealed record ArtifactStoreUsage(string Name, string Path, long TotalBytes, long UsedBytes, long FreeBytes);
