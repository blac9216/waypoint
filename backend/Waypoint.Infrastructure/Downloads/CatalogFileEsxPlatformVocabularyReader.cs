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
using Microsoft.Extensions.Options;
using Waypoint.Core.Downloads;

namespace Waypoint.Infrastructure.Downloads;

/// <summary>
/// Reads the <c>lcm.esx.supported.host.platforms</c> vocabulary as a top-level JSON
/// array key on <see cref="EsxAcquisitionOptions.VocabularyDocumentPath"/> -- the
/// same already-authenticated vendor catalog document
/// <c>VendorProductVersionCatalogParser</c> flattens for depot artifact indexing.
/// Reads the file fresh on every call (never cached), which is what lets a test
/// mutate the on-disk document and observe the very next call reflect it (issue
/// #1470 AC: "no hardcoding"). Never throws on an unavailable/unreadable/malformed
/// document -- every degrade path returns an empty list and logs a WARNING naming the
/// reason (issue #1602), so an operator-visible symptom (an empty platforms list, or
/// every write carrying a platform key rejected as "not in the current vendor
/// vocabulary") has a server-side explanation.
/// </summary>
public sealed partial class CatalogFileEsxPlatformVocabularyReader : IEsxPlatformVocabularyReader
{
	private const string VocabularyKey = "lcm.esx.supported.host.platforms";

	private readonly IOptions<EsxAcquisitionOptions> _options;
	private readonly ILogger<CatalogFileEsxPlatformVocabularyReader> _logger;

	public CatalogFileEsxPlatformVocabularyReader(
		IOptions<EsxAcquisitionOptions> options,
		ILogger<CatalogFileEsxPlatformVocabularyReader> logger)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);
		_options = options;
		_logger = logger;
	}

	public async Task<IReadOnlyList<string>> GetSupportedPlatformsAsync(CancellationToken cancellationToken)
	{
		string path = _options.Value.VocabularyDocumentPath;
		if (string.IsNullOrWhiteSpace(path))
		{
			LogPathUnset();
			return [];
		}

		if (!File.Exists(path))
		{
			LogDocumentAbsent(path);
			return [];
		}

		string json;
		try
		{
			json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// UnauthorizedAccessException is a SystemException, not an IOException --
			// File.ReadAllTextAsync throws it for a permission-denied file that
			// File.Exists still reports as present. Caught alongside IOException so the
			// documented never-throw-on-unavailable contract holds for that path too.
			LogReadFailed(path, exception);
			return [];
		}

		try
		{
			using JsonDocument document = JsonDocument.Parse(json);
			if (!document.RootElement.TryGetProperty(VocabularyKey, out JsonElement platforms)
				|| platforms.ValueKind != JsonValueKind.Array)
			{
				LogVocabularyKeyAbsent(path);
				return [];
			}

			List<string> values = [];
			foreach (JsonElement entry in platforms.EnumerateArray())
			{
				if (entry.ValueKind == JsonValueKind.String)
				{
					string? value = entry.GetString();
					if (!string.IsNullOrWhiteSpace(value))
					{
						values.Add(value);
					}
				}
			}

			return values;
		}
		catch (JsonException exception)
		{
			// Malformed catalog document: degrade to "no selectable platforms yet"
			// rather than failing the request -- the vocabulary is advisory input to
			// subscription CRUD, not a gate on the API being usable at all.
			LogMalformedJson(path, exception);
			return [];
		}
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "ESX platform vocabulary degraded to empty: EsxAcquisition:VocabularyDocumentPath is unset.")]
	private partial void LogPathUnset();

	[LoggerMessage(Level = LogLevel.Warning, Message = "ESX platform vocabulary degraded to empty: document not found at '{Path}'.")]
	private partial void LogDocumentAbsent(string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "ESX platform vocabulary degraded to empty: failed to read '{Path}'.")]
	private partial void LogReadFailed(string path, Exception exception);

	[LoggerMessage(Level = LogLevel.Warning, Message = "ESX platform vocabulary degraded to empty: '{Path}' is not valid JSON.")]
	private partial void LogMalformedJson(string path, Exception exception);

	[LoggerMessage(Level = LogLevel.Warning, Message = "ESX platform vocabulary degraded to empty: '{Path}' has no '" + VocabularyKey + "' array.")]
	private partial void LogVocabularyKeyAbsent(string path);
}
