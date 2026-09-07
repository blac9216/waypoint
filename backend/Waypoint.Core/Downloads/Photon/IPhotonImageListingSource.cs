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
/// The HTTP boundary <c>PhotonImageDiscoveryJobHandler</c> (in
/// <c>Waypoint.Infrastructure.Execution</c>) is abstracted behind, mirroring
/// <see cref="IPhotonRepoMetadataSource"/>'s own testability convention. Lists the
/// image files published directly under one version/channel directory (an HTML
/// autoindex per research #1029's three-tier discovery pattern) -- there is
/// deliberately no method on this interface capable of fetching an actual image file's
/// body (this issue's AC 5/AC 1: "no code path from discovery to file fetch").
/// <see cref="PhotonImageListingEntry.SizeBytes"/> is populated ONLY from a real
/// <c>HEAD</c> <c>Content-Length</c>, never from any size text an HTML listing page
/// renders inline (issue #1170's guard) -- an implementation MUST NOT parse a
/// listing's human-readable size column into this field.
/// </summary>
public interface IPhotonImageListingSource
{
	/// <summary>
	/// Returns every image file found directly under <c>channelBaseUrl</c>, or
	/// <c>null</c> if that directory listing is unreachable or unparseable -- the
	/// caller then treats the channel as not-yet-published rather than indexing zero
	/// entries as a confirmed empty channel (mirrors <see cref="PhotonRepomdProbeResult.Absent"/>'s
	/// "no row" posture for a combination that does not exist upstream). A
	/// successfully-parsed listing with zero recognized image files is returned as an
	/// empty list, distinct from <c>null</c>.
	/// </summary>
	Task<IReadOnlyList<PhotonImageListingEntry>?> ListImagesAsync(string channelBaseUrl, CancellationToken cancellationToken);
}

/// <summary>
/// One image file entry read back from a channel directory listing.
/// <see cref="RelativePath"/> is relative to the channel directory (no leading slash,
/// no <c>..</c> segment -- validated the same way <c>HttpPhotonRepoMetadataSource</c>
/// validates a repomd <c>&lt;location href&gt;</c>, since an autoindex page is
/// unsigned, untrusted input from the same upstream).
/// </summary>
public sealed record PhotonImageListingEntry(string RelativePath, long? SizeBytes, string? ETag);
