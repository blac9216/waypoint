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

namespace Waypoint.Core.StigManager;

/// <summary>
/// The network boundary for issue #311's direct-API CKL upload and benchmark metadata
/// enrichment, isolated the same way <see cref="IStigManagerProbe"/> is (#310): no fake
/// STIG Manager instance exists in this repo, and CLAUDE.md forbids a real lab hostname
/// in a committed fixture, so integration tests exercise the convert stage against a
/// stub of this interface. Verifying against a real instance is a manual owner step.
/// </summary>
public interface IStigManagerUploadClient
{
	/// <summary>
	/// Uploads one CKL file's bytes to the resolved STIG Manager instance's collection.
	/// Mirrors the sibling <c>vmware-stig-docker</c> watcher's cargo upload semantics: a
	/// prior asset+review for the same benchmark/hostname pair reports as
	/// <see cref="StigManagerUploadOutcome.Conflict"/> (HTTP 409), never thrown --
	/// callers persist the outcome and never fail the scan job over it.
	/// </summary>
	Task<StigManagerUploadResult> UploadCklAsync(
		ResolvedStigManagerConnection connection, string? clientSecret, string cklPath, CancellationToken cancellationToken);

	/// <summary>
	/// Enriches static catalog benchmark metadata with the installed title, release
	/// info, and version from STIG Manager's own <c>/stigs</c> list (mirrors
	/// <c>Resolve-BenchmarkMetadata</c>'s <c>Get-StigManagerStigs</c> step). Returns
	/// <paramref name="fallback"/> unchanged on any failure (unreachable, auth failure,
	/// benchmark not installed) -- this never throws, matching the predecessor's
	/// "resolution never throws" contract.
	/// </summary>
	Task<StigManagerBenchmarkMetadata> ResolveBenchmarkMetadataAsync(
		ResolvedStigManagerConnection connection, string? clientSecret, string benchmarkId, StigManagerBenchmarkMetadata fallback, CancellationToken cancellationToken);
}

/// <summary>Outcome of one <see cref="IStigManagerUploadClient.UploadCklAsync"/> attempt.</summary>
public enum StigManagerUploadOutcome
{
	Uploaded,

	/// <summary>HTTP 409 -- an asset/review for this benchmark+hostname already exists in the collection.</summary>
	Conflict,

	Failed,
}

/// <summary><see cref="Detail"/> is a short, redacted reason suitable for <c>jobs.upload_detail</c> -- never a raw exception message or response body.</summary>
public sealed record StigManagerUploadResult(StigManagerUploadOutcome Outcome, string? Detail);

/// <summary>Benchmark identity + release metadata stamped onto a CKL's <c>STIG_INFO</c> header. Mirrors <c>ScanBenchmarkMetadata</c> (Waypoint.Core.Scans) field-for-field so the static catalog map and the enriched result share one shape.</summary>
public sealed record StigManagerBenchmarkMetadata(string? BenchmarkId, string? Title, string? ReleaseInfo, string? Version);
