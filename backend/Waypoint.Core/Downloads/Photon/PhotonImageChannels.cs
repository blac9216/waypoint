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
/// The Photon image-tree release-channel axis, matching migration 0130's
/// <c>photon_image_index_channel_check</c> verbatim -- this is the closed set.
/// <see cref="Waypoint.Tests.Core.Downloads.Photon.PhotonImageIndexConstantsConstraintDriftTests"/>
/// asserts this list stays byte-identical to the CHECK constraint the migration
/// produces.
/// </summary>
public static class PhotonImageChannels
{
	public const string Ga = "GA";
	public const string Rc = "RC";
	public const string Beta = "Beta";

	public static readonly IReadOnlyList<string> All = [Ga, Rc, Beta];
}

/// <summary>
/// The product-facing image-kind axis this issue's Proposed Changes name, matching
/// migration 0130's <c>photon_image_index_kind_check</c> verbatim -- this is the
/// closed set. <c>cloud-image</c> consolidates the AMI/Azure/GCE cloud-image variants
/// (research #1029 finding 1's "non-repo siblings") to one product-facing kind, since
/// this lane indexes them identically (metadata only, no per-cloud distinction stored).
/// </summary>
public static class PhotonImageKinds
{
	public const string Iso = "iso";
	public const string Ova = "ova";
	public const string Ovf = "ovf";
	public const string CloudImage = "cloud-image";
	public const string Rpi = "rpi";

	public static readonly IReadOnlyList<string> All = [Iso, Ova, Ovf, CloudImage, Rpi];

	/// <summary>
	/// The closed extension-to-kind map <c>HttpPhotonImageListingSource</c> classifies
	/// a discovered filename against -- an unrecognized extension (a directory index
	/// page, a checksum sidecar file, etc.) is not an image file and is skipped rather
	/// than indexed under a guessed kind.
	/// </summary>
	public static string? ClassifyByFileName(string fileName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

		string lower = fileName.ToLowerInvariant();
		if (lower.EndsWith(".iso", StringComparison.Ordinal))
		{
			return Iso;
		}

		if (lower.EndsWith(".ova", StringComparison.Ordinal))
		{
			return Ova;
		}

		if (lower.EndsWith(".ovf", StringComparison.Ordinal))
		{
			return Ovf;
		}

		if (lower.EndsWith(".ami", StringComparison.Ordinal)
			|| lower.EndsWith(".vhd", StringComparison.Ordinal)
			|| lower.EndsWith(".vhdx", StringComparison.Ordinal)
			|| lower.EndsWith(".qcow2", StringComparison.Ordinal)
			|| lower.EndsWith(".raw", StringComparison.Ordinal))
		{
			return CloudImage;
		}

		if (lower.Contains("rpi", StringComparison.Ordinal)
			&& (lower.EndsWith(".img.xz", StringComparison.Ordinal) || lower.EndsWith(".img", StringComparison.Ordinal)))
		{
			return Rpi;
		}

		return null;
	}
}
