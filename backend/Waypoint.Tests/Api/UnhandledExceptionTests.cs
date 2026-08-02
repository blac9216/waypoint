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
/// Pins <c>ErrorHandlingMiddleware</c>'s generic <c>catch (Exception)</c> arm (issue
/// #75) — the one route to a 500 that, unlike every <c>ApiException</c> status path,
/// had no integration test. Two things matter about that arm and both are asserted
/// here: it still answers with the documented envelope, and it never lets the
/// underlying exception's message, type name, or stack trace reach the client
/// (<c>docs/security.md</c> control 1 — a raw exception <c>.ToString()</c> is one of
/// the most common ways a connection string or file path ends up in an HTTP response).
/// </summary>
public sealed class UnhandledExceptionTests : IClassFixture<ThrowingApiFactory>
{
	private readonly ThrowingApiFactory _factory;

	public UnhandledExceptionTests(ThrowingApiFactory factory)
	{
		_factory = factory;
	}

	[Fact]
	public async Task UnhandledException_Returns500WithErrorEnvelope()
	{
		HttpClient client = _factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync(ThrowingApiFactory.ThrowPath);

		Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "internal_error");
	}

	[Fact]
	public async Task UnhandledException_ResponseBody_DoesNotLeakExceptionDetails()
	{
		HttpClient client = _factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync(ThrowingApiFactory.ThrowPath);
		string body = await response.Content.ReadAsStringAsync();

		// The security-relevant half of #75: none of the exception's message, its type
		// name, or a stack-frame marker may appear anywhere in what the client receives.
		Assert.DoesNotContain(ThrowingApiFactory.ExceptionMessage, body, StringComparison.Ordinal);
		Assert.DoesNotContain(nameof(InvalidOperationException), body, StringComparison.Ordinal);
		Assert.DoesNotContain(".cs:line", body, StringComparison.Ordinal);
		Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
	}
}
