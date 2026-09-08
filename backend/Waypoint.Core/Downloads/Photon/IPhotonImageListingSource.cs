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
	/// Lists every image file found directly under <c>channelBaseUrl</c>. The outcome is
	/// a three-way <see cref="PhotonImageListingResult"/> rather than a nullable list,
	/// for the reason issue #1835 records on the sibling repo lane: "we looked and this
	/// channel is not published" and "we could not look" are different facts, and folding
	/// them into one value makes an under-indexed sweep indistinguishable from a
	/// fully-indexed one. An explicit 404 on the listing is
	/// <see cref="PhotonImageListingKind.Absent"/> (a cartesian-product channel the
	/// vendor never published -- normal, no row, not an error); a 403, a 5xx, any other
	/// non-404 failure status, a transport failure, a timeout, or a listing document over
	/// the implementation's byte cap are all
	/// <see cref="PhotonImageListingKind.Indeterminate"/> with an operator-readable
	/// <see cref="PhotonImageListingResult.Error"/> naming which of those happened. A
	/// successfully-parsed listing is <see cref="PhotonImageListingKind.Found"/>, with
	/// zero recognized image files returned as an empty entry list -- itself distinct
	/// from both other outcomes.
	/// </summary>
	Task<PhotonImageListingResult> ListImagesAsync(string channelBaseUrl, CancellationToken cancellationToken);
}

/// <summary>
/// The outcome of listing one version/channel directory. Mirrors
/// <see cref="PhotonRepomdProbeResult"/>'s split on the sibling repo lane: an observed
/// absence, an observed listing, and "the probe could not determine which" are three
/// distinct facts, never one nullable value.
/// </summary>
public sealed record PhotonImageListingResult(
	PhotonImageListingKind Kind, IReadOnlyList<PhotonImageListingEntry> Entries, string? Error)
{
	/// <summary>The channel directory answered an explicit 404 -- it is not published for this version.</summary>
	public static readonly PhotonImageListingResult Absent = new(PhotonImageListingKind.Absent, [], null);

	public static PhotonImageListingResult Found(IReadOnlyList<PhotonImageListingEntry> entries) =>
		new(PhotonImageListingKind.Found, entries, null);

	/// <summary>
	/// The probe never produced an answer this code may act on (403/5xx/other non-404
	/// status, transport failure, timeout, or an over-cap listing document). The caller
	/// must NOT treat this as an unpublished channel: nothing is written, and the sweep
	/// reports itself incomplete.
	/// </summary>
	public static PhotonImageListingResult Indeterminate(string error) =>
		new(PhotonImageListingKind.Indeterminate, [], error);
}

public enum PhotonImageListingKind
{
	Found,
	Absent,
	Indeterminate,
}

/// <summary>
/// One image file entry read back from a channel directory listing.
/// <see cref="RelativePath"/> is relative to the channel directory (no leading slash,
/// no <c>..</c> segment -- validated the same way <c>HttpPhotonRepoMetadataSource</c>
/// validates a repomd <c>&lt;location href&gt;</c>, since an autoindex page is
/// unsigned, untrusted input from the same upstream).
/// </summary>
public sealed record PhotonImageListingEntry(string RelativePath, long? SizeBytes, string? ETag);
