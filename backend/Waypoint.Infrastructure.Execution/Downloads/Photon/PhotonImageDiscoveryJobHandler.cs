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

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Waypoint.Core.Downloads.Photon;
using Waypoint.Core.Jobs;

namespace Waypoint.Infrastructure.Downloads.Photon;

/// <summary>
/// The <c>photon-image-discovery</c> <see cref="JobShape.Simple"/> job handler (issue
/// #1790, epic #1184, this issue's documented remainder of #1509),
/// <c>waypoint_download_runner</c>-claimed. Enumerates every Photon version branch
/// <see cref="IPhotonRepoMetadataSource.GetVersionBranchesAsync"/> reports (the same
/// small <c>photon_versions.json</c> feed <c>PhotonRepoDiscoveryJobHandler</c> already
/// reads -- reused rather than re-fetched by a second abstraction) x every
/// <see cref="PhotonImageChannels.All"/> value, lists each channel directory via
/// <see cref="IPhotonImageListingSource.ListImagesAsync"/>, and upserts one
/// <c>photon_image_index</c> row per recognized ISO/OVA/OVF/cloud-image/RPi file via
/// <see cref="IPhotonIndexRepository.UpsertImageIndexEntryAsync"/> -- metadata only,
/// never a download of the image itself (AC 1/AC 5).
/// A channel directory whose listing is unreachable/unparseable
/// (<see cref="IPhotonImageListingSource.ListImagesAsync"/> returning <c>null</c>) is
/// treated as "not published for this version" and produces no row and only a
/// debug-level note -- mirrors <c>PhotonRepoDiscoveryJobHandler</c>'s Absent handling:
/// a cartesian-product guess the vendor never published is not an error. When the
/// sweep indexes NOTHING across every version/channel, the job itself fails rather than
/// reporting a misleading "Indexed 0" success (mirrors round-2 review finding 1's fix
/// on the sibling repo-discovery job). At least one indexed file is partial success.
/// Payload: <c>{"base_url": "https://packages.broadcom.com/photon"}</c> -- required, no
/// hardcoded production default, so a test can point this handler at a fixture host.
/// </summary>
public sealed partial class PhotonImageDiscoveryJobHandler : IJobHandler
{
	private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
	};

	private readonly IPhotonRepoMetadataSource _versionSource;
	private readonly IPhotonImageListingSource _imageSource;
	private readonly IPhotonIndexRepository _repository;
	private readonly ILogger<PhotonImageDiscoveryJobHandler> _logger;

	public PhotonImageDiscoveryJobHandler(
		IPhotonRepoMetadataSource versionSource,
		IPhotonImageListingSource imageSource,
		IPhotonIndexRepository repository,
		ILogger<PhotonImageDiscoveryJobHandler> logger)
	{
		ArgumentNullException.ThrowIfNull(versionSource);
		ArgumentNullException.ThrowIfNull(imageSource);
		ArgumentNullException.ThrowIfNull(repository);
		ArgumentNullException.ThrowIfNull(logger);

		_versionSource = versionSource;
		_imageSource = imageSource;
		_repository = repository;
		_logger = logger;
	}

	public string JobType => "photon-image-discovery";

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		PhotonImageDiscoveryPayload? payload;
		try
		{
			payload = JsonSerializer.Deserialize<PhotonImageDiscoveryPayload>(context.Job.Payload, PayloadOptions);
		}
		catch (JsonException exception)
		{
			return JobExecutionOutcome.Failed($"Malformed photon-image-discovery payload: {exception.Message}");
		}

		if (payload is null || string.IsNullOrWhiteSpace(payload.BaseUrl))
		{
			return JobExecutionOutcome.Failed("photon-image-discovery payload requires a non-empty 'base_url'.");
		}

		IReadOnlyList<string>? versions = await _versionSource.GetVersionBranchesAsync(payload.BaseUrl, cancellationToken).ConfigureAwait(false);
		if (versions is null || versions.Count == 0)
		{
			return JobExecutionOutcome.Failed(
				$"Could not enumerate Photon version branches from '{payload.BaseUrl}' (photon_versions.json unreachable or malformed).");
		}

		int indexed = 0;
		int skippedUnrecognized = 0;
		int channelsUnpublished = 0;
		int totalChannels = versions.Count * PhotonImageChannels.All.Count;

		foreach (string version in versions)
		{
			foreach (string channel in PhotonImageChannels.All)
			{
				string channelBaseUrl = $"{payload.BaseUrl.TrimEnd('/')}/{version}/{ChannelDirectoryName(channel)}";

				IReadOnlyList<PhotonImageListingEntry>? entries = await _imageSource
					.ListImagesAsync(channelBaseUrl, cancellationToken).ConfigureAwait(false);

				if (entries is null)
				{
					channelsUnpublished++;
					LogChannelUnpublished(_logger, version, channel, channelBaseUrl);
					continue;
				}

				foreach (PhotonImageListingEntry entry in entries)
				{
					string? kind = PhotonImageKinds.ClassifyByFileName(entry.RelativePath);
					if (kind is null)
					{
						// The listing source already filters to recognized extensions,
						// but re-checked here defensively: never index a file under a
						// guessed kind.
						skippedUnrecognized++;
						continue;
					}

					await _repository.UpsertImageIndexEntryAsync(
						new PhotonImageIndexEntry(version, channel, kind, entry.RelativePath, entry.SizeBytes, entry.ETag),
						cancellationToken).ConfigureAwait(false);
					indexed++;
				}
			}
		}

		if (indexed == 0)
		{
			LogNothingIndexed(_logger, totalChannels, channelsUnpublished);
			return JobExecutionOutcome.Failed(
				$"photon-image-discovery: indexed 0 image(s) across {versions.Count} version(s) x " +
				$"{PhotonImageChannels.All.Count} channel(s) ({channelsUnpublished} channel(s) unpublished/unreachable); nothing indexed.");
		}

		string summary = $"Indexed {indexed} Photon image(s) across {versions.Count} version(s) " +
			$"({channelsUnpublished} channel(s) unpublished/unreachable, {skippedUnrecognized} unrecognized file(s) skipped).";
		return JobExecutionOutcome.Succeeded(summary);
	}

	/// <summary>
	/// Lowercased channel word as the directory-name token (e.g. <c>GA</c> -&gt;
	/// <c>ga</c>) -- the live upstream directory-naming shape is unverified pending a
	/// real mirror (this PR's Verified expectation is <c>pending-live</c>).
	/// </summary>
	internal static string ChannelDirectoryName(string channel) => channel.ToLowerInvariant();

	[LoggerMessage(Level = LogLevel.Error, Message = "photon-image-discovery: indexed nothing -- {TotalChannels} channel(s) probed, {Unpublished} unpublished/unreachable")]
	private static partial void LogNothingIndexed(ILogger logger, int totalChannels, int unpublished);

	[LoggerMessage(Level = LogLevel.Debug, Message = "photon-image-discovery: {Version}/{Channel} channel listing unpublished or unreachable ({ChannelBaseUrl}), no row indexed")]
	private static partial void LogChannelUnpublished(ILogger logger, string version, string channel, string channelBaseUrl);

	private sealed record PhotonImageDiscoveryPayload(string? BaseUrl);
}
