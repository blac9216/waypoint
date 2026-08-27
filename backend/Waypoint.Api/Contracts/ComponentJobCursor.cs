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

using System.Globalization;
using Waypoint.Core.Jobs;

namespace Waypoint.Api.Contracts;

/// <summary>
/// Issue #757's opaque wire cursor for <c>GET /api/v1/runs/{id}/component-jobs</c>.
/// Wraps the composite <c>(priority, created_at, id)</c> keyset the list's
/// <c>ORDER BY priority, created_at, id</c> requires -- mirrors
/// <see cref="RunHistoryCursor"/>'s style (versioned prefix, base64, never throws on
/// decode) but with one extra leg, since this list's primary sort key is
/// <c>priority</c>, not <c>created_at</c> alone. The wire value is
/// <c>"v1:&lt;priority&gt;:&lt;unix-ms&gt;:&lt;uuid&gt;"</c> before base64.
/// </summary>
public static class ComponentJobCursor
{
	private const string Prefix = "v1:";

	public static string Encode(ComponentJobCursorPosition position)
	{
		string text = $"{Prefix}{position.Priority.ToString(CultureInfo.InvariantCulture)}:{position.CreatedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}:{position.Id:D}";
		return Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(text));
	}

	/// <summary>
	/// Decodes a cursor previously produced by <see cref="Encode"/>. Returns
	/// <c>false</c> -- never throws -- for anything malformed, mirroring
	/// <see cref="JobEventCursor.TryDecode"/>/<see cref="RunHistoryCursor.TryDecode"/>'s
	/// "never a 500 on client-abusable input" contract.
	/// </summary>
	public static bool TryDecode(string cursor, out ComponentJobCursorPosition? position)
	{
		position = null;
		if (string.IsNullOrWhiteSpace(cursor))
		{
			return false;
		}

		byte[] bytes;
		try
		{
			bytes = Convert.FromBase64String(cursor);
		}
		catch (FormatException)
		{
			return false;
		}

		string text = System.Text.Encoding.ASCII.GetString(bytes);
		if (!text.StartsWith(Prefix, StringComparison.Ordinal))
		{
			return false;
		}

		string[] parts = text[Prefix.Length..].Split(':', 3);
		if (parts.Length != 3)
		{
			return false;
		}

		if (!short.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out short priority))
		{
			return false;
		}

		if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long unixMs))
		{
			return false;
		}

		if (!Guid.TryParse(parts[2], out Guid id))
		{
			return false;
		}

		position = new ComponentJobCursorPosition(priority, DateTimeOffset.FromUnixTimeMilliseconds(unixMs), id);
		return true;
	}
}
