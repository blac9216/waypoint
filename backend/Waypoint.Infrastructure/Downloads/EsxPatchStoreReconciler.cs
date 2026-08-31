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
///
/// Round-1 review hardening, structurally re-keyed in round 2: #1446's parser is
/// deliberately tolerant -- an unreadable <c>hostupdate/</c> root or vendor
/// consolidated metadata index still returns <c>Succeeded=true</c> with an
/// empty/partial <c>Bundles</c> list plus a warning, not a failure. Treating that
/// output as a complete reading of the store would convert a transient read failure
/// into persistent <c>missing</c> discrepancy rows for every content key the
/// affected scope ever indexed (the exact transient-to-persistent conversion #1656
/// rules out).
///
/// Round 1 gated this on pattern-matching the parser's warning <i>prose</i>, which
/// round 2 found incomplete in the way that strategy predicts: two of the parser's
/// warning shapes (a zero-length or malformed/half-written vendor consolidated
/// metadata index) produced the identical "vendor left with zero bundles" outcome
/// but were not recognized, so a concurrently-written index still flooded that
/// vendor's previously-indexed content as false <c>missing</c> rows (round-2 review
/// finding F4), and the whole-run root-degraded branch had no test pinning its
/// prose-only trigger at all (finding F5). Both are now keyed on machine-readable
/// fields #1446's parser sets at the exact sites that also emit those warnings --
/// <see cref="EsxPatchStoreMetadata.RootReadable"/> for the whole-store case and
/// <see cref="EsxPatchStoreMetadata.VendorHealth"/> (a closed
/// <see cref="EsxPatchStoreVendorHealthKind"/> enum) for the per-vendor case. No
/// prose parsing remains in the gate; <c>Warnings</c> stays for humans only. A
/// contract test in <see cref="EsxPatchStoreReconcilerTests"/> pins the enum's member
/// count against this class's exhaustive-by-throw <see cref="DescribeHealthKind"/>
/// switch, so a future parser edit that adds a new degraded-vendor shape without
/// updating both goes red (round-2 review finding F5's "pin the contract" ask). Every
/// skip still surfaces on
/// <see cref="EsxPatchStoreReconciliationReport.ReconcilerWarnings"/> rather than
/// staying silent -- a degraded run is retried in full on the next pass, so this
/// never permanently suppresses real missing-detection, only this run's.
///
/// Orphan detection separately confines each vendor code from the store's XML to a
/// single path segment resolving strictly under <c>HostupdateRoot</c> (the
/// <c>ContentLibraryRepository.ResolveDiskPath</c> pattern, #1391) before it is ever
/// combined into a filesystem path.
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

		List<string> reconcilerWarnings = [];
		(bool rootDegraded, HashSet<string> degradedVendorCodes) = ClassifyParseHealth(metadata);
		if (rootDegraded)
		{
			reconcilerWarnings.Add(
				"Missing-discrepancy detection skipped entirely this run: the parse could not list vendor directories under this store's hostupdate root, so its empty/partial bundle list cannot be trusted as ground truth for what is actually missing. See ParserWarnings for the read failure; a healthy parse will detect real missing content on the next run.");
		}

		int newMissing = 0;
		int resolved = 0;
		int skippedMissing = 0;
		HashSet<string> vendorsWithSkippedMissing = new(StringComparer.Ordinal);
		foreach ((string previouslyIndexedKey, string previouslyIndexedVendorCode) in
			await ListIndexedContentKeysAsync(connection, storeRoot, cancellationToken).ConfigureAwait(false))
		{
			if (seenContentKeys.Contains(previouslyIndexedKey))
			{
				if (await ResolveDiscrepancyAsync(connection, storeRoot, EsxPatchStoreDiscrepancyType.Missing, previouslyIndexedKey, cancellationToken).ConfigureAwait(false))
				{
					resolved++;
				}

				continue;
			}

			if (rootDegraded || degradedVendorCodes.Contains(previouslyIndexedVendorCode))
			{
				skippedMissing++;
				vendorsWithSkippedMissing.Add(previouslyIndexedVendorCode);
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

		if (!rootDegraded && skippedMissing > 0)
		{
			string vendorDetail = string.Join(", ", vendorsWithSkippedMissing
				.Order(StringComparer.Ordinal)
				.Select(code => $"{code} ({DescribeVendorDegradation(metadata.VendorHealth, code)})"));
			reconcilerWarnings.Add(
				$"Missing-discrepancy detection skipped for {skippedMissing} previously indexed key(s) under vendor(s) {vendorDetail}: that vendor's parse this run left it degraded, so its absence from the current bundle list cannot be trusted as a real removal. See ParserWarnings for the read failure; a healthy parse of that vendor will detect real missing content on a later run.");
		}

		int newOrphan = 0;
		foreach (string vendorCode in metadata.VendorCodes)
		{
			string? vendorDir = TryResolveVendorDir(metadata.HostupdateRoot, vendorCode, reconcilerWarnings);
			if (vendorDir is null || !Directory.Exists(vendorDir))
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
				// Diverges from the parser's own tolerant-on-unreadable posture
				// (SafeEnumerateDirectories) only in silence, not in behaviour: an
				// unreadable vendor directory still does not abort the rest of the
				// reconciliation, but it must warn -- a bare `continue` here would be
				// indistinguishable in the database from a vendor directory that was
				// read successfully and simply has no orphans (round-1 review finding 3).
				reconcilerWarnings.Add($"Vendor '{vendorCode}': could not list zip files under '{vendorDir}': {ex.GetType().Name}: {ex.Message}");
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

		return new EsxPatchStoreReconciliationReport(true, null, indexedCount, newMissing, newOrphan, resolved, metadata.Warnings, reconcilerWarnings);
	}

	/// <summary>
	/// Classifies this run's parse health from #1446's structural fields (round-2
	/// review findings F4/F5) -- never from <see cref="EsxPatchStoreMetadata.Warnings"/>
	/// prose. Returns whether the hostupdate root itself could not be enumerated
	/// (<see cref="EsxPatchStoreMetadata.RootReadable"/> is <see langword="false"/>;
	/// every vendor's previously-indexed content is then unattributable and the whole
	/// run's missing-detection is skipped) and, otherwise, which individual vendor
	/// codes <see cref="EsxPatchStoreMetadata.VendorHealth"/> named as degraded this
	/// run.
	/// </summary>
	private static (bool RootDegraded, HashSet<string> DegradedVendorCodes) ClassifyParseHealth(EsxPatchStoreMetadata metadata)
	{
		HashSet<string> degradedVendorCodes = new(
			metadata.VendorHealth.Select(health => health.VendorCode),
			StringComparer.Ordinal);

		return (!metadata.RootReadable, degradedVendorCodes);
	}

	/// <summary>
	/// Human-readable summary of every distinct <see cref="EsxPatchStoreVendorHealthKind"/>
	/// <paramref name="vendorCode"/> triggered this run, for
	/// <see cref="EsxPatchStoreReconciliationReport.ReconcilerWarnings"/>. The gate
	/// itself (<see cref="ClassifyParseHealth"/>) never inspects this text -- it is
	/// display only; <see cref="DescribeHealthKind"/>'s <c>default</c> arm throwing
	/// (this repo's <c>DiscrepancyTypeKey</c> precedent, below) turns an unhandled
	/// <see cref="EsxPatchStoreVendorHealthKind"/> member into a loud runtime failure
	/// the first time it is actually produced, rather than a silently-blank
	/// description; <see cref="EsxPatchStoreReconcilerTests"/>'s enum-count contract
	/// test is what actually pins the member count at build/test time (round-2 review
	/// finding F5).
	/// </summary>
	private static string DescribeVendorDegradation(IReadOnlyList<EsxPatchStoreVendorHealth> vendorHealth, string vendorCode) =>
		string.Join("; ", vendorHealth
			.Where(health => string.Equals(health.VendorCode, vendorCode, StringComparison.Ordinal))
			.Select(health => health.Kind)
			.Distinct()
			.Select(DescribeHealthKind));

	private static string DescribeHealthKind(EsxPatchStoreVendorHealthKind kind) => kind switch
	{
		EsxPatchStoreVendorHealthKind.UnreadableIndex => "could not read its consolidated metadata index",
		EsxPatchStoreVendorHealthKind.EmptyIndex => "its consolidated metadata index is empty",
		EsxPatchStoreVendorHealthKind.MalformedIndex => "its consolidated metadata index is not valid/safe XML (or exceeds the parse size bound)",
		EsxPatchStoreVendorHealthKind.UnreadableZip => "could not read a metadata zip it names",
		_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
	};

	/// <summary>
	/// Confines a vendor code taken verbatim from the store's own XML (the
	/// consolidated index's <c>&lt;vendor&gt;</c> text, which the parser accepts on a
	/// non-empty check alone) to a real, single-segment child of
	/// <paramref name="hostupdateRoot"/> before it is ever combined into a filesystem
	/// path -- the <c>ContentLibraryRepository.ResolveDiskPath</c> pattern (#1391).
	/// A code containing a separator or <c>..</c> is rejected by the segment check;
	/// an absolute code is caught by both the segment check and the resolved-prefix
	/// check (belt-and-suspenders, matching the #1391 precedent). Round-1 review
	/// finding 2: without this, such a code escapes the store root (or, if rooted,
	/// discards it entirely) and the reconciler lists and records whatever
	/// <c>*.zip</c> files it finds there as this store's orphans.
	/// </summary>
	private static string? TryResolveVendorDir(string hostupdateRoot, string vendorCode, List<string> warnings)
	{
		if (vendorCode is "." or ".." || Path.GetFileName(vendorCode) != vendorCode || Path.IsPathRooted(vendorCode))
		{
			warnings.Add($"Vendor code '{vendorCode}' from this store's consolidated index is not a valid single path segment -- skipped rather than resolved against the store's hostupdate root.");
			return null;
		}

		string rootFullPath = Path.GetFullPath(hostupdateRoot);
		string vendorDir = Path.GetFullPath(Path.Combine(rootFullPath, vendorCode));
		string rootWithSeparator = rootFullPath.EndsWith(Path.DirectorySeparatorChar)
			? rootFullPath
			: rootFullPath + Path.DirectorySeparatorChar;
		if (!vendorDir.StartsWith(rootWithSeparator, StringComparison.Ordinal))
		{
			warnings.Add($"Vendor code '{vendorCode}' from this store's consolidated index resolved outside this store's hostupdate root -- skipped rather than followed.");
			return null;
		}

		return vendorDir;
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

	/// <summary>
	/// Every content key this store has previously indexed, paired with the vendor
	/// code it was indexed under -- the vendor pairing is what lets the missing-diff
	/// scope its degraded-parse gate (<see cref="ClassifyParseWarnings"/>) to just the
	/// vendor(s) a read failure actually affected, rather than the whole store.
	/// </summary>
	private static async Task<List<(string ContentKey, string VendorCode)>> ListIndexedContentKeysAsync(NpgsqlConnection connection, string storeRoot, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand command = new("SELECT content_key, vendor_code FROM esx_patch_store_index WHERE store_root = $1", connection);
		command.Parameters.AddWithValue(storeRoot);

		List<(string, string)> keys = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			keys.Add((reader.GetString(0), reader.GetString(1)));
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
