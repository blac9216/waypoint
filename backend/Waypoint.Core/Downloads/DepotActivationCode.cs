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

using System.Text;
using System.Text.Json;

namespace Waypoint.Core.Downloads;

/// <summary>
/// Decodes the structure of a VCF 9.1 Software Depot Activation Code (issue #691):
/// base64 text whose decoded UTF-8 body is a JSON object carrying (at minimum) an
/// <c>asset_id</c> field -- the sibling <c>../vcf-docker-download/Dockerfile</c>
/// decodes this same shape to seed <c>machine_id</c>. Waypoint uses the decoded
/// <c>asset_id</c> only for pairing validation (issue #691 AC: "requiring its embedded
/// asset_id to match the managed Depot ID before encrypted storage") -- the decoded
/// value is treated as UNTRUSTED input until that comparison passes, and the raw code
/// text itself is never logged, returned, or included in any exception message this
/// type produces.
/// </summary>
public static class DepotActivationCodeCodec
{
	/// <summary>
	/// Attempts to decode <paramref name="code"/> and extract its <c>asset_id</c>.
	/// Returns null (never throws) on any malformed input -- not valid base64, not
	/// valid UTF-8 JSON after decoding, or missing/empty <c>asset_id</c> -- so callers
	/// get one uniform "structurally invalid" outcome without needing to catch a
	/// specific exception type, and without the raw code ever surfacing in a stack
	/// trace or log line.
	/// </summary>
	public static string? TryExtractAssetId(string code)
	{
		if (string.IsNullOrWhiteSpace(code))
		{
			return null;
		}

		byte[] decoded;
		try
		{
			decoded = Convert.FromBase64String(code.Trim());
		}
		catch (FormatException)
		{
			return null;
		}

		try
		{
			string json = Encoding.UTF8.GetString(decoded);
			using JsonDocument document = JsonDocument.Parse(json);
			if (document.RootElement.ValueKind != JsonValueKind.Object)
			{
				return null;
			}

			if (!document.RootElement.TryGetProperty("asset_id", out JsonElement assetIdElement)
				|| assetIdElement.ValueKind != JsonValueKind.String)
			{
				return null;
			}

			string? assetId = assetIdElement.GetString();
			return string.IsNullOrWhiteSpace(assetId) ? null : assetId;
		}
		catch (JsonException)
		{
			return null;
		}
		catch (DecoderFallbackException)
		{
			return null;
		}
	}
}
