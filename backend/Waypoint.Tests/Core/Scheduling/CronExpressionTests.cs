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

using Waypoint.Core.Scheduling;

namespace Waypoint.Tests.Core.Scheduling;

public sealed class CronExpressionTests
{
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("* * * *")]
	[InlineData("* * * * * *")]
	[InlineData("60 * * * *")]
	[InlineData("* 24 * * *")]
	[InlineData("* * 0 * *")]
	[InlineData("* * * 13 *")]
	[InlineData("* * * * 7")]
	[InlineData("a * * * *")]
	[InlineData("5-3 * * * *")]
	public void Parse_RejectsInvalidExpressions(string expression)
	{
		Assert.Throws<FormatException>(() => CronExpression.Parse(expression));
	}

	[Fact]
	public void GetNextOccurrence_EveryMinute_IsOneMinuteLater()
	{
		CronExpression cron = CronExpression.Parse("* * * * *");
		DateTimeOffset after = new(2026, 8, 19, 10, 30, 15, TimeSpan.Zero);

		DateTimeOffset next = cron.GetNextOccurrence(after);

		Assert.Equal(new DateTimeOffset(2026, 8, 19, 10, 31, 0, TimeSpan.Zero), next);
	}

	[Fact]
	public void GetNextOccurrence_DailyAtFixedHour_RollsToTomorrowWhenPastToday()
	{
		// "0 2 * * *" -- daily at 02:00. Asking from 10:30 the same day must roll to
		// tomorrow's 02:00, not silently return an earlier time today.
		CronExpression cron = CronExpression.Parse("0 2 * * *");
		DateTimeOffset after = new(2026, 8, 19, 10, 30, 0, TimeSpan.Zero);

		DateTimeOffset next = cron.GetNextOccurrence(after);

		Assert.Equal(new DateTimeOffset(2026, 8, 20, 2, 0, 0, TimeSpan.Zero), next);
	}

	[Fact]
	public void GetNextOccurrence_DailyAtFixedHour_SameDayWhenStillAhead()
	{
		CronExpression cron = CronExpression.Parse("0 2 * * *");
		DateTimeOffset after = new(2026, 8, 19, 0, 0, 0, TimeSpan.Zero);

		DateTimeOffset next = cron.GetNextOccurrence(after);

		Assert.Equal(new DateTimeOffset(2026, 8, 19, 2, 0, 0, TimeSpan.Zero), next);
	}

	[Fact]
	public void GetNextOccurrence_StepExpression_FiresEveryFifteenMinutes()
	{
		CronExpression cron = CronExpression.Parse("*/15 * * * *");
		DateTimeOffset after = new(2026, 8, 19, 10, 16, 0, TimeSpan.Zero);

		DateTimeOffset next = cron.GetNextOccurrence(after);

		Assert.Equal(new DateTimeOffset(2026, 8, 19, 10, 30, 0, TimeSpan.Zero), next);
	}

	[Fact]
	public void GetNextOccurrence_ListExpression_FiresOnEachListedValue()
	{
		CronExpression cron = CronExpression.Parse("0 6,18 * * *");
		DateTimeOffset after = new(2026, 8, 19, 6, 0, 0, TimeSpan.Zero);

		DateTimeOffset next = cron.GetNextOccurrence(after);

		Assert.Equal(new DateTimeOffset(2026, 8, 19, 18, 0, 0, TimeSpan.Zero), next);
	}

	[Fact]
	public void GetNextOccurrence_RangeExpression_RespectsBounds()
	{
		CronExpression cron = CronExpression.Parse("0 9-17 * * *");
		DateTimeOffset after = new(2026, 8, 19, 17, 30, 0, TimeSpan.Zero);

		DateTimeOffset next = cron.GetNextOccurrence(after);

		// Past 17:00 for the day -- rolls to tomorrow's 09:00, not today's remaining hours.
		Assert.Equal(new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero), next);
	}

	/// <summary>
	/// POSIX/Vixie cron day-matching: when BOTH day-of-month and day-of-week are
	/// restricted, a day matches if EITHER matches (OR), not both (AND).
	/// "0 0 1 * 1" fires on the 1st of the month OR every Monday.
	/// </summary>
	[Fact]
	public void GetNextOccurrence_BothDayFieldsRestricted_MatchesEither()
	{
		CronExpression cron = CronExpression.Parse("0 0 1 * 1");

		// 2026-08-19 is a Wednesday; the next Monday is 2026-08-24, before the next
		// 1st-of-month (2026-09-01) -- so the OR semantics must pick the Monday.
		DateTimeOffset after = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
		DateTimeOffset next = cron.GetNextOccurrence(after);

		Assert.Equal(new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero), next);
		Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
	}

	[Fact]
	public void GetNextOccurrence_OnlyDayOfWeekRestricted_MatchesThatFieldAlone()
	{
		// "0 3 * * 1" -- 03:00 every Monday. day-of-month is unrestricted ("*"), so
		// only day-of-week decides.
		CronExpression cron = CronExpression.Parse("0 3 * * 1");
		DateTimeOffset after = new(2026, 8, 19, 0, 0, 0, TimeSpan.Zero); // Wednesday

		DateTimeOffset next = cron.GetNextOccurrence(after);

		Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
		Assert.Equal(new DateTimeOffset(2026, 8, 24, 3, 0, 0, TimeSpan.Zero), next);
	}
}
