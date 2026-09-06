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

namespace Waypoint.Core.Downloads;

/// <summary>The exact string values of <c>vmtools_artifact_index.platform</c>, matching <c>vmtools_artifact_index_platform_check</c> (migration 0109).</summary>
public static class VmToolsPlatforms
{
	public const string Windows = "windows";
	public const string Linux = "linux";
	public const string Arm = "arm";

	/// <summary>Neither an arch-tagged Windows/Linux path nor an OSP subtree could be determined from the artifact's path (e.g. a top-level key/doc file swept up by the crawl).</summary>
	public const string Unknown = "unknown";

	public static readonly IReadOnlyCollection<string> All = [Windows, Linux, Arm, Unknown];
}

/// <summary>The exact string values of <c>vmtools_artifact_index.file_type</c>, matching <c>vmtools_artifact_index_file_type_check</c> (migration 0109).</summary>
public static class VmToolsFileTypes
{
	public const string Iso = "iso";
	public const string Exe = "exe";
	public const string Sha = "sha";
	public const string Sig = "sig";
	public const string Rpm = "rpm";
	public const string Deb = "deb";
	public const string Other = "other";

	public static readonly IReadOnlyCollection<string> All = [Iso, Exe, Sha, Sig, Rpm, Deb, Other];
}

/// <summary>
/// One row of <c>vmtools_artifact_index</c> (migration 0109, issue #1392): a real
/// artifact found by a recursive crawl of the public VMware Tools mirror. Version
/// identity comes from the FILENAME (<see cref="ToolsVersionRaw"/>/<see cref="ToolsBuild"/>),
/// never the containing directory -- research #1030 finding 1: <c>latest/</c> and
/// <c>esx/&lt;major&gt;latest/</c> are real directories of mixed vintages, which
/// <see cref="IsLatestAlias"/> marks without ever implying "current".
/// </summary>
public sealed record VmToolsArtifact(
	Guid Id,
	string RelativePath,
	string? ToolsVersionRaw,
	int? ToolsVersionMajor,
	int? ToolsVersionMinor,
	int? ToolsVersionPatch,
	string? ToolsBuild,
	string Platform,
	string FileType,
	long? SizeBytes,
	string? ETag,
	string? SelfHashSha256,
	bool? SignatureAvailable,
	DateTimeOffset FirstSeenAt,
	DateTimeOffset LastSeenAt,
	bool IsLatestAlias)
{
	/// <summary>
	/// Builds the row a fresh crawl observation maps to, before any repository upsert
	/// decides whether it is new or a re-seen artifact (first/last seen timestamps are
	/// therefore both "now" here -- the repository is what advances <see cref="LastSeenAt"/>
	/// on a genuine re-seen row and leaves <see cref="FirstSeenAt"/> untouched).
	/// </summary>
	public static VmToolsArtifact FromCrawl(
		string relativePath,
		string? toolsVersionRaw,
		int? toolsVersionMajor,
		int? toolsVersionMinor,
		int? toolsVersionPatch,
		string? toolsBuild,
		string platform,
		string fileType,
		long? sizeBytes,
		string? eTag,
		bool isLatestAlias,
		DateTimeOffset observedAt)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(platform);
		ArgumentException.ThrowIfNullOrWhiteSpace(fileType);
		return new VmToolsArtifact(
			Guid.NewGuid(), relativePath, toolsVersionRaw, toolsVersionMajor, toolsVersionMinor, toolsVersionPatch,
			toolsBuild, platform, fileType, sizeBytes, eTag, SelfHashSha256: null, SignatureAvailable: null,
			observedAt, observedAt, isLatestAlias);
	}
}

/// <summary>
/// One row of <c>vmtools_esx_version_mapping</c> (migration 0109, issue #1392): a
/// parsed data line of the upstream <c>versions</c> file, correlating an
/// <c>esx/&lt;version&gt;</c> directory name to a Tools version/build. A malformed source
/// line never becomes a row -- see <c>VmToolsVersionsFileParseResult.Warnings</c>.
/// </summary>
public sealed record VmToolsEsxVersionMapping(
	Guid Id,
	int SequenceInFile,
	string EsxiVersionDir,
	string? EsxiBuild,
	string ToolsVersionCode,
	string? ToolsVersionRaw,
	int? ToolsVersionMajor,
	int? ToolsVersionMinor,
	int? ToolsVersionPatch,
	string? ToolsBuild,
	string RawRow);
