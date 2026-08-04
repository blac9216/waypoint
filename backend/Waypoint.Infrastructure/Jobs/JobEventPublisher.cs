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
using Waypoint.Core.Logging;

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
///
/// Because it is swallowed, the log line is the *only* thing an operator ever sees for a
/// dropped event, which is why <see cref="EmitAsync"/> goes to some length to say which
/// of the two failures happened -- never reaching the server, versus reaching it and
/// being blocked -- rather than guessing from the exception. See the comment there.
/// </summary>
public sealed partial class JobEventPublisher : IJobEventPublisher
{
	private readonly string _connectionString;
	private readonly int _commandTimeoutSeconds;
	private readonly ISecretRedactor _redactor;
	private readonly ILogger<JobEventPublisher> _logger;

	public JobEventPublisher(string connectionString, int commandTimeoutSeconds, ISecretRedactor redactor, ILogger<JobEventPublisher> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(commandTimeoutSeconds, 0);
		ArgumentNullException.ThrowIfNull(redactor);
		ArgumentNullException.ThrowIfNull(logger);

		_connectionString = connectionString;
		_commandTimeoutSeconds = commandTimeoutSeconds;
		_redactor = redactor;
		_logger = logger;
	}

	public async Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

		await using NpgsqlConnection connection = new(_connectionString);

		// Opening the connection and running the INSERT are two separate try blocks, and
		// which one failed is the ONLY thing that decides whether this method is allowed to
		// mention lock contention. That is not a style preference -- the two failures are
		// indistinguishable as exception objects. Measured against Npgsql 8.0.5:
		//
		//   connect to a closed port       NpgsqlException   IsTransient=true   inner SocketException
		//   connect that stalls            NpgsqlException   IsTransient=true   inner TimeoutException
		//   command timeout, server up     NpgsqlException   IsTransient=true   inner TimeoutException
		//   server kills the backend       PostgresException IsTransient=true   (57P01, no inner)
		//   bad SQL / bad payload          PostgresException IsTransient=false  (22P02, no inner)
		//
		// A stalled connect therefore carries the same type, the same IsTransient flag and
		// the same inner exception type as a genuine command timeout. No predicate over the
		// exception can separate them.
		//
		// The previous revision keyed the contention diagnosis on `IsTransient`, so with
		// Postgres simply unreachable it told the operator the emit "timed out after 5s --
		// likely lock contention on trg_job_events_assign_seq". Nothing had been contended,
		// no statement had run, and no 5s had elapsed. A confidently wrong diagnosis is
		// worse than a vague one: it sends someone to inspect a trigger that was never
		// involved, and this log line is the only visibility a dropped event ever gets.
		try
		{
			await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (NpgsqlException exception)
		{
			LogEventEmitCouldNotReachPostgres(eventType, jobId, runId, exception);
			return;
		}

		try
		{
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
			// security.md control 1 at the Postgres sink: the payload is scrubbed of
			// in-play secret values on this path exactly as BufferedJobEventWriter
			// scrubs the batched path (there at enqueue, here at emit -- this method IS
			// the emit).
			command.Parameters.AddWithValue(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : _redactor.Redact(payloadJson));

			await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (NpgsqlException exception) when (IsCommandTimeout(exception))
		{
			// The connection is open, so the server was reached and this single-row INSERT
			// did not finish inside its budget -- both stated as facts. Contention on
			// trg_job_events_assign_seq is what #117 measured and by far the likeliest
			// cause, but this code has not confirmed it, so the line says "likeliest",
			// not "is".
			LogEventEmitTimedOut(eventType, jobId, runId, _commandTimeoutSeconds, exception);
		}
		catch (NpgsqlException exception)
		{
			// Every other Npgsql failure, including a PostgresException (which derives from
			// NpgsqlException) and a connection lost mid-statement. Catching the base type
			// rather than PostgresException matters: before the split above, a transient
			// NpgsqlException that was not a PostgresException only stayed inside the
			// "never throws" contract because the contention filter happened to swallow it.
			LogEventEmitFailed(eventType, jobId, runId, exception);
		}
	}

	/// <summary>
	/// Npgsql surfaces an expired <see cref="NpgsqlCommand.CommandTimeout"/> as an
	/// <see cref="NpgsqlException"/> wrapping a <see cref="TimeoutException"/>. This is only
	/// a meaningful test once the connection is open -- a connect that stalls produces the
	/// identical shape (see the table in <see cref="EmitAsync"/>), which is why this is
	/// never applied to a failure of <see cref="NpgsqlConnection.OpenAsync(CancellationToken)"/>.
	/// </summary>
	private static bool IsCommandTimeout(NpgsqlException exception) => exception.InnerException is TimeoutException;

	[LoggerMessage(
		Level = LogLevel.Error,
		Message = "job_events emit for {EventType} (job={JobId}, run={RunId}) reached the server but its INSERT did not finish within {TimeoutSeconds}s; likeliest cause is lock contention on trg_job_events_assign_seq; event dropped")]
	private partial void LogEventEmitTimedOut(string eventType, Guid? jobId, Guid? runId, int timeoutSeconds, Exception exception);

	[LoggerMessage(
		Level = LogLevel.Error,
		Message = "job_events emit for {EventType} (job={JobId}, run={RunId}) could not open a connection to Postgres -- no statement ran, so nothing was contended; event dropped")]
	private partial void LogEventEmitCouldNotReachPostgres(string eventType, Guid? jobId, Guid? runId, Exception exception);

	[LoggerMessage(Level = LogLevel.Error, Message = "job_events emit for {EventType} (job={JobId}, run={RunId}) failed; event dropped")]
	private partial void LogEventEmitFailed(string eventType, Guid? jobId, Guid? runId, Exception exception);
}
