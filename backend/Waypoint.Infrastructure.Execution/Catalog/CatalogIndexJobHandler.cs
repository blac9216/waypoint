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
/// </summary>
public sealed class CatalogIndexJobHandler : IJobHandler
{
	private const string InvocationCommand = "Invoke-WaypointCatalogIndex";

	private readonly IPowerShellExecutor _executor;
	private readonly IDepotArtifactRepository _artifacts;
	private readonly ISecretRedactor _redactor;
	private readonly IOptions<CatalogOptions> _catalogOptions;
	private readonly IOptions<PowerShellOptions> _powerShellOptions;

	public CatalogIndexJobHandler(
		IPowerShellExecutor executor,
		IDepotArtifactRepository artifacts,
		ISecretRedactor redactor,
		IOptions<CatalogOptions> catalogOptions,
		IOptions<PowerShellOptions> powerShellOptions)
	{
		ArgumentNullException.ThrowIfNull(executor);
		ArgumentNullException.ThrowIfNull(artifacts);
		ArgumentNullException.ThrowIfNull(redactor);
		ArgumentNullException.ThrowIfNull(catalogOptions);
		ArgumentNullException.ThrowIfNull(powerShellOptions);

		_executor = executor;
		_artifacts = artifacts;
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
	/// Best-effort conversion of the unwrapped <c>SizeBytes</c> PowerShell property
	/// value (may arrive as <see cref="long"/>, <see cref="int"/>, or a numeric
	/// string) into the new <see cref="DepotArtifactUpsert.SizeBytes"/> column
	/// (migration 0100). Returns null rather than throwing on anything else -- one
	/// unparsable size must not fail the whole row, matching this handler's existing
	/// "skip, don't halt" posture for malformed entries.
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
}
