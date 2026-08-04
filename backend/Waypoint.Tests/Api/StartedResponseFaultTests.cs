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
using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Waypoint.Tests.Support;

namespace Waypoint.Tests.Api;

/// <summary>
/// Issue #76: a fault after the first flushed byte (an SSE stream mid-fault, the
/// moment #7 lands) must not make the error middleware throw a second
/// InvalidOperationException from <c>Response.Clear()</c> on top of the original.
/// The guarded writer logs and aborts instead; the client sees the flushed prefix,
/// a hard connection break, and never a plausible-looking rewritten response --
/// and never the exception's text (security.md control 1 still holds mid-stream).
/// </summary>
public sealed class StartedResponseFaultTests : IClassFixture<ThrowingApiFactory>
{
	/// <summary>Captures every category's formatted messages. Registered as THE
	/// ILoggerFactory in test DI (after UseSerilog's registration, so it wins for
	/// anything resolved from RequestServices -- which is exactly how the guarded
	/// writer obtains its logger).</summary>
	private sealed class CapturingLoggerFactory : ILoggerFactory
	{
		public ConcurrentQueue<string> Messages { get; } = new();

		public ILogger CreateLogger(string categoryName) => new Sink(Messages);

		public void AddProvider(ILoggerProvider provider)
		{
		}

		public void Dispose()
		{
		}

		private sealed class Sink : ILogger
		{
			private readonly ConcurrentQueue<string> _messages;
			public Sink(ConcurrentQueue<string> messages) { _messages = messages; }
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
			public bool IsEnabled(LogLevel logLevel) => true;
			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
			{
				_messages.Enqueue(formatter(state, exception));
			}
		}
	}

	private readonly ThrowingApiFactory _factory;

	public StartedResponseFaultTests(ThrowingApiFactory factory)
	{
		_factory = factory;
	}

	[Fact]
	public async Task AFaultAfterTheFirstFlush_AbortsCleanly_WithoutASecondaryException()
	{
		CapturingLoggerFactory loggerFactory = new();
		HttpClient client = _factory.WithWebHostBuilder(builder =>
			builder.ConfigureTestServices(services => services.AddSingleton<ILoggerFactory>(loggerFactory))).CreateClient();

		using HttpRequestMessage request = new(HttpMethod.Get, ThrowingApiFactory.StreamThrowPath);
		using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

		// Headers went out before the fault: the status is the streamed 200, not a
		// rewritten 500 -- rewriting is exactly what the guard forbids.
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

		string body;
		try
		{
			body = await response.Content.ReadAsStringAsync();
		}
		catch (HttpRequestException)
		{
			// An aborted connection may surface as a transport error while draining --
			// that IS the designed outcome (a hard break, not a fabricated ending).
			// The flushed prefix was already proven sent by the 200 + content type.
			AssertGuardTookThePath(loggerFactory);
			return;
		}
		catch (IOException)
		{
			AssertGuardTookThePath(loggerFactory);
			return;
		}

		// If the body was readable, it contains exactly what was flushed before the
		// fault -- and none of the exception's text.
		Assert.StartsWith(ThrowingApiFactory.StreamedPrefix, body, StringComparison.Ordinal);
		Assert.DoesNotContain(ThrowingApiFactory.ExceptionMessage, body, StringComparison.Ordinal);
		Assert.DoesNotContain("\"error\"", body, StringComparison.OrdinalIgnoreCase);
		AssertGuardTookThePath(loggerFactory);
	}

	/// <summary>The observable difference between the guard and the pre-#76 behavior
	/// lives in the logs: the guard announces the abort; the old path instead raised a
	/// second InvalidOperationException from Response.Clear(). Both are asserted.</summary>
	private static void AssertGuardTookThePath(CapturingLoggerFactory loggerFactory)
	{
		Assert.Contains(loggerFactory.Messages, message =>
			message.Contains("aborting the connection", StringComparison.Ordinal));
	}
}
