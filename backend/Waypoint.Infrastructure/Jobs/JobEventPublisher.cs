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

using Microsoft.Extensions.Logging;
using Npgsql;
using Waypoint.Core.Jobs;

namespace Waypoint.Infrastructure.Jobs;

/// <summary>
/// Writes one row to <c>job_events</c> per call, each on its own connection with an
/// explicit <see cref="NpgsqlCommand.CommandTimeout"/> (see
/// <see cref="JobEngineOptions.EventCommandTimeoutSeconds"/> for the chosen budget and
/// why it is deliberately shorter than Npgsql's inherited 30s default -- #108/#117).
/// Every caller in this codebase (the dispatcher, the recovery sweep) emits *after* its
/// own state-changing write has already committed on a separate connection, so this
/// type never participates in the caller's transaction and the ordering lock
/// (<c>trg_job_events_assign_seq</c>) is never held across another row lock -- the
/// "emit last, in a short transaction" rule the schema's doc comments and #117 ask for.
/// A failed emit is logged and swallowed: the event stream is best-effort observability
/// for issue #6's SSE layer, never the record of truth for job/run state.
/// </summary>
public sealed partial class JobEventPublisher : IJobEventPublisher
{
	private readonly string _connectionString;
	private readonly int _commandTimeoutSeconds;
	private readonly ILogger<JobEventPublisher> _logger;

	public JobEventPublisher(string connectionString, int commandTimeoutSeconds, ILogger<JobEventPublisher> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(commandTimeoutSeconds, 0);
		ArgumentNullException.ThrowIfNull(logger);

		_connectionString = connectionString;
		_commandTimeoutSeconds = commandTimeoutSeconds;
		_logger = logger;
	}

	public async Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

		try
		{
			await using NpgsqlConnection connection = new(_connectionString);
			await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

			await using NpgsqlCommand command = new(
				"""
				INSERT INTO job_events (run_id, job_id, event_type, payload)
				VALUES ($1, $2, $3, $4::jsonb)
				""", connection)
			{
				CommandTimeout = _commandTimeoutSeconds
			};
			command.Parameters.AddWithValue((object?)runId ?? DBNull.Value);
			command.Parameters.AddWithValue((object?)jobId ?? DBNull.Value);
			command.Parameters.AddWithValue(eventType);
			command.Parameters.AddWithValue(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);

			await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (NpgsqlException exception) when (IsLikelyLockContention(exception))
		{
			// A command timeout this short, on a single-row INSERT, is the documented
			// signature of another writer holding trg_job_events_assign_seq's global
			// advisory lock past this event's budget -- name that instead of surfacing a
			// bare Npgsql timeout.
			LogEventEmitTimedOutOnLockContention(eventType, jobId, runId, _commandTimeoutSeconds, exception);
		}
		catch (PostgresException exception)
		{
			LogEventEmitFailed(eventType, jobId, runId, exception);
		}
	}

	private static bool IsLikelyLockContention(NpgsqlException exception) =>
		exception.InnerException is TimeoutException || exception is { IsTransient: true };

	[LoggerMessage(
		Level = LogLevel.Error,
		Message = "job_events emit for {EventType} (job={JobId}, run={RunId}) timed out after {TimeoutSeconds}s -- likely lock contention on trg_job_events_assign_seq; event dropped")]
	private partial void LogEventEmitTimedOutOnLockContention(string eventType, Guid? jobId, Guid? runId, int timeoutSeconds, Exception exception);

	[LoggerMessage(Level = LogLevel.Error, Message = "job_events emit for {EventType} (job={JobId}, run={RunId}) failed; event dropped")]
	private partial void LogEventEmitFailed(string eventType, Guid? jobId, Guid? runId, Exception exception);
}
