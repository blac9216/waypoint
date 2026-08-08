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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Waypoint.Core.Downloads;
using Waypoint.Core.SystemState;

namespace Waypoint.Infrastructure.SystemState;

/// <inheritdoc cref="IArtifactStoreDiskUsageProvider"/>
/// <remarks>
/// Computes usage via <see cref="DriveInfo"/> against the mount point that contains
/// <c>DownloadOptions.ArtifactStorePath</c> (issue #10/#228) -- .NET's cross-platform
/// equivalent of <c>statvfs</c>/<c>GetDiskFreeSpaceEx</c>, so this works unmodified on
/// the Linux container image and on a developer's host. Figures describe the whole
/// filesystem the store lives on, not a Waypoint-owned quota within it (see
/// <see cref="ArtifactStoreUsage"/>'s doc comment).
/// </remarks>
public sealed partial class ArtifactStoreDiskUsageProvider : IArtifactStoreDiskUsageProvider
{
	private const string StoreName = "Artifact store";

	private readonly IOptions<DownloadOptions> _downloadOptions;
	private readonly ILogger<ArtifactStoreDiskUsageProvider> _logger;

	public ArtifactStoreDiskUsageProvider(IOptions<DownloadOptions> downloadOptions, ILogger<ArtifactStoreDiskUsageProvider> logger)
	{
		ArgumentNullException.ThrowIfNull(downloadOptions);
		ArgumentNullException.ThrowIfNull(logger);
		_downloadOptions = downloadOptions;
		_logger = logger;
	}

	/// <summary>
	/// A store whose directory does not exist yet (first boot, before any download has
	/// run) or whose filesystem cannot be statted is omitted rather than thrown --
	/// <c>GET /system</c> is chrome-relevant status, not a hard dependency on the
	/// store having been created; a fresh appliance should still answer with an empty
	/// (not 500) stores list. The failure is logged so it is visible to an operator.
	/// </summary>
	public IReadOnlyList<ArtifactStoreUsage> GetUsage()
	{
		string storePath = _downloadOptions.Value.ArtifactStorePath;

		try
		{
			Directory.CreateDirectory(storePath);
			DriveInfo drive = new(storePath);

			long totalBytes = drive.TotalSize;
			long freeBytes = drive.AvailableFreeSpace;
			long usedBytes = Math.Max(0, totalBytes - freeBytes);

			return [new ArtifactStoreUsage(StoreName, storePath, totalBytes, usedBytes, freeBytes)];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			LogDiskUsageUnavailable(exception, storePath);
			return [];
		}
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "Could not read disk usage for artifact store at '{StorePath}'.")]
	private partial void LogDiskUsageUnavailable(Exception exception, string storePath);
}
