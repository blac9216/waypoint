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
/// Issue #409: a thrown <see cref="Waypoint.Core.Secrets.MasterKeyUnavailableException"/>
/// must reach the client as a distinct, operator-actionable <c>503
/// master_key_unavailable</c> -- not the generic <c>500 internal_error</c> every other
/// unhandled exception falls back to (<see cref="UnhandledExceptionTests"/>'s arm).
/// Drives <c>ErrorHandlingMiddleware</c>'s dedicated catch clause end to end through
/// the real HTTP pipeline via <see cref="ThrowingApiFactory"/>'s fault-injection route,
/// the same technique #75/#76 already use for the other middleware arms -- this proves
/// the mapping without standing up Postgres/master-key infrastructure. The
/// key-present/unaffected half of the acceptance criteria is covered by
/// <c>CredentialsApiTests</c> (Postgres-backed), which already configures a real key
/// file and asserts ordinary 200/201/202 outcomes for every credential write.
/// </summary>
public sealed class MasterKeyUnavailableTests : IClassFixture<ThrowingApiFactory>
{
	private readonly ThrowingApiFactory _factory;

	public MasterKeyUnavailableTests(ThrowingApiFactory factory)
	{
		_factory = factory;
	}

	[Fact]
	public async Task MasterKeyUnavailableException_Returns503_WithMasterKeyUnavailableCode()
	{
		HttpClient client = _factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync(ThrowingApiFactory.MasterKeyThrowPath);

		Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "master_key_unavailable");
	}

	[Fact]
	public async Task MasterKeyUnavailableException_ResponseBody_DoesNotEchoTheDetailedExceptionMessage()
	{
		HttpClient client = _factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync(ThrowingApiFactory.MasterKeyThrowPath);
		string body = await response.Content.ReadAsStringAsync();

		// The security-relevant half of #409: the mounted key file's path (and any
		// other filesystem detail the real exception message carries) is server
		// layout, not something the wire body may echo -- only the log gets it.
		Assert.DoesNotContain(ThrowingApiFactory.MasterKeyExceptionMessage, body, StringComparison.Ordinal);
		Assert.DoesNotContain("/fake/server/path", body, StringComparison.Ordinal);
		Assert.DoesNotContain("WAYPOINT_MASTER_KEY_FILE", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task MasterKeyUnavailableException_ResponseBody_TellsTheOperatorWhatToDo()
	{
		HttpClient client = _factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync(ThrowingApiFactory.MasterKeyThrowPath);
		string body = await response.Content.ReadAsStringAsync();

		// Actionable, not just distinct: the message must point the operator at the
		// remediation (mount the key file / deploy docs), matching
		// ErrorHandlingMiddleware.MasterKeyUnavailableMessage.
		Assert.Contains("mount", body, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("deploy/README.md", body, StringComparison.Ordinal);
	}
}
