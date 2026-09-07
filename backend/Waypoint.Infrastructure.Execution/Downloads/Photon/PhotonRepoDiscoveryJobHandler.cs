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
/// The <c>photon-repo-discovery</c> <see cref="JobShape.Simple"/> job handler (issue
/// #1509, epic #1184), <c>waypoint_download_runner</c>-claimed (migration 0130).
/// Enumerates every Photon version branch <see cref="IPhotonRepoMetadataSource.GetVersionBranchesAsync"/>
/// reports, x every <see cref="PhotonRepoVariants.All"/> value, x both
/// <see cref="PhotonArches.All"/> (this issue's AC 2: both arches unconditionally,
/// never filtered by a subscription preset), probes each repo's
/// <c>repodata/repomd.xml</c>, and upserts one <c>photon_repo_index</c> row per repo
/// via <see cref="IPhotonIndexRepository.UpsertRepoIndexEntryAsync"/>. A repo with no
/// repodata but a directory that exists upstream
/// (<see cref="PhotonRepomdProbeKind.NoRepodata"/>, the <c>photon_snapshots</c>
/// classification) is indexed with <c>HasRepodata = false</c> -- never an error (AC 3).
/// A repo directory that does not exist upstream at all
/// (<see cref="PhotonRepomdProbeKind.Absent"/>) produces no row at all and only a
/// debug-level note -- indexing it as <c>HasRepodata = false</c> would make a claim the
/// probe cannot support (a cartesian-product guess, not an observed repo). A repo whose
/// probe fails outright (<see cref="PhotonRepomdProbeKind.Error"/>) is skipped and its
/// error collected -- one bad repo never fails the whole job, matching this repo's
/// tolerant-parse convention elsewhere (e.g. <c>EsxPatchStoreMetadataParser</c>) -- but
/// when the sweep indexes NOTHING, the job itself fails rather than reporting a
/// misleading "Indexed 0" success, whatever mix of absent/errored/unreachable probes
/// produced that outcome (round-2 review finding 1: an upstream repo-directory rename
/// classifies every probe <c>Absent</c> while <c>photon_versions.json</c> keeps
/// serving, which errors on nothing yet indexes nothing -- a permanently stale index
/// behind a green run). At least one indexed repo is partial success: the job succeeds
/// and the probe errors ride along as a warning in the outcome message.
/// Payload: <c>{"base_url": "https://packages.broadcom.com/photon"}</c> -- required, no
/// hardcoded production default, so a test can point this handler at a fixture host
/// (<c>https://photon.example.internal/photon</c> in this repo's own tests) with zero
/// special-casing.
/// </summary>
public sealed partial class PhotonRepoDiscoveryJobHandler : IJobHandler
{
	private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
	};

	private readonly IPhotonRepoMetadataSource _source;
	private readonly IPhotonIndexRepository _repository;
	private readonly ILogger<PhotonRepoDiscoveryJobHandler> _logger;

	public PhotonRepoDiscoveryJobHandler(
		IPhotonRepoMetadataSource source, IPhotonIndexRepository repository, ILogger<PhotonRepoDiscoveryJobHandler> logger)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(repository);
		ArgumentNullException.ThrowIfNull(logger);

		_source = source;
		_repository = repository;
		_logger = logger;
	}

	public string JobType => "photon-repo-discovery";

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		PhotonRepoDiscoveryPayload? payload;
		try
		{
			payload = JsonSerializer.Deserialize<PhotonRepoDiscoveryPayload>(context.Job.Payload, PayloadOptions);
		}
		catch (JsonException exception)
		{
			return JobExecutionOutcome.Failed($"Malformed photon-repo-discovery payload: {exception.Message}");
		}

		if (payload is null || string.IsNullOrWhiteSpace(payload.BaseUrl))
		{
			return JobExecutionOutcome.Failed("photon-repo-discovery payload requires a non-empty 'base_url'.");
		}

		IReadOnlyList<string>? versions = await _source.GetVersionBranchesAsync(payload.BaseUrl, cancellationToken).ConfigureAwait(false);
		if (versions is null || versions.Count == 0)
		{
			return JobExecutionOutcome.Failed(
				$"Could not enumerate Photon version branches from '{payload.BaseUrl}' (photon_versions.json unreachable or malformed).");
		}

		int indexed = 0;
		int noRepodata = 0;
		int absent = 0;
		List<string> errors = [];
		int totalProbes = versions.Count * PhotonRepoVariants.All.Count * PhotonArches.All.Count;

		foreach (string version in versions)
		{
			foreach (string variant in PhotonRepoVariants.All)
			{
				foreach (string arch in PhotonArches.All)
				{
					string repoBaseUrl = $"{payload.BaseUrl.TrimEnd('/')}/{version}/{RepoDirectoryName(version, variant, arch)}";

					PhotonRepomdProbeResult probe = await _source
						.TryGetRepomdRevisionAndPackageCountAsync(repoBaseUrl, cancellationToken).ConfigureAwait(false);

					switch (probe.Kind)
					{
						case PhotonRepomdProbeKind.Error:
							errors.Add($"{version}/{variant}/{arch}: {probe.Error}");
							continue;
						case PhotonRepomdProbeKind.Absent:
							absent++;
							LogRepoAbsent(_logger, version, variant, arch, repoBaseUrl);
							continue;
						case PhotonRepomdProbeKind.NoRepodata:
							noRepodata++;
							break;
						case PhotonRepomdProbeKind.Found:
							break;
					}

					await _repository.UpsertRepoIndexEntryAsync(
						new PhotonRepoIndexEntry(
							version, variant, arch, repoBaseUrl,
							HasRepodata: probe.Kind == PhotonRepomdProbeKind.Found,
							RepomdRevision: probe.Revision,
							PackageCount: probe.PackageCount),
						cancellationToken).ConfigureAwait(false);
					indexed++;
				}
			}
		}

		if (indexed == 0)
		{
			LogNothingIndexed(_logger, totalProbes, absent, errors.Count);
			string diagnosis = $"photon-repo-discovery: indexed 0 of {totalProbes} repo probe(s) across " +
				$"{versions.Count} version(s) ({absent} absent upstream, {errors.Count} probe error(s), " +
				$"{noRepodata} without repodata); nothing indexed.";
			return JobExecutionOutcome.Failed(errors.Count > 0
				? $"{diagnosis} Failed probes: {string.Join("; ", errors)}"
				: diagnosis);
		}

		string summary = $"Indexed {indexed} Photon repo(s) across {versions.Count} version(s) " +
			$"({noRepodata} without repodata, {absent} absent upstream).";
		if (errors.Count > 0)
		{
			LogRepoErrors(_logger, errors.Count);
			return JobExecutionOutcome.Succeeded($"{summary} {errors.Count} repo(s) skipped after a probe error: {string.Join("; ", errors)}");
		}

		return JobExecutionOutcome.Succeeded(summary);
	}

	/// <summary>
	/// <c>photon_&lt;version&gt;_&lt;arch&gt;</c> for the bare composite repo,
	/// <c>photon_&lt;variant&gt;_&lt;version&gt;_&lt;arch&gt;</c> for every other variant
	/// (research #1029 finding 1).
	/// </summary>
	internal static string RepoDirectoryName(string version, string variant, string arch) =>
		variant == PhotonRepoVariants.Composite
			? $"photon_{version}_{arch}"
			: $"photon_{variant}_{version}_{arch}";

	[LoggerMessage(Level = LogLevel.Warning, Message = "photon-repo-discovery: {Count} repo probe(s) failed and were skipped")]
	private static partial void LogRepoErrors(ILogger logger, int count);

	[LoggerMessage(Level = LogLevel.Error, Message = "photon-repo-discovery: indexed nothing -- {TotalProbes} probe(s), {Absent} absent upstream, {Errors} probe error(s)")]
	private static partial void LogNothingIndexed(ILogger logger, int totalProbes, int absent, int errors);

	[LoggerMessage(Level = LogLevel.Debug, Message = "photon-repo-discovery: {Version}/{Variant}/{Arch} directory absent upstream ({RepoBaseUrl}), no row indexed")]
	private static partial void LogRepoAbsent(ILogger logger, string version, string variant, string arch, string repoBaseUrl);

	private sealed record PhotonRepoDiscoveryPayload(string? BaseUrl);
}
