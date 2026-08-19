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

namespace Waypoint.Core.Scheduling;

/// <summary>
/// A minimal standard 5-field cron expression (minute hour day-of-month month
/// day-of-week), parsed and evaluated in-process rather than via a third-party NuGet
/// package -- this codebase has no cron dependency today (see the csproj files), and a
/// hand-rolled parser keeps the air-gapped build surface (CLAUDE.md: "no external
/// package fetches at runtime") to exactly what ships in the repo. Supports the four
/// standard field grammars: <c>*</c> (every value), a bare number, a comma-separated
/// list (<c>1,3,5</c>), a range (<c>1-5</c>), and a step (<c>*/15</c> or
/// <c>1-30/5</c>) -- no named months/weekdays (<c>JAN</c>, <c>MON</c>), no <c>L</c>/<c>W</c>/<c>#</c>
/// extensions; those are out of scope for a control-plane scan/discover/catalog-index
/// schedule, which only ever needs numeric cadences.
/// </summary>
public sealed class CronExpression
{
	private readonly IReadOnlySet<int> _minutes;
	private readonly IReadOnlySet<int> _hours;
	private readonly IReadOnlySet<int> _daysOfMonth;
	private readonly IReadOnlySet<int> _months;
	private readonly IReadOnlySet<int> _daysOfWeek;

	private CronExpression(
		IReadOnlySet<int> minutes, IReadOnlySet<int> hours, IReadOnlySet<int> daysOfMonth,
		IReadOnlySet<int> months, IReadOnlySet<int> daysOfWeek)
	{
		_minutes = minutes;
		_hours = hours;
		_daysOfMonth = daysOfMonth;
		_months = months;
		_daysOfWeek = daysOfWeek;
	}

	/// <summary>
	/// Parses a 5-field cron expression. Throws <see cref="FormatException"/> with a
	/// caller-facing message on anything that isn't exactly 5 whitespace-separated
	/// fields or that fails a field's own grammar/range check -- the API layer
	/// (<c>SchedulesController</c>) surfaces this as a 400 validation error, never a
	/// 500, since a bad cron string is a client input error.
	/// </summary>
	public static CronExpression Parse(string expression)
	{
		if (string.IsNullOrWhiteSpace(expression))
		{
			throw new FormatException("cron_expression is required.");
		}

		string[] fields = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (fields.Length != 5)
		{
			throw new FormatException(
				$"cron_expression must have exactly 5 fields (minute hour day-of-month month day-of-week); got {fields.Length}.");
		}

		return new CronExpression(
			ParseField(fields[0], 0, 59, "minute"),
			ParseField(fields[1], 0, 23, "hour"),
			ParseField(fields[2], 1, 31, "day-of-month"),
			ParseField(fields[3], 1, 12, "month"),
			ParseField(fields[4], 0, 6, "day-of-week"));
	}

	/// <summary>
	/// The next fire time strictly after <paramref name="after"/>, truncated to the
	/// minute (cron has no sub-minute resolution). Searches forward up to 4 years --
	/// long enough to cross any real day-of-month/month/day-of-week combination
	/// (including Feb 29 on a leap year) but bounded so a self-contradictory expression
	/// (e.g. day-of-month 31 in a month that never has one, combined with a narrow
	/// day-of-week) fails fast with <see cref="InvalidOperationException"/> rather than
	/// spinning forever.
	/// </summary>
	public DateTimeOffset GetNextOccurrence(DateTimeOffset after)
	{
		DateTimeOffset candidate = after.AddMinutes(1);
		candidate = new DateTimeOffset(
			candidate.Year, candidate.Month, candidate.Day, candidate.Hour, candidate.Minute, 0, candidate.Offset);

		DateTimeOffset limit = after.AddYears(4);
		while (candidate <= limit)
		{
			if (_months.Contains(candidate.Month) && MatchesDay(candidate) && _hours.Contains(candidate.Hour) && _minutes.Contains(candidate.Minute))
			{
				return candidate;
			}

			candidate = candidate.AddMinutes(1);
		}

		throw new InvalidOperationException("cron_expression has no occurrence within the next 4 years.");
	}

	/// <summary>
	/// Standard cron day-matching: when BOTH day-of-month and day-of-week are restricted
	/// (neither is the full "every value" set), a day matches if EITHER field matches
	/// (OR, not AND) -- the POSIX/Vixie cron convention. When only one is restricted,
	/// that one field alone decides.
	/// </summary>
	private bool MatchesDay(DateTimeOffset candidate)
	{
		bool domRestricted = _daysOfMonth.Count < 31;
		bool dowRestricted = _daysOfWeek.Count < 7;

		bool domMatch = _daysOfMonth.Contains(candidate.Day);
		bool dowMatch = _daysOfWeek.Contains((int)candidate.DayOfWeek);

		if (domRestricted && dowRestricted)
		{
			return domMatch || dowMatch;
		}

		return domMatch && dowMatch;
	}

	private static HashSet<int> ParseField(string field, int min, int max, string fieldName)
	{
		HashSet<int> values = [];
		foreach (string part in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
		{
			ParsePart(part, min, max, fieldName, values);
		}

		if (values.Count == 0)
		{
			throw new FormatException($"cron_expression {fieldName} field '{field}' did not resolve to any value.");
		}

		return values;
	}

	private static void ParsePart(string part, int min, int max, string fieldName, HashSet<int> values)
	{
		string rangePart = part;
		int step = 1;

		int slashIndex = part.IndexOf('/', StringComparison.Ordinal);
		if (slashIndex >= 0)
		{
			rangePart = part[..slashIndex];
			string stepText = part[(slashIndex + 1)..];
			if (!int.TryParse(stepText, NumberStyles.None, CultureInfo.InvariantCulture, out step) || step <= 0)
			{
				throw new FormatException($"cron_expression {fieldName} field has an invalid step '{stepText}'.");
			}
		}

		int rangeStart;
		int rangeEnd;
		if (rangePart == "*")
		{
			rangeStart = min;
			rangeEnd = max;
		}
		else
		{
			int dashIndex = rangePart.IndexOf('-', StringComparison.Ordinal);
			if (dashIndex >= 0)
			{
				string startText = rangePart[..dashIndex];
				string endText = rangePart[(dashIndex + 1)..];
				if (!int.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out rangeStart) ||
					!int.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out rangeEnd))
				{
					throw new FormatException($"cron_expression {fieldName} field has an invalid range '{rangePart}'.");
				}
			}
			else
			{
				if (!int.TryParse(rangePart, NumberStyles.None, CultureInfo.InvariantCulture, out rangeStart))
				{
					throw new FormatException($"cron_expression {fieldName} field has an invalid value '{rangePart}'.");
				}

				rangeEnd = rangeStart;
			}
		}

		if (rangeStart < min || rangeEnd > max || rangeStart > rangeEnd)
		{
			throw new FormatException(
				$"cron_expression {fieldName} field '{part}' is out of range ({min}-{max}).");
		}

		for (int value = rangeStart; value <= rangeEnd; value += step)
		{
			values.Add(value);
		}
	}
}
