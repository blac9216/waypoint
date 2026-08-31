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

using Npgsql;
using Waypoint.Core.Downloads;

namespace Waypoint.Infrastructure.Downloads;

/// <inheritdoc cref="IEsxPatchStoreReconciler"/>
/// <remarks>
/// Runs as the download-runner process (only a runner has filesystem access to a
/// mounted patch store, per ADR-0013/0014 -- see migration 0091's grant-header
/// comment). Combines the diff logic with its own Npgsql persistence, following
/// this repo's precedent for a single-consumer table (e.g.
/// <c>UnknownCatalogFileRepository</c>) rather than splitting out a repository
/// interface no other type would implement.
///
/// Diff strategy per run, against #1446's <see cref="IEsxPatchStoreMetadataParser"/>
/// output:
/// <list type="bullet">
/// <item><b>Index</b>: every <see cref="EsxPatchStoreMetadataBundle"/> the parse
/// found is upserted into <c>esx_patch_store_index</c>, keyed on its content key --
/// this table is the cumulative "should exist" model, and a bundle absent from one
/// run is never row-deleted from it (that absence is exactly what makes "missing"
/// detection possible).</item>
/// <item><b>Missing</b>: a content key this store has indexed before that the
/// current run's bundles do not include is opened (or kept open) as a
/// <see cref="EsxPatchStoreDiscrepancyType.Missing"/> discrepancy; a content key that
/// WAS missing and reappears resolves it.</item>
/// <item><b>Orphan</b>: a <c>*.zip</c> file physically present under a vendor
/// directory that no bundle from the current run references (the parser only opens
/// zips its vendor's consolidated metadata index names, so an unreferenced zip is
/// never even attempted -- see <c>EsxPatchStoreMetadataParser.ParseVendorMetadataIndex</c>)
/// is opened as an <see cref="EsxPatchStoreDiscrepancyType.Orphan"/> discrepancy; one
/// that becomes referenced on a later run (metadata catches up via transfer, per the
/// issue's Risks note) resolves it.</item>
/// </list>
/// Neither branch ever deletes a row or a disk file -- resolving only stamps
/// <c>resolved_at</c> on the alert; removing orphaned disk content is this issue's
/// surfacing/rebuild sibling's (#1452) explicit action, never automatic here.
/// </remarks>
public sealed class EsxPatchStoreReconciler : IEsxPatchStoreReconciler
{
	private readonly string _connectionString;
	private readonly IEsxPatchStoreMetadataParser _parser;
	private readonly TimeProvider _clock;

	public EsxPatchStoreReconciler(string connectionString, IEsxPatchStoreMetadataParser parser, TimeProvider? clock = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		ArgumentNullException.ThrowIfNull(parser);

		_connectionString = connectionString;
		_parser = parser;
		_clock = clock ?? TimeProvider.System;
	}

	public async Task<EsxPatchStoreReconciliationReport> ReconcileAsync(string storeRoot, EsxPatchStoreLayout? layout, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);

		EsxPatchStoreParseResult result = _parser.Parse(storeRoot, layout);
		if (!result.Succeeded)
		{
			return EsxPatchStoreReconciliationReport.Failed(result.FailureReason!);
		}

		EsxPatchStoreMetadata metadata = result.Metadata!;
		DateTimeOffset now = _clock.GetUtcNow();

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		int indexedCount = 0;
		HashSet<string> seenContentKeys = new(StringComparer.Ordinal);
		foreach (EsxPatchStoreMetadataBundle bundle in metadata.Bundles)
		{
			seenContentKeys.Add(bundle.ContentKey);
			await UpsertIndexRowAsync(connection, storeRoot, metadata.Layout, bundle, cancellationToken).ConfigureAwait(false);
			indexedCount++;
		}

		int newMissing = 0;
		int resolved = 0;
		foreach (string previouslyIndexedKey in await ListIndexedContentKeysAsync(connection, storeRoot, cancellationToken).ConfigureAwait(false))
		{
			if (seenContentKeys.Contains(previouslyIndexedKey))
			{
				if (await ResolveDiscrepancyAsync(connection, storeRoot, EsxPatchStoreDiscrepancyType.Missing, previouslyIndexedKey, cancellationToken).ConfigureAwait(false))
				{
					resolved++;
				}

				continue;
			}

			if (await RecordDiscrepancyAsync(
				connection, storeRoot, EsxPatchStoreDiscrepancyType.Missing, previouslyIndexedKey,
				vendorCode: null, relativePath: null,
				detail: $"content key '{previouslyIndexedKey}' was previously indexed at this store but was not found by the most recent parse.",
				cancellationToken).ConfigureAwait(false))
			{
				newMissing++;
			}
		}

		int newOrphan = 0;
		foreach (string vendorCode in metadata.VendorCodes)
		{
			string vendorDir = Path.Combine(metadata.HostupdateRoot, vendorCode);
			if (!Directory.Exists(vendorDir))
			{
				continue;
			}

			HashSet<string> referencedZipNames = new(
				metadata.Bundles.Where(b => string.Equals(b.VendorCode, vendorCode, StringComparison.Ordinal)).Select(b => b.ZipRelativePath),
				StringComparer.Ordinal);

			string[] zipFiles;
			try
			{
				zipFiles = Directory.GetFiles(vendorDir, "*.zip");
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Same tolerant-on-unreadable posture as the parser this reconciler
				// consumes -- an unreadable vendor directory does not abort the rest
				// of the reconciliation, it just contributes no orphan findings for
				// this vendor this run.
				continue;
			}

			foreach (string zipFile in zipFiles)
			{
				string fileName = Path.GetFileName(zipFile);
				string key = $"{vendorCode}/{fileName}";

				if (referencedZipNames.Contains(fileName))
				{
					if (await ResolveDiscrepancyAsync(connection, storeRoot, EsxPatchStoreDiscrepancyType.Orphan, key, cancellationToken).ConfigureAwait(false))
					{
						resolved++;
					}

					continue;
				}

				if (await RecordDiscrepancyAsync(
					connection, storeRoot, EsxPatchStoreDiscrepancyType.Orphan, key,
					vendorCode, fileName,
					detail: $"'{fileName}' is present under vendor '{vendorCode}' but is not referenced by that vendor's consolidated metadata index.",
					cancellationToken).ConfigureAwait(false))
				{
					newOrphan++;
				}
			}
		}

		return new EsxPatchStoreReconciliationReport(true, null, indexedCount, newMissing, newOrphan, resolved, metadata.Warnings);
	}

	private static async Task UpsertIndexRowAsync(
		NpgsqlConnection connection, string storeRoot, EsxPatchStoreLayout layout, EsxPatchStoreMetadataBundle bundle, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO esx_patch_store_index
				(store_root, layout, content_key, vendor_code, zip_relative_path, product_id, version, channel_name)
			VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
			ON CONFLICT (store_root, content_key) DO UPDATE SET
				vendor_code = EXCLUDED.vendor_code,
				zip_relative_path = EXCLUDED.zip_relative_path,
				product_id = EXCLUDED.product_id,
				version = EXCLUDED.version,
				channel_name = EXCLUDED.channel_name,
				last_indexed_at = now()
			""", connection);
		command.Parameters.AddWithValue(storeRoot);
		command.Parameters.AddWithValue(layout.ToString());
		command.Parameters.AddWithValue(bundle.ContentKey);
		command.Parameters.AddWithValue(bundle.VendorCode);
		command.Parameters.AddWithValue(bundle.ZipRelativePath);
		command.Parameters.AddWithValue((object?)bundle.ProductId ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)bundle.Version ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)bundle.ChannelName ?? DBNull.Value);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task<List<string>> ListIndexedContentKeysAsync(NpgsqlConnection connection, string storeRoot, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand command = new("SELECT content_key FROM esx_patch_store_index WHERE store_root = $1", connection);
		command.Parameters.AddWithValue(storeRoot);

		List<string> keys = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			keys.Add(reader.GetString(0));
		}

		return keys;
	}

	/// <summary>
	/// Opens (or reopens, if previously resolved) a discrepancy row. Returns true only
	/// when the row was freshly inserted (Postgres's standard <c>xmax = 0</c>
	/// discriminator) -- a reopen of an already-tracked row still refreshes
	/// <c>last_detected_at</c> and clears <c>resolved_at</c>, but does not count toward
	/// <see cref="EsxPatchStoreReconciliationReport"/>'s New* counters, which describe
	/// newly discovered conditions.
	/// </summary>
	private static async Task<bool> RecordDiscrepancyAsync(
		NpgsqlConnection connection, string storeRoot, EsxPatchStoreDiscrepancyType type, string key,
		string? vendorCode, string? relativePath, string detail, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO esx_patch_store_discrepancies (store_root, discrepancy_type, key, vendor_code, relative_path, detail)
			VALUES ($1, $2, $3, $4, $5, $6)
			ON CONFLICT (store_root, discrepancy_type, key) DO UPDATE SET
				last_detected_at = now(),
				resolved_at = NULL,
				vendor_code = EXCLUDED.vendor_code,
				relative_path = EXCLUDED.relative_path,
				detail = EXCLUDED.detail
			RETURNING (xmax = 0) AS inserted
			""", connection);
		command.Parameters.AddWithValue(storeRoot);
		command.Parameters.AddWithValue(DiscrepancyTypeKey(type));
		command.Parameters.AddWithValue(key);
		command.Parameters.AddWithValue((object?)vendorCode ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)relativePath ?? DBNull.Value);
		command.Parameters.AddWithValue(detail);

		object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return scalar is true;
	}

	/// <summary>
	/// Closes an open discrepancy for <paramref name="key"/>, if one exists. Returns
	/// true only when a row transitioned from open to resolved by this call -- an
	/// already-resolved or never-existing row is a no-op, so callers can invoke this
	/// unconditionally for every content key/zip name the current run still finds.
	/// </summary>
	private static async Task<bool> ResolveDiscrepancyAsync(
		NpgsqlConnection connection, string storeRoot, EsxPatchStoreDiscrepancyType type, string key, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand command = new(
			"""
			UPDATE esx_patch_store_discrepancies
			SET resolved_at = now()
			WHERE store_root = $1 AND discrepancy_type = $2 AND key = $3 AND resolved_at IS NULL
			""", connection);
		command.Parameters.AddWithValue(storeRoot);
		command.Parameters.AddWithValue(DiscrepancyTypeKey(type));
		command.Parameters.AddWithValue(key);

		int rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		return rows > 0;
	}

	private static string DiscrepancyTypeKey(EsxPatchStoreDiscrepancyType type) => type switch
	{
		EsxPatchStoreDiscrepancyType.Missing => "missing",
		EsxPatchStoreDiscrepancyType.Orphan => "orphan",
		_ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
	};
}
