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

namespace Waypoint.Api.Contracts;

/// <summary>
/// Issue #708/#689's opaque wire cursor for <c>GET /runs/history</c> -- mirrors
/// <see cref="JobEventCursor"/>'s style (versioned prefix, base64, never throws on
/// decode) but wraps a COMPOSITE key rather than a single column: unlike
/// <c>job_events.seq</c>, <c>runs.created_at</c> is not unique (two runs can be
/// created in the same transaction-visible instant under concurrent load), so
/// <see cref="JobQueueRepository.ListRunsAsync"/>'s existing <c>ORDER BY r.created_at
/// DESC, r.id DESC</c> tie-break is exactly the ordering this cursor must reproduce --
/// wrapping <c>created_at</c> alone would silently skip or duplicate rows across a
/// page boundary that lands mid-tie. The wire value is
/// <c>"v1:&lt;unix-ms&gt;:&lt;uuid&gt;"</c> before base64.
/// </summary>
public static class RunHistoryCursor
{
	private const string Prefix = "v1:";

	public static string Encode(DateTimeOffset createdAt, Guid id)
	{
		string text = $"{Prefix}{createdAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}:{id:D}";
		return Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(text));
	}

	/// <summary>
	/// Decodes a cursor previously produced by <see cref="Encode"/>. Returns
	/// <c>false</c> -- never throws -- for anything malformed, mirroring
	/// <see cref="JobEventCursor.TryDecode"/>'s "never a 500 on client-abusable input"
	/// contract.
	/// </summary>
	public static bool TryDecode(string cursor, out DateTimeOffset createdAt, out Guid id)
	{
		createdAt = default;
		id = default;

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

		string[] parts = text[Prefix.Length..].Split(':', 2);
		if (parts.Length != 2)
		{
			return false;
		}

		if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long unixMs))
		{
			return false;
		}

		if (!Guid.TryParse(parts[1], out Guid parsedId))
		{
			return false;
		}

		createdAt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
		id = parsedId;
		return true;
	}
}
