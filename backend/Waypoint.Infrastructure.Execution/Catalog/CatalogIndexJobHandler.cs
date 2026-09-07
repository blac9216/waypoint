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
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.PowerShell;
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
/// handler needs (the depot path, which PowerShell function to invoke) is either
/// configuration (<see cref="CatalogOptions"/>) or resolved at execution time, not
/// carried on the job row.
///
/// Issue #690 AC: local catalog re-index resolves and decrypts NO credential at all.
/// The offline indexing walk (<c>Invoke-WaypointCatalogIndex</c> -&gt;
/// <c>Get-FileManifest</c>, docs/domain-model.md open question 4) is a pure
/// filesystem read of files already present on the offline depot share -- it never
/// authenticated to anything, so there is no purpose-specific credential (Activation
/// Code or legacy Download Token) for this handler to declare or consume. The
/// PowerShell module's <c>-DepotToken</c> parameter is left unbound here (it stays
/// optional on the module signature for forward compatibility with a future
/// vendor-catalog-refresh addition that would consume it -- see the module's own doc
/// comment).
///
/// Issue #1512: rewritten to presence-sweep semantics. #1503's
/// <c>Invoke-WaypointCatalogIndex</c> no longer treats "found on disk" as "create a
/// row" -- it walks the authenticated vendor catalog and verifies each entry's disk
/// presence, emitting one of two record shapes per the module's own doc comment:
/// <c>RecordType = 'ArtifactPresence'</c> (a catalog entry, present or missing --
/// #1495's existing <see cref="IDepotArtifactRepository.UpsertAsync"/> upsert, keyed
/// by the depot-relative catalog identity) or <c>RecordType = 'UnknownFile'</c> (a file
/// on disk matching no catalog entry -- #1495's <see cref="IUnknownCatalogFileRepository.RecordSeenAsync"/>,
/// which itself raises the new-unknown-file alert on first sighting, decision Q11).
/// Nothing is silently dropped: a row this handler cannot classify (missing/unrecognized
/// <c>RecordType</c>, or missing required fields for its shape) is skipped, not fatal --
/// the same "one malformed entry must not block every other artifact" posture this
/// handler has always had.
/// </summary>
public sealed class CatalogIndexJobHandler : IJobHandler
{
	private const string InvocationCommand = "Invoke-WaypointCatalogIndex";
	private const string ArtifactPresenceRecordType = "ArtifactPresence";
	private const string UnknownFileRecordType = "UnknownFile";

	private readonly IPowerShellExecutor _executor;
	private readonly IDepotArtifactRepository _artifacts;
	private readonly IUnknownCatalogFileRepository _unknownFiles;
	private readonly ISecretRedactor _redactor;
	private readonly IOptions<CatalogOptions> _catalogOptions;
	private readonly IOptions<PowerShellOptions> _powerShellOptions;

	public CatalogIndexJobHandler(
		IPowerShellExecutor executor,
		IDepotArtifactRepository artifacts,
		IUnknownCatalogFileRepository unknownFiles,
		ISecretRedactor redactor,
		IOptions<CatalogOptions> catalogOptions,
		IOptions<PowerShellOptions> powerShellOptions)
	{
		ArgumentNullException.ThrowIfNull(executor);
		ArgumentNullException.ThrowIfNull(artifacts);
		ArgumentNullException.ThrowIfNull(unknownFiles);
		ArgumentNullException.ThrowIfNull(redactor);
		ArgumentNullException.ThrowIfNull(catalogOptions);
		ArgumentNullException.ThrowIfNull(powerShellOptions);

		_executor = executor;
		_artifacts = artifacts;
		_unknownFiles = unknownFiles;
		_redactor = redactor;
		_catalogOptions = catalogOptions;
		_powerShellOptions = powerShellOptions;
	}

	public string JobType => "catalog-index";

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		CatalogOptions options = _catalogOptions.Value;

		Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
		{
			["DepotPath"] = options.DepotPath,
		};

		PowerShellRequest request = new(
			InvocationCommand,
			PowerShellRequestKind.Command,
			parameters,
			context.Job.Id,
			context.Job.RunId);

		PowerShellExecutionResult result = await _executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

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

		SweepOutcome outcome = await ProcessSweepOutputAsync(result.Output, context, cancellationToken).ConfigureAwait(false);

		string progressPayload = JsonSerializer.Serialize(new { indexed_count = outcome.Upserted, unknown_count = outcome.UnknownSeen });
		await context.Events
			.EmitAsync(JobEventTypes.RunProgress, null, context.Job.RunId, progressPayload, cancellationToken)
			.ConfigureAwait(false);

		string message = $"Indexed {outcome.Upserted} artifact(s); {outcome.UnknownSeen} unknown file(s) seen.";
		if (outcome.Rejected > 0)
		{
			message += $" Rejected {outcome.Rejected} row(s).";
		}

		return JobExecutionOutcome.Succeeded(message);
	}

	/// <summary>
	/// Walks <c>Invoke-WaypointCatalogIndex</c>'s output (one PSObject per catalog entry
	/// or unknown file -- see this type's own doc comment), branching on
	/// <c>RecordType</c> so each of the two #1503 shapes lands through the repository
	/// method that owns it. <c>RunProgress</c> fires every 25 rows PROCESSED
	/// (upserted or unknown-seen together, not upserted alone) -- the same cadence this
	/// handler has always used, extended to cover both shapes now that both count
	/// toward real work done.
	/// </summary>
	private async Task<SweepOutcome> ProcessSweepOutputAsync(
		IReadOnlyList<object?> output, JobExecutionContext context, CancellationToken cancellationToken)
	{
		int upserted = 0;
		int unknownSeen = 0;
		int rejected = 0;
		int processed = 0;

		foreach (object? item in output)
		{
			if (item is not System.Management.Automation.PSObject psObject)
			{
				processed++;
				continue;
			}

			string? recordType = GetProperty<string>(psObject, "RecordType");

			if (string.Equals(recordType, UnknownFileRecordType, StringComparison.Ordinal))
			{
				string? unknownRelativePath = GetProperty<string>(psObject, "RelativePath");
				if (string.IsNullOrWhiteSpace(unknownRelativePath))
				{
					processed++;
					continue;
				}

				object? unknownSizeBytes = PowerShellValueUnwrap.Unwrap(psObject.Properties["SizeBytes"]?.Value);

				try
				{
					await _unknownFiles.RecordSeenAsync(unknownRelativePath, TryToInt64(unknownSizeBytes), cancellationToken).ConfigureAwait(false);
				}
				catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.CheckViolation)
				{
					// Symmetric with the ArtifactPresence branch below: a row a future
					// constraint revision rejects is counted and skipped, not allowed to
					// abort the rest of the sweep. No CHECK constraint exists on
					// unknown_catalog_files as of migration 0100 (the only concrete abort
					// this could hit today is the NOT NULL already guarded by the
					// blank-path check above), so this branch is unreachable under the
					// current schema -- kept for consistency as the vocabulary grows.
					rejected++;
					processed++;
					string warningPayload = JsonSerializer.Serialize(new
					{
						severity = "warning",
						line = $"catalog-index: rejected unknown file '{unknownRelativePath}' -- constraint {exception.ConstraintName}. Skipped, not aborted.",
					});
					await context.Events
						.EmitAsync(JobEventTypes.JobLog, context.Job.Id, context.Job.RunId, warningPayload, cancellationToken)
						.ConfigureAwait(false);
					continue;
				}

				unknownSeen++;
				processed++;
				await MaybeEmitProgressAsync(context, processed, cancellationToken).ConfigureAwait(false);
				continue;
			}

			if (!string.Equals(recordType, ArtifactPresenceRecordType, StringComparison.Ordinal))
			{
				// Unrecognized or missing RecordType -- malformed entry, skipped rather
				// than failing the whole job (this handler's long-standing "skip, don't
				// halt" posture, unchanged by #1512).
				processed++;
				continue;
			}

			DepotArtifactUpsert? upsert = TryParseArtifact(psObject);
			if (upsert is null)
			{
				processed++;
				continue;
			}

			try
			{
				await _artifacts.UpsertAsync(upsert, cancellationToken).ConfigureAwait(false);
			}
			catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.CheckViolation)
			{
				// Issue #1705 Option B (defense in depth; A -- migration 0129 widening
				// depot_artifacts_status_check -- is the actual fix): a row whose status
				// the constraint rejects (e.g. a status value emitted by a future
				// sweep/handler revision the constraint has not yet been widened for) is
				// counted and skipped rather than allowed to propagate and abort every
				// other artifact in the batch.
				rejected++;
				processed++;
				string warningPayload = JsonSerializer.Serialize(new
				{
					severity = "warning",
					line = $"catalog-index: rejected artifact '{upsert.RelativePath}' with status '{upsert.Status}' -- constraint {exception.ConstraintName}. Skipped, not aborted.",
				});
				await context.Events
					.EmitAsync(JobEventTypes.JobLog, context.Job.Id, context.Job.RunId, warningPayload, cancellationToken)
					.ConfigureAwait(false);
				continue;
			}

			upserted++;
			processed++;
			await MaybeEmitProgressAsync(context, processed, cancellationToken).ConfigureAwait(false);
		}

		return new SweepOutcome(upserted, unknownSeen, rejected);
	}

	private static async Task MaybeEmitProgressAsync(JobExecutionContext context, int processed, CancellationToken cancellationToken)
	{
		if (processed % 25 != 0)
		{
			return;
		}

		string payload = JsonSerializer.Serialize(new { processed_count = processed });
		await context.Events
			.EmitAsync(JobEventTypes.RunProgress, null, context.Job.RunId, payload, cancellationToken)
			.ConfigureAwait(false);
	}

	private static DepotArtifactUpsert? TryParseArtifact(System.Management.Automation.PSObject psObject)
	{
		string? externalId = GetProperty<string>(psObject, "ExternalId");
		string? status = GetProperty<string>(psObject, "Status");
		if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(status))
		{
			return null;
		}

		string? sha256 = GetProperty<string>(psObject, "Sha256");
		string? product = GetProperty<string>(psObject, "Product");
		string? version = GetProperty<string>(psObject, "Version");
		object? sizeBytes = PowerShellValueUnwrap.Unwrap(psObject.Properties["SizeBytes"]?.Value);
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

		// externalId is passed as RelativePath (migration 0100, issue #1488): the
		// module's ExternalId property was already a depot-relative path (see this
		// type's own doc comment), so this is the same value the catalog-identity
		// column now expects, carried through the explicitly named field instead of
		// a bare ExternalId string that used to stand in for two different things.
		return new DepotArtifactUpsert(externalId, sha256, status, metadataJson, TryToInt64(sizeBytes));
	}

	/// <summary>
	/// Best-effort conversion of an unwrapped PowerShell <c>SizeBytes</c> property
	/// value (may arrive as <see cref="long"/>, <see cref="int"/>, or a numeric
	/// string) into a nullable <see cref="long"/>. Returns null rather than throwing on
	/// anything else -- one unparsable size must not fail the whole row, matching this
	/// handler's existing "skip, don't halt" posture for malformed entries.
	/// </summary>
	private static long? TryToInt64(object? value)
	{
		return value switch
		{
			long longValue => longValue,
			int intValue => intValue,
			string stringValue when long.TryParse(stringValue, out long parsed) => parsed,
			_ => null,
		};
	}

	private static T? GetProperty<T>(System.Management.Automation.PSObject psObject, string name)
		where T : class
	{
		return PowerShellValueUnwrap.UnwrapAs<T>(psObject.Properties[name]?.Value);
	}

	private readonly record struct SweepOutcome(int Upserted, int UnknownSeen, int Rejected);
}
