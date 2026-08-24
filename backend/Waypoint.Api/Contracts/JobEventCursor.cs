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
/// Issue #581's opaque wire cursor for <c>GET /runs/{id}/events/history</c>. The
/// underlying keyset is a single value -- <c>job_events.seq</c> -- because <c>seq</c>
/// is already a total, gap-tolerant, commit-order key over the table (migration
/// 0001/0104: assigned inside the ordering-lock at commit time, unique, monotonic
/// with respect to visibility). A composite <c>(timestamp, id)</c> cursor exists to
/// manufacture a total order when the natural key alone has ties or is not
/// commit-ordered; <c>seq</c> has neither problem, so wrapping it in a second column
/// would add encoding surface without adding any ordering guarantee.
///
/// The value is base64-encoded rather than emitted as a bare integer so a client
/// cannot construct or edit one by hand and so a future cursor revision (e.g. adding a
/// version byte) is not a breaking wire-format change for existing bookmarks -- it is
/// explicitly NOT a security boundary (job_events access is already gated by the
/// endpoint's own Viewer+ authorization and run-scoping, not by cursor secrecy).
/// </summary>
public static class JobEventCursor
{
	private const string Prefix = "v1:";

	public static string Encode(long seq)
	{
		byte[] bytes = System.Text.Encoding.ASCII.GetBytes(Prefix + seq.ToString(CultureInfo.InvariantCulture));
		return Convert.ToBase64String(bytes);
	}

	/// <summary>
	/// Decodes a cursor previously produced by <see cref="Encode"/>. Returns
	/// <c>false</c> -- never throws -- for anything malformed (wrong base64, wrong
	/// prefix, non-numeric, negative): the caller (<c>RunsController.MapHistoryQuery</c>)
	/// turns a decode failure into a 400 <c>validation_error</c>, never a 500.
	/// </summary>
	public static bool TryDecode(string cursor, out long seq)
	{
		seq = 0;
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

		// ASCII.GetString never throws (it replaces undecodable bytes with '?'), so a
		// non-ASCII cursor safely falls through to the prefix/numeric checks below
		// instead of raising.
		string text = System.Text.Encoding.ASCII.GetString(bytes);

		if (!text.StartsWith(Prefix, StringComparison.Ordinal))
		{
			return false;
		}

		string numeric = text[Prefix.Length..];
		if (!long.TryParse(numeric, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) || parsed < 0)
		{
			return false;
		}

		seq = parsed;
		return true;
	}
}
