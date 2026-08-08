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

using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.Scans;
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;
using Waypoint.Core.StigManager;
using Waypoint.Infrastructure.StigManager;

namespace Waypoint.Infrastructure.Scans;

/// <summary>
/// Issue #311's post-convert action: uploads a job's CKL to the target site's resolved
/// STIG Manager instance (<see cref="StigManagerRepository.ResolveForSiteAsync"/>,
/// #310) and persists the outcome to <c>jobs.upload_status</c>/<c>upload_detail</c>
/// (migration 0018) -- never as a state-machine transition, per that migration's
/// comment: <c>uploaded</c> already means "artifacts ready" (PR #302), and this issue
/// does not reopen that contract. Shared by <see cref="Waypoint.Infrastructure.Scans.ScanJobHandler"/>'s
/// convert stage (the first attempt) and <c>JobsController</c>'s retry route (issue
/// #297's shape, reused narrowly) so both go through the exact same resolve
/// -&gt; decrypt -&gt; upload -&gt; persist path.
///
/// <b>Never throws</b> and <b>never fails the caller's job</b>: every failure mode --
/// no STIG Manager configured, credential decrypt failure, network failure, 409
/// conflict -- resolves to a <see cref="JobUploadStatuses"/> value persisted on the job
/// row, not an exception propagated to <see cref="ScanJobHandler"/>'s convert stage
/// (issue #311 AC: "upload failure must NEVER fail the scan run").
/// </summary>
public sealed class ScanUploadCoordinator
{
	private readonly StigManagerRepository _stigman;
	private readonly IStigManagerUploadClient _uploadClient;
	private readonly ICredentialSecretStore _secrets;
	private readonly IJobQueueRepository _jobs;
	private readonly ISecretRedactor _redactor;

	public ScanUploadCoordinator(
		StigManagerRepository stigman,
		IStigManagerUploadClient uploadClient,
		ICredentialSecretStore secrets,
		IJobQueueRepository jobs,
		ISecretRedactor redactor)
	{
		ArgumentNullException.ThrowIfNull(stigman);
		ArgumentNullException.ThrowIfNull(uploadClient);
		ArgumentNullException.ThrowIfNull(secrets);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(redactor);
		_stigman = stigman;
		_uploadClient = uploadClient;
		_secrets = secrets;
		_jobs = jobs;
		_redactor = redactor;
	}

	/// <summary>
	/// Uploads <paramref name="cklPath"/> for <paramref name="target"/>'s site, persists
	/// the outcome on <paramref name="jobId"/>, and returns it. A missing STIG Manager
	/// connection (no site override, no global default) is not an error -- it persists
	/// as <see cref="JobUploadStatuses.Failed"/> with an explanatory detail, same as any
	/// other non-throwing degrade, since there is genuinely nothing to upload to.
	/// </summary>
	public async Task<StigManagerUploadResult> UploadAsync(Guid jobId, Target target, string cklPath, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(target);
		ArgumentException.ThrowIfNullOrWhiteSpace(cklPath);

		await _jobs.SetUploadStatusAsync(jobId, JobUploadStatuses.Pending, null, cancellationToken).ConfigureAwait(false);

		StigManagerUploadResult result;
		try
		{
			ResolvedStigManagerConnection? connection = await _stigman.ResolveForSiteAsync(target.SiteId, cancellationToken).ConfigureAwait(false);
			if (connection is null)
			{
				result = new StigManagerUploadResult(StigManagerUploadOutcome.Failed, "No STIG Manager connection is configured for this site or globally.");
			}
			else
			{
				string? clientSecret = await ResolveClientSecretAsync(connection, jobId, cancellationToken).ConfigureAwait(false);
				result = await _uploadClient.UploadCklAsync(connection, clientSecret, cklPath, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			// Belt-and-suspenders: IStigManagerUploadClient's own contract is
			// non-throwing, but a coordinator-level catch keeps the "never fail the
			// scan run" AC true even against a future implementation that forgets it,
			// matching Resolve-BenchmarkMetadata's own never-throw discipline.
			result = new StigManagerUploadResult(StigManagerUploadOutcome.Failed, "STIG Manager upload failed unexpectedly.");
		}

		string status = result.Outcome switch
		{
			StigManagerUploadOutcome.Uploaded => JobUploadStatuses.Uploaded,
			StigManagerUploadOutcome.Conflict => JobUploadStatuses.Conflict,
			_ => JobUploadStatuses.Failed,
		};
		string? detail = result.Detail is null ? null : _redactor.Redact(result.Detail);
		await _jobs.SetUploadStatusAsync(jobId, status, detail, cancellationToken).ConfigureAwait(false);
		return result;
	}

	/// <summary>
	/// Enriches <paramref name="fallback"/> with the installed STIG's title/release/version
	/// from the target's resolved STIG Manager instance -- degrades to
	/// <paramref name="fallback"/> unchanged on any failure, including no connection
	/// configured (mirrors <c>Resolve-BenchmarkMetadata</c>'s "never throws" contract).
	/// </summary>
	public async Task<StigManagerBenchmarkMetadata> ResolveBenchmarkMetadataAsync(
		Guid jobId, Target target, StigManagerBenchmarkMetadata fallback, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(target);
		ArgumentNullException.ThrowIfNull(fallback);

		if (string.IsNullOrWhiteSpace(fallback.BenchmarkId))
		{
			return fallback;
		}

		try
		{
			ResolvedStigManagerConnection? connection = await _stigman.ResolveForSiteAsync(target.SiteId, cancellationToken).ConfigureAwait(false);
			if (connection is null)
			{
				return fallback;
			}

			string? clientSecret = await ResolveClientSecretAsync(connection, jobId, cancellationToken).ConfigureAwait(false);
			return await _uploadClient
				.ResolveBenchmarkMetadataAsync(connection, clientSecret, fallback.BenchmarkId, fallback, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			return fallback;
		}
	}

	private async Task<string?> ResolveClientSecretAsync(ResolvedStigManagerConnection connection, Guid jobId, CancellationToken cancellationToken)
	{
		if (connection.CredentialId is not Guid credentialId)
		{
			return null;
		}

		try
		{
			using DecryptedSecret decrypted = await _secrets
				.DecryptAsync(credentialId, actor: "system", jobId, runId: null, cancellationToken)
				.ConfigureAwait(false);
			return decrypted.Value;
		}
		catch (CredentialSecretNotFoundException)
		{
			return null;
		}
		catch (MasterKeyUnavailableException)
		{
			return null;
		}
	}
}
