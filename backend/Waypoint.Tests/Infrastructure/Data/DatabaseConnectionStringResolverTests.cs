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

using Microsoft.Extensions.Configuration;
using Npgsql;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Data;

/// <summary>
/// Issue #843 acceptance criteria, exercised directly against
/// <see cref="DatabaseConnectionStringResolver"/> -- the single point every host
/// (API, compliance-runner, download-runner) resolves <c>ConnectionStrings:Waypoint</c>
/// from a non-secret base value plus an optional mounted password file.
/// </summary>
public sealed class DatabaseConnectionStringResolverTests
{
	private const string BaseConnectionString = "Host=db.example.internal;Port=5432;Database=waypoint;Username=waypoint";

	[Fact]
	public void Resolve_WithNoPasswordFile_ReturnsConnectionStringUnchanged()
	{
		// Precedence rule: an ordinary complete connection string (test fixtures,
		// non-Compose hosts) already carrying Password= must be untouched when no
		// password file is configured.
		const string complete = BaseConnectionString + ";Password=already-set";

		string? resolved = DatabaseConnectionStringResolver.Resolve(complete, passwordFilePath: null);

		Assert.Equal(complete, resolved);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void Resolve_WithBlankPasswordFilePath_ReturnsConnectionStringUnchanged(string blankPath)
	{
		string? resolved = DatabaseConnectionStringResolver.Resolve(BaseConnectionString, blankPath);

		Assert.Equal(BaseConnectionString, resolved);
	}

	[Fact]
	public void Resolve_WithNullConnectionString_AndNoPasswordFile_ReturnsNull()
	{
		string? resolved = DatabaseConnectionStringResolver.Resolve(connectionString: null, passwordFilePath: null);

		Assert.Null(resolved);
	}

	[Fact]
	public void Resolve_WithPasswordFile_AppendsPasswordFromFile()
	{
		string path = WritePasswordFileRaw("s3cr3t-value");

		try
		{
			string? resolved = DatabaseConnectionStringResolver.Resolve(BaseConnectionString, path);

			NpgsqlConnectionStringBuilder builder = new(resolved);
			Assert.Equal("s3cr3t-value", builder.Password);
			Assert.Equal("db.example.internal", builder.Host);
			Assert.Equal("waypoint", builder.Username);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Resolve_WithPasswordFile_OverridesAnExistingInlinePassword()
	{
		// Precedence rule: when a password file IS configured, it always wins over
		// whatever Password= the base connection string already carried.
		const string complete = BaseConnectionString + ";Password=stale-inline-password";
		string path = WritePasswordFileRaw("fresh-from-file");

		try
		{
			string? resolved = DatabaseConnectionStringResolver.Resolve(complete, path);

			NpgsqlConnectionStringBuilder builder = new(resolved);
			Assert.Equal("fresh-from-file", builder.Password);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Resolve_WithPasswordFile_TrimsExactlyOneTrailingLfNewline()
	{
		string path = WritePasswordFileRaw("trailing-lf-password\n");

		try
		{
			string? resolved = DatabaseConnectionStringResolver.Resolve(BaseConnectionString, path);

			Assert.Equal("trailing-lf-password", new NpgsqlConnectionStringBuilder(resolved).Password);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Resolve_WithPasswordFile_TrimsExactlyOneTrailingCrLfNewline()
	{
		string path = WritePasswordFileRaw("trailing-crlf-password\r\n");

		try
		{
			string? resolved = DatabaseConnectionStringResolver.Resolve(BaseConnectionString, path);

			Assert.Equal("trailing-crlf-password", new NpgsqlConnectionStringBuilder(resolved).Password);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Resolve_WithPasswordFile_DoesNotTrimTrailingSpaces()
	{
		// Only a trailing newline is a file-terminator artifact to trim -- trailing
		// spaces are legitimate password content and must survive untouched.
		string path = WritePasswordFileRaw("trailing-space-password  ");

		try
		{
			string? resolved = DatabaseConnectionStringResolver.Resolve(BaseConnectionString, path);

			Assert.Equal("trailing-space-password  ", new NpgsqlConnectionStringBuilder(resolved).Password);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Theory]
	[InlineData("semi;colon")]
	[InlineData("equals=sign")]
	[InlineData("has\"double\"quotes")]
	[InlineData("has'single'quotes")]
	[InlineData("space and\ttab")]
	[InlineData("bäckslash\\and-ünïcödé-Ω-字")]
	[InlineData("semi;equals=quote\"mix'ed")]
	public void Resolve_WithSpecialCharacterPassword_RoundTripsThroughNpgsqlBuilder(string specialPassword)
	{
		// The acceptance criterion is "special-character passwords work" -- proven by
		// round-tripping through NpgsqlConnectionStringBuilder (proper escaping) rather
		// than string concatenation, which a `;`/`=` in the password would corrupt.
		string path = WritePasswordFileRaw(specialPassword);

		try
		{
			string? resolved = DatabaseConnectionStringResolver.Resolve(BaseConnectionString, path);

			Assert.Equal(specialPassword, new NpgsqlConnectionStringBuilder(resolved).Password);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Resolve_WithMissingPasswordFile_ThrowsWithoutLeakingContentsAndNamesThePath()
	{
		string missingPath = Path.Combine(Path.GetTempPath(), $"waypoint-843-missing-{Guid.NewGuid():N}.txt");

		InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
			() => DatabaseConnectionStringResolver.Resolve(BaseConnectionString, missingPath));

		Assert.Contains(missingPath, exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Resolve_WithEmptyPasswordFile_ThrowsClearly()
	{
		string path = WritePasswordFileRaw(string.Empty);

		try
		{
			InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
				() => DatabaseConnectionStringResolver.Resolve(BaseConnectionString, path));

			Assert.Contains("empty", exception.Message, StringComparison.OrdinalIgnoreCase);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Resolve_WithFileContainingOnlyANewline_ThrowsClearly()
	{
		// A file that is nothing but its own line terminator is empty once the
		// terminator is trimmed -- must fail the same way a zero-byte file does, not
		// silently produce an empty-string password.
		string path = WritePasswordFileRaw("\n");

		try
		{
			Assert.Throws<InvalidOperationException>(
				() => DatabaseConnectionStringResolver.Resolve(BaseConnectionString, path));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Resolve_WithUnreadableDirectoryAsPasswordFilePath_ThrowsClearly()
	{
		string directoryPath = Path.Combine(Path.GetTempPath(), $"waypoint-843-dir-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directoryPath);

		try
		{
			Assert.Throws<InvalidOperationException>(
				() => DatabaseConnectionStringResolver.Resolve(BaseConnectionString, directoryPath));
		}
		finally
		{
			Directory.Delete(directoryPath);
		}
	}

	[Fact]
	public void Resolve_WithPasswordFile_AndNoBaseConnectionString_ThrowsClearly()
	{
		string path = WritePasswordFileRaw("irrelevant");

		try
		{
			foreach (string? emptyBase in new[] { null, "", "   " })
			{
				Assert.Throws<InvalidOperationException>(
					() => DatabaseConnectionStringResolver.Resolve(emptyBase, path));
			}
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void ResolveAndApply_WithNoPasswordFileConfigured_LeavesConnectionStringUnchanged()
	{
		const string complete = BaseConnectionString + ";Password=already-set";
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ConnectionStrings:Waypoint"] = complete
			})
			.Build();

		DatabaseConnectionStringResolver.ResolveAndApply(configuration);

		Assert.Equal(complete, configuration.GetConnectionString("Waypoint"));
	}

	[Fact]
	public void ResolveAndApply_WithPasswordFileConfigured_OverwritesConnectionStringsInPlace()
	{
		string path = WritePasswordFileRaw("resolved-through-apply");
		try
		{
			IConfiguration configuration = new ConfigurationBuilder()
				.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["ConnectionStrings:Waypoint"] = BaseConnectionString,
					["Database:PasswordFile"] = path
				})
				.Build();

			DatabaseConnectionStringResolver.ResolveAndApply(configuration);

			string? resolved = configuration.GetConnectionString("Waypoint");
			Assert.Equal("resolved-through-apply", new NpgsqlConnectionStringBuilder(resolved).Password);
		}
		finally
		{
			File.Delete(path);
		}
	}

	private static string WritePasswordFileRaw(string rawContents)
	{
		string path = Path.Combine(Path.GetTempPath(), $"waypoint-843-pw-{Guid.NewGuid():N}.txt");
		File.WriteAllText(path, rawContents);
		return path;
	}
}
