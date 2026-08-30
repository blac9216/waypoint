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
using Waypoint.Core.Downloads;

namespace Waypoint.Infrastructure.Downloads;

/// <summary>
/// Reads the <c>lcm.esx.supported.host.platforms</c> vocabulary as a top-level JSON
/// array key on <see cref="EsxAcquisitionOptions.VocabularyDocumentPath"/> -- the
/// same already-authenticated vendor catalog document
/// <c>VendorProductVersionCatalogParser</c> flattens for depot artifact indexing.
/// Reads the file fresh on every call (never cached), which is what lets a test
/// mutate the on-disk document and observe the very next call reflect it (issue
/// #1470 AC: "no hardcoding").
/// </summary>
public sealed class CatalogFileEsxPlatformVocabularyReader : IEsxPlatformVocabularyReader
{
	private const string VocabularyKey = "lcm.esx.supported.host.platforms";

	private readonly IOptions<EsxAcquisitionOptions> _options;

	public CatalogFileEsxPlatformVocabularyReader(IOptions<EsxAcquisitionOptions> options)
	{
		ArgumentNullException.ThrowIfNull(options);
		_options = options;
	}

	public async Task<IReadOnlyList<string>> GetSupportedPlatformsAsync(CancellationToken cancellationToken)
	{
		string path = _options.Value.VocabularyDocumentPath;
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			return [];
		}

		string json;
		try
		{
			json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
		}
		catch (IOException)
		{
			return [];
		}

		try
		{
			using JsonDocument document = JsonDocument.Parse(json);
			if (!document.RootElement.TryGetProperty(VocabularyKey, out JsonElement platforms)
				|| platforms.ValueKind != JsonValueKind.Array)
			{
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
		catch (JsonException)
		{
			// Malformed catalog document: degrade to "no selectable platforms yet"
			// rather than failing the request -- the vocabulary is advisory input to
			// subscription CRUD, not a gate on the API being usable at all.
			return [];
		}
	}
}
