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
using Waypoint.Core.Configuration;

namespace Waypoint.Tests.Core;

/// <summary>
/// Issue #333: the admin password hash should be resolvable from a mounted file, with
/// the pre-existing env-var-bound value kept as a fallback for deployments that have not
/// moved to the file yet. Hash values here are computed at runtime via
/// <see cref="Pbkdf2PasswordHasher"/> (mirroring the #62 test style) rather than
/// committed as literals, so nothing that looks like a real credential lands in the repo.
/// </summary>
public sealed class LocalAuthOptionsPostConfigureTests : IDisposable
{
	private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"waypoint-test-hash-{Guid.NewGuid():N}.txt");

	[Fact]
	public void PostConfigure_WithFileConfigured_ReadsHashFromFile()
	{
		string expectedHash = Pbkdf2PasswordHasher.Hash("file-delivered-password");
		File.WriteAllText(_tempFile, expectedHash);

		LocalAuthOptions options = new()
		{
			AdminPasswordHashFile = _tempFile,
			AdminPasswordHash = null
		};

		CreateSubject().PostConfigure(name: null, options);

		Assert.Equal(expectedHash, options.AdminPasswordHash);
	}

	[Fact]
	public void PostConfigure_WithFileConfigured_TrimsTrailingNewline()
	{
		string expectedHash = Pbkdf2PasswordHasher.Hash("file-delivered-password");
		File.WriteAllText(_tempFile, expectedHash + "\n");

		LocalAuthOptions options = new()
		{
			AdminPasswordHashFile = _tempFile,
			AdminPasswordHash = null
		};

		CreateSubject().PostConfigure(name: null, options);

		Assert.Equal(expectedHash, options.AdminPasswordHash);
	}

	[Fact]
	public void PostConfigure_WithNoFileConfigured_FallsBackToEnvSourcedValue()
	{
		string envHash = Pbkdf2PasswordHasher.Hash("env-delivered-password");
		LocalAuthOptions options = new()
		{
			AdminPasswordHashFile = null,
			AdminPasswordHash = envHash
		};

		CreateSubject().PostConfigure(name: null, options);

		Assert.Equal(envHash, options.AdminPasswordHash);
	}

	[Fact]
	public void PostConfigure_WithFilePathSetButFileMissing_FallsBackToEnvSourcedValue()
	{
		string envHash = Pbkdf2PasswordHasher.Hash("env-delivered-password");
		LocalAuthOptions options = new()
		{
			AdminPasswordHashFile = Path.Combine(Path.GetTempPath(), $"waypoint-test-missing-{Guid.NewGuid():N}.txt"),
			AdminPasswordHash = envHash
		};

		CreateSubject().PostConfigure(name: null, options);

		Assert.Equal(envHash, options.AdminPasswordHash);
	}

	[Fact]
	public void PostConfigure_WithBothFileAndEnvConfigured_FileTakesPrecedence()
	{
		string fileHash = Pbkdf2PasswordHasher.Hash("file-delivered-password");
		string envHash = Pbkdf2PasswordHasher.Hash("env-delivered-password");
		File.WriteAllText(_tempFile, fileHash);

		LocalAuthOptions options = new()
		{
			AdminPasswordHashFile = _tempFile,
			AdminPasswordHash = envHash
		};

		CreateSubject().PostConfigure(name: null, options);

		Assert.Equal(fileHash, options.AdminPasswordHash);
		Assert.NotEqual(envHash, options.AdminPasswordHash);
	}

	[Fact]
	public void PostConfigure_WithNeitherFileNorEnvConfigured_LeavesHashNull()
	{
		LocalAuthOptions options = new()
		{
			AdminPasswordHashFile = null,
			AdminPasswordHash = null
		};

		CreateSubject().PostConfigure(name: null, options);

		Assert.Null(options.AdminPasswordHash);
	}

	private static LocalAuthOptionsPostConfigure CreateSubject() =>
		new(NullLogger<LocalAuthOptionsPostConfigure>.Instance);

	public void Dispose()
	{
		if (File.Exists(_tempFile))
		{
			File.Delete(_tempFile);
		}
	}
}
