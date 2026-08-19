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

using Microsoft.Extensions.Logging.Abstractions;
using Waypoint.Core.Auth;
using Waypoint.Core.Authorization;
using Waypoint.Core.Configuration;
using Waypoint.Infrastructure.Auth;
using Waypoint.Tests.Support;

namespace Waypoint.Tests.Infrastructure;

public sealed class InMemoryLocalAuthenticationServiceTests
{
	private const string Password = "correct-horse-battery-staple";

	[Fact]
	public void Authenticate_WithNoPasswordHashConfigured_ThrowsNotReady()
	{
		// Issue #505: an unresolved admin hash is "the backend isn't ready", not
		// "credentials rejected" — the two must be distinguishable so the API can
		// answer 503 auth_not_ready instead of a misleading 401 invalid_credentials.
		InMemoryLocalAuthenticationService service = CreateService(new LocalAuthOptions
		{
			AdminUsername = "admin",
			AdminPasswordHash = null
		});

		Assert.Throws<LocalAuthNotReadyException>(() => service.Authenticate("admin", Password));
	}

	[Fact]
	public void Authenticate_WithCorrectCredentials_ReturnsAdminSession()
	{
		InMemoryLocalAuthenticationService service = CreateService(BuildOptions());

		LocalSession? session = service.Authenticate("admin", Password);

		Assert.NotNull(session);
		Assert.Equal("admin", session!.Username);
		Assert.Equal(WaypointRole.Admin, session.Role);
		Assert.True(session.ExpiresAt > DateTimeOffset.UtcNow);
	}

	[Fact]
	public void Authenticate_WithWrongPassword_ReturnsNull()
	{
		InMemoryLocalAuthenticationService service = CreateService(BuildOptions());

		LocalSession? session = service.Authenticate("admin", "wrong-password");

		Assert.Null(session);
	}

	[Fact]
	public void Authenticate_WithWrongUsername_ReturnsNull()
	{
		InMemoryLocalAuthenticationService service = CreateService(BuildOptions());

		LocalSession? session = service.Authenticate("someone-else", Password);

		Assert.Null(session);
	}

	[Fact]
	public void ValidateToken_ForIssuedSession_ReturnsSameSession()
	{
		InMemoryLocalAuthenticationService service = CreateService(BuildOptions());
		LocalSession issued = service.Authenticate("admin", Password)!;

		LocalSession? validated = service.ValidateToken(issued.Token);

		Assert.NotNull(validated);
		Assert.Equal(issued.Token, validated!.Token);
	}

	[Fact]
	public void ValidateToken_ForUnknownToken_ReturnsNull()
	{
		InMemoryLocalAuthenticationService service = CreateService(BuildOptions());

		LocalSession? validated = service.ValidateToken("not-a-real-token");

		Assert.Null(validated);
	}

	[Fact]
	public void ValidateToken_ForExpiredSession_ReturnsNull()
	{
		InMemoryLocalAuthenticationService service = CreateService(BuildOptions(sessionLifetime: TimeSpan.FromMilliseconds(1)));
		LocalSession issued = service.Authenticate("admin", Password)!;

		Thread.Sleep(50);
		LocalSession? validated = service.ValidateToken(issued.Token);

		Assert.Null(validated);
	}

	private static InMemoryLocalAuthenticationService CreateService(LocalAuthOptions options)
	{
		return new InMemoryLocalAuthenticationService(
			new StaticOptionsMonitor<LocalAuthOptions>(options),
			NullLogger<InMemoryLocalAuthenticationService>.Instance);
	}

	private static LocalAuthOptions BuildOptions(TimeSpan? sessionLifetime = null)
	{
		return new LocalAuthOptions
		{
			AdminUsername = "admin",
			AdminPasswordHash = Pbkdf2PasswordHasher.Hash(Password),
			SessionLifetime = sessionLifetime ?? TimeSpan.FromHours(1)
		};
	}
}
