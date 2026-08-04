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

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Waypoint.Tests.Support;

/// <summary>
/// An <see cref="ILogger{TCategoryName}"/> that records what was logged, and -- the part
/// that matters -- reports <see cref="IsEnabled"/> as <c>true</c>.
///
/// This exists because of a measurable coverage gap, not for convenience.
/// <c>[LoggerMessage]</c> generates a partial method whose body begins with
/// <c>if (!logger.IsEnabled(level)) return;</c>. <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger{T}"/>
/// answers <c>false</c>, so every generated log method short-circuits on line one and the
/// rest of the generated body -- the state struct, the formatter, the actual
/// <c>Log</c> call -- is never executed. PR #126 read the resulting numbers as inherent
/// "dilution" from source generation. PR #104 had already found the opposite: the lines
/// are reachable, and a logger that says yes reaches them.
///
/// It is also not only a coverage instrument. Log lines are the operator's only view of
/// a background service, and asserting on them turns "this line exists" into "this line
/// says the right thing with the right values in it".
///
/// Thread-safe: the dispatcher and the recovery sweep log from several threads at once.
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
	private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

	/// <summary>Everything logged so far, oldest first.</summary>
	public IReadOnlyList<CapturedLogEntry> Entries => [.. _entries];

	public IDisposable BeginScope<TState>(TState state)
		where TState : notnull => NullScope.Instance;

	/// <summary>
	/// Always <c>true</c>. This is the whole point of the type: it is what lets a
	/// <c>[LoggerMessage]</c>-generated body run past its guard clause.
	/// </summary>
	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
	{
		ArgumentNullException.ThrowIfNull(formatter);

		Dictionary<string, object?> properties = state is IEnumerable<KeyValuePair<string, object?>> structured
			? structured.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
			: [];
		_entries.Enqueue(new CapturedLogEntry(logLevel, eventId, formatter(state, exception), exception, properties));
	}

	/// <summary>The one entry at <paramref name="level"/>; throws if there is not exactly one.</summary>
	public CapturedLogEntry OnlyEntryAt(LogLevel level) => Entries.Single(entry => entry.Level == level);

	/// <summary>Every entry at <paramref name="level"/>, oldest first.</summary>
	public IReadOnlyList<CapturedLogEntry> EntriesAt(LogLevel level) => [.. Entries.Where(entry => entry.Level == level)];

	private sealed class NullScope : IDisposable
	{
		public static readonly NullScope Instance = new();

		public void Dispose()
		{
			// Nothing to release; scopes are not asserted on.
		}
	}
}

/// <summary>One captured log call, already rendered through the generated formatter.</summary>
public sealed record CapturedLogEntry(
	LogLevel Level,
	EventId EventId,
	string Message,
	Exception? Exception,
	IReadOnlyDictionary<string, object?> Properties);
