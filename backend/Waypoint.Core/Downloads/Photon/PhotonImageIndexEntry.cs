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

namespace Waypoint.Core.Downloads.Photon;

/// <summary>
/// One discovered Photon installer/appliance image-tree file (migration 0130's
/// <c>photon_image_index</c>): version x <see cref="PhotonImageChannels"/> x relative
/// path, identity-keyed on that triple. <see cref="SizeBytes"/> is populated ONLY from
/// a real byte count -- a <c>HEAD</c> <c>Content-Length</c> or a storage-API field --
/// NEVER from a rounded HTML directory-listing column (issue #1170's guard; the
/// discovery source never parses a listing's human-readable size column at all -- see
/// <c>HttpPhotonImageListingSource</c>'s own remarks). Carries no image bytes -- this
/// is a metadata-only row, never a download (this issue's AC 5/AC 1).
/// </summary>
public sealed record PhotonImageIndexEntry(
	string Version,
	string Channel,
	string ImageKind,
	string RelativePath,
	long? SizeBytes,
	string? ETag)
{
	/// <summary>Null until read back from storage, which assigns the row's identity.</summary>
	public Guid? Id { get; init; }

	public DateTimeOffset? DiscoveredAt { get; init; }

	public DateTimeOffset? LastSeenAt { get; init; }
}
