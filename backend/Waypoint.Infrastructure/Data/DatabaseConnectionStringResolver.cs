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

using System.Security;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Waypoint.Core.Configuration;
using Waypoint.Infrastructure.DependencyInjection;

namespace Waypoint.Infrastructure.Data;

/// <summary>
/// Issue #843: the single place every host (API, compliance-runner, download-runner)
/// resolves the real <c>ConnectionStrings:Waypoint</c> value it hands to every
/// repository, migrator, readiness reporter, queue component, and worker-registry
/// writer. Each host's <c>Program.cs</c> calls <see cref="Resolve"/> exactly once,
/// before <c>AddWaypointInfrastructure</c>/<c>AddWaypointExecution</c> read the
/// connection string, and writes the result back into <c>IConfiguration</c> under the
/// same <c>ConnectionStrings:Waypoint</c> key -- every downstream consumer keeps
/// calling <c>configuration.GetConnectionString("Waypoint")</c> exactly as before and
/// is untouched by this change (the risk the issue calls out: every repository,
/// migrator, readiness reporter, queue component and worker registry must receive the
/// SAME resolved value, which a single resolve-and-overwrite point guarantees by
/// construction rather than by convention).
///
/// Precedence: <see cref="WaypointDatabaseOptions.PasswordFile"/> unset (null/empty) is
/// a complete no-op -- <paramref name="connectionString"/> passes through byte-for-byte,
/// which is what keeps ordinary complete connection strings (unit/integration test
/// fixtures, a non-Compose host that already embeds <c>Password=</c>) working exactly
/// as before. When the option IS set, the file-backed password always wins: it is
/// parsed onto <paramref name="connectionString"/> via
/// <see cref="NpgsqlConnectionStringBuilder"/> (never string concatenation, so a
/// password containing <c>;</c>, <c>=</c>, quotes, or non-ASCII characters is escaped
/// exactly the way Npgsql itself would escape it), overwriting any <c>Password=</c>
/// already present in the base string.
/// </summary>
public static class DatabaseConnectionStringResolver
{
	/// <summary>
	/// Host-startup convenience wrapper: reads <c>Database:PasswordFile</c> (bound as
	/// <see cref="Waypoint.Core.Configuration.WaypointDatabaseOptions.PasswordFile"/>)
	/// and the current <c>ConnectionStrings:Waypoint</c> value straight off
	/// <paramref name="configuration"/>, resolves the final connection string via
	/// <see cref="Resolve"/>, then writes it back onto <paramref name="configuration"/>
	/// under the same key. Every one of the three hosts' <c>Program.cs</c> calls this
	/// exactly once, immediately after building configuration and before
	/// <c>AddWaypointInfrastructure</c>/<c>AddWaypointExecution</c> ever read the
	/// connection string -- see the type doc comment for why one call site upstream of
	/// every reader is what makes them all receive the identical resolved value,
	/// without a single downstream consumer needing to change.
	///
	/// CONSTRAINT -- no configuration source may be added (or reloaded) after this call.
	/// The indexer writes into the configuration's in-memory chain as it exists right now;
	/// <c>ConfigurationManager</c> rebuilds its provider list whenever a source is added,
	/// which silently discards this write. The failure mode would be the host quietly
	/// connecting with the unresolved base connection string (wrong/absent password), not
	/// an error -- so every host must finish composing its configuration sources BEFORE
	/// calling this, and none of the three does otherwise today.
	/// </summary>
	public static void ResolveAndApply(IConfiguration configuration)
	{
		WaypointDatabaseOptions databaseOptions = configuration
			.GetSection(WaypointDatabaseOptions.SectionName)
			.Get<WaypointDatabaseOptions>()
			?? new WaypointDatabaseOptions();

		string? resolved = Resolve(
			configuration.GetConnectionString(ServiceCollectionExtensions.ConnectionStringName),
			databaseOptions.PasswordFile);

		configuration[$"ConnectionStrings:{ServiceCollectionExtensions.ConnectionStringName}"] = resolved;
	}

	/// <summary>
	/// Resolves the final connection string. Throws <see cref="InvalidOperationException"/>
	/// with an operator-actionable message (naming the configured path, never its
	/// contents) when <paramref name="passwordFilePath"/> is set but the file is
	/// missing, empty, or unreadable, or when no base connection string is configured
	/// to append the password to. This method runs during host startup, before the
	/// request pipeline exists, so a thrown exception here can only ever reach startup
	/// logs/console -- never an API caller (see the type doc comment).
	/// </summary>
	/// <param name="connectionString">
	/// The configured <c>ConnectionStrings:Waypoint</c> value, or <c>null</c>/empty on a
	/// host with no database configured at all (some hosts run fine without one --
	/// e.g. a runner started for health-check-only use). Passed through unchanged
	/// whenever <paramref name="passwordFilePath"/> is not set.
	/// </param>
	/// <param name="passwordFilePath">
	/// <see cref="WaypointDatabaseOptions.PasswordFile"/> -- the mounted password
	/// file's path, or <c>null</c>/empty to leave <paramref name="connectionString"/>
	/// untouched.
	/// </param>
	public static string? Resolve(string? connectionString, string? passwordFilePath)
	{
		if (string.IsNullOrWhiteSpace(passwordFilePath))
		{
			return connectionString;
		}

		if (string.IsNullOrWhiteSpace(connectionString))
		{
			throw new InvalidOperationException(
				"Database:PasswordFile is configured but ConnectionStrings:Waypoint has no base " +
				"connection string (host/port/database/username) for the file-backed password to " +
				"be applied to.");
		}

		string password = ReadPassword(passwordFilePath);

		NpgsqlConnectionStringBuilder builder = new(connectionString)
		{
			Password = password
		};
		return builder.ConnectionString;
	}

	private static string ReadPassword(string passwordFilePath)
	{
		string raw;
		try
		{
			raw = File.ReadAllText(passwordFilePath);
		}
		catch (Exception exception) when (
			exception is FileNotFoundException or DirectoryNotFoundException)
		{
			throw new InvalidOperationException(
				$"Database:PasswordFile '{passwordFilePath}' does not exist.", exception);
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
		{
			// The path is startup-diagnostic information an operator needs to fix a
			// mount; it is never returned to an API caller (see type doc comment) --
			// nothing reads this message once the host is serving requests.
			throw new InvalidOperationException(
				$"Database:PasswordFile '{passwordFilePath}' could not be read: {exception.Message}", exception);
		}

		// Trim exactly one trailing newline (CRLF or LF), matching how `echo` / a text
		// editor / `docker secret` terminate a single-line file -- not all trailing
		// whitespace, so a password that legitimately ends in a space survives.
		if (raw.EndsWith("\r\n", StringComparison.Ordinal))
		{
			raw = raw[..^2];
		}
		else if (raw.EndsWith('\n'))
		{
			raw = raw[..^1];
		}

		if (raw.Length == 0)
		{
			throw new InvalidOperationException($"Database:PasswordFile '{passwordFilePath}' is empty.");
		}

		return raw;
	}
}
