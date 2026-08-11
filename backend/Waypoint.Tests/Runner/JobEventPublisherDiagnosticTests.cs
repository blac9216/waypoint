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

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Runner.Jobs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Runner;

/// <summary>
/// The fixtures the publisher suites did not have. Round 1 asked what every fixture in
/// <c>JobEngineLoggingTests</c> and <c>JobEventPublisherTests</c> shares, and the answer
/// was: all of them talk to a live, reachable Postgres container. That shared property was
/// load-bearing (docs/testing.md, "Fixture monoculture"). With the server simply
/// unreachable, <see cref="JobEventPublisher.EmitAsync"/> reported
/// <c>"timed out after 5s -- likely lock contention on trg_job_events_assign_seq"</c>: it
/// had never reached the server, nothing was contended, and no 5s had elapsed.
///
/// A dropped event is invisible apart from this log line, so a confidently wrong line is
/// worse than a vague one -- it sends an operator to inspect a trigger that never ran.
/// These fixtures make that distinction falsifiable, which is what it was missing.
///
/// This class deliberately takes no <c>PostgresFixture</c>: an unreachable server needs no
/// server, so these run outside the Postgres collection, in parallel, and would still run
/// on a machine with no docker at all.
/// </summary>
public sealed class JobEventPublisherDiagnosticTests
{
	// Port 1 is privileged: binding it needs root, so nothing this suite or the compose
	// stack starts can be listening there, and a connect is refused immediately rather
	// than waiting out a timeout. PostgresFixture's own port picker only ever hands out
	// ephemeral ports, so it can never collide with this.
	private const string RefusedConnectionString =
		"Host=127.0.0.1;Port=1;Database=waypoint_test;Username=waypoint_test;Password=waypoint_test;Timeout=2";

	/// <summary>
	/// Connection refused: the emit is still swallowed and still logged at Error, but the
	/// line must say the server was never reached and must not name the ordering lock.
	/// </summary>
	[Fact]
	public async Task AnEmitAgainstAClosedPort_SaysItCouldNotReachPostgres()
	{
		CapturingLogger<JobEventPublisher> logger = new();
		JobEventPublisher publisher = new(RefusedConnectionString, commandTimeoutSeconds: 5, new InPlaySecretRedactor(), logger);

		await publisher.EmitAsync(JobEventTypes.SystemNotice, null, null, "{}", CancellationToken.None);

		AssertHonestUnreachableDiagnostic(logger);
	}

	/// <summary>
	/// The case that decides the shape of the fix, and the reason narrowing the old
	/// predicate to "the real command-timeout signature" would not have been enough.
	///
	/// This listener accepts the TCP connection (the kernel completes the handshake into
	/// the backlog) and then never speaks Postgres, so Npgsql gives up waiting for the
	/// startup response and throws an <c>NpgsqlException</c> wrapping a
	/// <c>TimeoutException</c> -- byte-for-byte the same shape a genuine command timeout
	/// produces, and marked <c>IsTransient</c> just like it. No predicate over the
	/// exception object can tell these apart; only knowing which operation failed can.
	/// So the emit must still report this as "never got there".
	///
	/// <c>JobEventPublisherTests.AStalledConnectAndACommandTimeout_AreIndistinguishableAsExceptions</c>
	/// asserts that premise directly against a live server.
	/// </summary>
	[Fact]
	public async Task AnEmitAgainstASocketThatNeverAnswers_StillSaysItCouldNotReachPostgres()
	{
		using TcpListener silent = new(IPAddress.Loopback, 0);
		silent.Start();
		int port = ((IPEndPoint)silent.LocalEndpoint).Port;

		try
		{
			CapturingLogger<JobEventPublisher> logger = new();
			JobEventPublisher publisher = new(
				$"Host=127.0.0.1;Port={port};Database=waypoint_test;Username=waypoint_test;Password=waypoint_test;Timeout=2",
				commandTimeoutSeconds: 5, new InPlaySecretRedactor(),
				logger);

			await publisher.EmitAsync(JobEventTypes.SystemNotice, null, null, "{}", CancellationToken.None);

			AssertHonestUnreachableDiagnostic(logger);
		}
		finally
		{
			silent.Stop();
		}
	}

	/// <summary>
	/// The swallow contract has to survive the restructure too. Before it, an
	/// <c>NpgsqlException</c> that was neither transient nor a <c>PostgresException</c>
	/// would have escaped <c>EmitAsync</c> entirely -- it only stayed inside the "never
	/// throws" contract because the old, too-broad contention filter happened to catch it.
	/// </summary>
	[Fact]
	public async Task AnEmitAgainstAnUnreachableServer_DoesNotThrowAtTheCaller()
	{
		JobEventPublisher publisher = new(RefusedConnectionString, commandTimeoutSeconds: 5, new InPlaySecretRedactor(), new CapturingLogger<JobEventPublisher>());

		Exception? escaped = await Record.ExceptionAsync(
			() => publisher.EmitAsync(JobEventTypes.JobLog, Guid.NewGuid(), null, "{}", CancellationToken.None));

		Assert.Null(escaped);
	}

	/// <summary>
	/// Cancellation must still win over the unreachable-server path. Splitting the connect
	/// out of the emit created a second place cancellation can surface, and the rule there
	/// is the same as everywhere else: a cancelled token is the one failure an emit does not
	/// swallow, because turning a shutting-down host into a silently dropped event makes a
	/// stopping dispatcher look like it finished its work.
	///
	/// Pointed at an unreachable server on purpose. Against a live one the connection comes
	/// straight out of the pool and the token is not observed until the INSERT, so
	/// <c>JobEventPublisherTests.EmitAsync_WithAnAlreadyCancelledToken_Rethrows</c> only ever
	/// exercises the command-side guard; this is the connect-side one.
	/// </summary>
	[Fact]
	public async Task AnEmitCancelledWhileConnecting_RethrowsRatherThanReportingAnUnreachableServer()
	{
		CapturingLogger<JobEventPublisher> logger = new();
		JobEventPublisher publisher = new(RefusedConnectionString, commandTimeoutSeconds: 5, new InPlaySecretRedactor(), logger);
		using CancellationTokenSource cts = new();
		await cts.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => publisher.EmitAsync(JobEventTypes.SystemNotice, null, null, "{}", cts.Token));

		Assert.Empty(logger.Entries);
	}

	private static void AssertHonestUnreachableDiagnostic(CapturingLogger<JobEventPublisher> logger)
	{
		CapturedLogEntry entry = logger.OnlyEntryAt(LogLevel.Error);

		// What the line must say: it never got there.
		Assert.Contains("could not open a connection", entry.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("event dropped", entry.Message, StringComparison.OrdinalIgnoreCase);
		Assert.NotNull(entry.Exception);

		// What it must not say. These three assertions are the finding: every one of them
		// failed before the fix, because Npgsql marks socket and IO failures IsTransient
		// and the contention filter accepted anything transient.
		Assert.DoesNotContain("trg_job_events_assign_seq", entry.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("contention", entry.Message, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("5s", entry.Message, StringComparison.Ordinal);
	}
}
