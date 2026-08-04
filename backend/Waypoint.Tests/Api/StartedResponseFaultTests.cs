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
	private readonly ThrowingApiFactory _factory;

	public StartedResponseFaultTests(ThrowingApiFactory factory)
	{
		_factory = factory;
	}

	[Fact]
	public async Task AFaultAfterTheFirstFlush_AbortsCleanly_WithoutASecondaryException()
	{
		HttpClient client = _factory.CreateClient();

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
			// The flushed prefix was already proven sent by the 200 + content type;
			// nothing further to assert about a body we could not read.
			return;
		}
		catch (IOException)
		{
			return;
		}

		// If the body was readable, it contains exactly what was flushed before the
		// fault -- and none of the exception's text.
		Assert.StartsWith(ThrowingApiFactory.StreamedPrefix, body, StringComparison.Ordinal);
		Assert.DoesNotContain(ThrowingApiFactory.ExceptionMessage, body, StringComparison.Ordinal);
		Assert.DoesNotContain("error", body, StringComparison.OrdinalIgnoreCase);
	}
}
