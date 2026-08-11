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
using Microsoft.Extensions.Options;
using Waypoint.Core.Catalog;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.PowerShell;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.PowerShell;

namespace Waypoint.Infrastructure.Catalog;

/// <summary>
/// The first production <see cref="IJobHandler"/> registration (issue #194, epic #9
/// slice 2): <c>catalog-index</c>, a <see cref="Waypoint.Core.Jobs.JobShape.Simple"/>
/// job type (<c>queued -&gt; running -&gt; done</c>, per <c>JobShapes.ForJobType</c> --
/// the #136 Standard-shape blocker does not apply here).
///
/// Unlike <see cref="Waypoint.Infrastructure.PowerShell.PowerShellJobHandler"/>, this
/// handler is not payload-driven -- <c>POST /catalog/sync</c> fans the job out with an
/// empty <c>{}</c> payload (see <c>CatalogController.Sync</c>), so everything this
/// handler needs (the depot path, which credential holds the depot token, which
/// PowerShell function to invoke) is either configuration
/// (<see cref="CatalogOptions"/>) or resolved at execution time, not carried on the
/// job row.
///
/// Depot token flow (security.md controls 1/2, reusing the epic #6/#8 canary
/// machinery): <see cref="ICredentialSecretStore.DecryptAsync"/> returns a
/// <see cref="DecryptedSecret"/> that has already registered the value with the
/// redactor (holding the handle IS being redacted); the value is passed to
/// <c>Invoke-WaypointCatalogIndex</c> as a bound <see cref="PowerShellRequest.Parameters"/>
/// entry -- never interpolated into command/script text, never logged, never placed on
/// argv. The handle is disposed (ending the in-play window) as soon as the PowerShell
/// invocation returns, in a <c>finally</c>, whether it succeeded or not.
/// </summary>
public sealed class CatalogIndexJobHandler : IJobHandler
{
	private const string InvocationCommand = "Invoke-WaypointCatalogIndex";

	private readonly IPowerShellExecutor _executor;
	private readonly ICredentialSecretStore _secrets;
	private readonly Waypoint.Infrastructure.Secrets.CredentialRepository _credentials;
	private readonly IDepotArtifactRepository _artifacts;
	private readonly IJobRunnerRepository _jobs;
	private readonly ISecretRedactor _redactor;
	private readonly IOptions<CatalogOptions> _catalogOptions;
	private readonly IOptions<PowerShellOptions> _powerShellOptions;

	public CatalogIndexJobHandler(
		IPowerShellExecutor executor,
		ICredentialSecretStore secrets,
		Waypoint.Infrastructure.Secrets.CredentialRepository credentials,
		IDepotArtifactRepository artifacts,
		IJobRunnerRepository jobs,
		ISecretRedactor redactor,
		IOptions<CatalogOptions> catalogOptions,
		IOptions<PowerShellOptions> powerShellOptions)
	{
		ArgumentNullException.ThrowIfNull(executor);
		ArgumentNullException.ThrowIfNull(secrets);
		ArgumentNullException.ThrowIfNull(credentials);
		ArgumentNullException.ThrowIfNull(artifacts);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(redactor);
		ArgumentNullException.ThrowIfNull(catalogOptions);
		ArgumentNullException.ThrowIfNull(powerShellOptions);

		_executor = executor;
		_secrets = secrets;
		_credentials = credentials;
		_artifacts = artifacts;
		_jobs = jobs;
		_redactor = redactor;
		_catalogOptions = catalogOptions;
		_powerShellOptions = powerShellOptions;
	}

	public string JobType => "catalog-index";

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		CatalogOptions options = _catalogOptions.Value;
		string actor = await ResolveActorAsync(context.Job.RunId, cancellationToken).ConfigureAwait(false);

		CredentialResponse? depotTokenCredential = await _credentials
			.FindByTypeAsync(options.DepotTokenCredentialType, cancellationToken)
			.ConfigureAwait(false);
		if (depotTokenCredential is null)
		{
			return JobExecutionOutcome.Failed(
				$"No credential of type '{options.DepotTokenCredentialType}' is configured for the depot token.");
		}

		PowerShellExecutionResult result;
		DecryptedSecret? decrypted = null;
		try
		{
			// security.md control 4 / #8's fail-closed decrypt audit: this call writes
			// the secret.decrypted audit row (credential, job, run, actor, timestamp)
			// in the same transaction as the ciphertext read, before any plaintext
			// reaches this method -- attribution is durable even if this handler
			// crashes on the next line.
			decrypted = await _secrets
				.DecryptAsync(depotTokenCredential.Id, actor, context.Job.Id, context.Job.RunId, cancellationToken)
				.ConfigureAwait(false);

			Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
			{
				["DepotPath"] = options.DepotPath,
				["DepotToken"] = decrypted.Value,
			};

			PowerShellRequest request = new(
				InvocationCommand,
				PowerShellRequestKind.Command,
				parameters,
				context.Job.Id,
				context.Job.RunId);

			result = await _executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
		}
		catch (CredentialSecretNotFoundException exception)
		{
			return JobExecutionOutcome.Failed($"Depot token credential has no stored secret: {exception.Message}");
		}
		catch (MasterKeyUnavailableException exception)
		{
			return JobExecutionOutcome.Failed($"Depot token could not be decrypted: {exception.Message}");
		}
		finally
		{
			// Ends the in-play redaction window as soon as the invocation is done --
			// the value must not still be "in play" once this method has returned.
			decrypted?.Dispose();
		}

		if (!result.Succeeded)
		{
			// jobs.note is a sink too (security.md control 1) -- classify on the raw
			// reason (so a redacted token doesn't defeat the auth-failure markers) but
			// return only the scrubbed text.
			string rawNote = result.FailureReason ?? "catalog-index invocation failed with no failure reason.";
			bool isAuthFailure = AuthFailureClassifier.IsAuthFailure(rawNote, [.. _powerShellOptions.Value.AuthFailureMarkers]);
			string note = _redactor.Redact(rawNote);
			return isAuthFailure ? JobExecutionOutcome.AuthFailed(note) : JobExecutionOutcome.Failed(note);
		}

		int upserted = await UpsertArtifactsAsync(result.Output, context, cancellationToken).ConfigureAwait(false);

		string progressPayload = JsonSerializer.Serialize(new { indexed_count = upserted });
		await context.Events
			.EmitAsync(JobEventTypes.RunProgress, null, context.Job.RunId, progressPayload, cancellationToken)
			.ConfigureAwait(false);

		return JobExecutionOutcome.Succeeded($"Indexed {upserted} artifact(s).");
	}

	/// <summary>
	/// Parses <c>Invoke-WaypointCatalogIndex</c>'s output (one PSObject per file, base
	/// properties ExternalId/Sha256/Status/Product/Version/SizeBytes/RelativePath -- see
	/// the module's doc comment) into <see cref="DepotArtifactUpsert"/> rows and upserts
	/// each through the slice-1 repository's <c>ON CONFLICT</c> path (idempotent
	/// re-sync, issue #193's acceptance criterion). Rows the parser cannot make sense of
	/// are skipped rather than failing the whole job -- one malformed entry must not
	/// block every other artifact from indexing (the same "individual target failures
	/// must not halt a run" principle CLAUDE.md states for scans/downloads).
	/// </summary>
	private async Task<int> UpsertArtifactsAsync(IReadOnlyList<object?> output, JobExecutionContext context, CancellationToken cancellationToken)
	{
		int upserted = 0;
		foreach (object? item in output)
		{
			DepotArtifactUpsert? upsert = TryParseArtifact(item);
			if (upsert is null)
			{
				continue;
			}

			await _artifacts.UpsertAsync(upsert, cancellationToken).ConfigureAwait(false);
			upserted++;

			if (upserted % 25 == 0)
			{
				string payload = JsonSerializer.Serialize(new { indexed_count = upserted });
				await context.Events
					.EmitAsync(JobEventTypes.RunProgress, null, context.Job.RunId, payload, cancellationToken)
					.ConfigureAwait(false);
			}
		}

		return upserted;
	}

	private static DepotArtifactUpsert? TryParseArtifact(object? item)
	{
		if (item is not System.Management.Automation.PSObject psObject)
		{
			return null;
		}

		string? externalId = GetProperty<string>(psObject, "ExternalId");
		string? status = GetProperty<string>(psObject, "Status");
		if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(status))
		{
			return null;
		}

		string? sha256 = GetProperty<string>(psObject, "Sha256");
		string? product = GetProperty<string>(psObject, "Product");
		string? version = GetProperty<string>(psObject, "Version");
		object? sizeBytes = psObject.Properties["SizeBytes"]?.Value;
		string? relativePath = GetProperty<string>(psObject, "RelativePath");

		Dictionary<string, object?> metadata = new(StringComparer.Ordinal)
		{
			["relative_path"] = relativePath,
			["size_bytes"] = sizeBytes,
		};
		if (!string.IsNullOrWhiteSpace(product))
		{
			metadata["product"] = product;
		}

		if (!string.IsNullOrWhiteSpace(version))
		{
			metadata["version"] = version;
		}

		string metadataJson = JsonSerializer.Serialize(metadata);
		return new DepotArtifactUpsert(externalId, sha256, status, metadataJson);
	}

	private static T? GetProperty<T>(System.Management.Automation.PSObject psObject, string name)
		where T : class
	{
		return psObject.Properties[name]?.Value as T;
	}

	/// <summary>
	/// Attribution for the decrypt audit row (security.md control 4): the run's
	/// initiator when recorded (issue #208's derive-from-identity pattern), falling
	/// back to a fixed system marker for a scheduled/unattributed run so the audit row
	/// is never written with a null/empty actor.
	/// </summary>
	private async Task<string> ResolveActorAsync(Guid? runId, CancellationToken cancellationToken)
	{
		if (runId is null)
		{
			return "system";
		}

		RunQueueState? state = await _jobs.GetRunQueueStateAsync(runId.Value, cancellationToken).ConfigureAwait(false);
		return string.IsNullOrWhiteSpace(state?.InitiatedBy) ? "system" : state!.InitiatedBy!;
	}
}
