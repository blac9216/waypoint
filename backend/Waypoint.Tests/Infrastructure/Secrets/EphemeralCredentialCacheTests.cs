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

using Waypoint.Core.Logging;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Secrets;

/// <summary>
/// Issue #276: the process-memory-only holding pen behind the ADR-0011 ad hoc "my
/// credentials" flow. No Postgres needed -- these exercise the in-memory contract in
/// isolation (a <c>null</c> connection string makes the best-effort audit write a
/// no-op, proven separately by <c>EphemeralCredentialScanRunTests</c>'s end-to-end
/// canary proof).
/// </summary>
public sealed class EphemeralCredentialCacheTests
{
	private static EphemeralCredentialCache CreateCache(out InPlaySecretRedactor redactor)
	{
		redactor = new InPlaySecretRedactor();
		return new EphemeralCredentialCache(redactor, connectionString: null, new CapturingLogger<EphemeralCredentialCache>());
	}

	[Fact]
	public void Put_ThenTryTake_ReturnsTheSameCredentialExactlyOnce()
	{
		EphemeralCredentialCache cache = CreateCache(out _);
		Guid jobId = Guid.NewGuid();
		EphemeralCredential credential = new("adhoc-user@example.internal", "invented-unit-secret-1234");

		cache.Put(jobId, Guid.NewGuid(), credential, "tester");

		EphemeralCredential? taken = cache.TryTake(jobId);
		Assert.NotNull(taken);
		Assert.Equal(credential.Username, taken!.Username);
		Assert.Equal(credential.Secret, taken.Secret);

		// Single-shot: gone after the first take.
		Assert.Null(cache.TryTake(jobId));
	}

	[Fact]
	public void TryTake_UnknownJobId_ReturnsNull()
	{
		EphemeralCredentialCache cache = CreateCache(out _);
		Assert.Null(cache.TryTake(Guid.NewGuid()));
	}

	[Fact]
	public void Put_TracksTheSecretWithTheRedactorUntilTaken()
	{
		EphemeralCredentialCache cache = CreateCache(out InPlaySecretRedactor redactor);
		Guid jobId = Guid.NewGuid();
		const string secret = "invented-unit-secret-track-5678";

		cache.Put(jobId, Guid.NewGuid(), new EphemeralCredential("user@example.internal", secret), "tester");

		Assert.Equal("line with [REDACTED] inside", redactor.Redact($"line with {secret} inside"));

		cache.TryTake(jobId);

		// Untracked the instant it is taken -- the in-play window closes at consumption,
		// same discipline as DecryptedSecret.Dispose.
		Assert.Equal($"line with {secret} inside", redactor.Redact($"line with {secret} inside"));
	}

	[Fact]
	public void Put_SameJobIdTwice_Throws()
	{
		EphemeralCredentialCache cache = CreateCache(out _);
		Guid jobId = Guid.NewGuid();
		cache.Put(jobId, null, new EphemeralCredential("user@example.internal", "invented-unit-secret-first"), "tester");

		Assert.Throws<InvalidOperationException>(() =>
			cache.Put(jobId, null, new EphemeralCredential("user@example.internal", "invented-unit-secret-second"), "tester"));
	}

	[Fact]
	public void Put_EmptySecret_Throws()
	{
		EphemeralCredentialCache cache = CreateCache(out _);
		Assert.Throws<ArgumentException>(() =>
			cache.Put(Guid.NewGuid(), null, new EphemeralCredential("user@example.internal", string.Empty), "tester"));
	}

	[Fact]
	public void Put_NoActor_Throws()
	{
		EphemeralCredentialCache cache = CreateCache(out _);
		Assert.Throws<ArgumentException>(() =>
			cache.Put(Guid.NewGuid(), null, new EphemeralCredential("user@example.internal", "invented-unit-secret"), actor: " "));
	}
}
