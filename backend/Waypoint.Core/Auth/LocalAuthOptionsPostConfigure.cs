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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Waypoint.Core.Configuration;

namespace Waypoint.Core.Auth;

/// <summary>
/// Resolves <see cref="LocalAuthOptions.AdminPasswordHash"/> from a mounted file when
/// one is configured (issue #333), falling back to whatever value bound directly from
/// configuration/the <c>LocalAuth__AdminPasswordHash</c> environment variable (issue
/// #62) when it isn't. This runs as an <see cref="IPostConfigureOptions{TOptions}"/>
/// step rather than inline at the read site, so every consumer of
/// <c>IOptions&lt;LocalAuthOptions&gt;</c> — today just
/// <see cref="ILocalAuthenticationService"/> — sees one already-resolved
/// <see cref="LocalAuthOptions.AdminPasswordHash"/> and never needs to know there are
/// two possible sources.
///
/// <para>
/// Precedence: file wins when <see cref="LocalAuthOptions.AdminPasswordHashFile"/> is
/// set AND the file exists and is non-empty after trimming. Anything else — file unset,
/// path set but missing, or empty — falls back to the env-sourced
/// <see cref="LocalAuthOptions.AdminPasswordHash"/> as already bound. A missing file at
/// a configured path is treated as "fall back", not "fail startup": the alternative
/// (throwing) would take down local auth entirely from a typo'd path in an otherwise
/// working env-var deployment, which is a worse failure mode than the one this issue is
/// closing. <see cref="Auth.ILocalAuthenticationService"/> already fails every login
/// closed when the resolved hash ends up null (see its doc comment) — that is the
/// correct failure surface, not a thrown exception here.
/// </para>
/// <para>
/// Env-var delivery is deprecated, not removed (be conservative — existing deployments
/// must keep working): when the file isn't in play and the env-sourced value is the one
/// actually used, this logs a one-time WARN naming the recommended replacement.
/// </para>
/// </summary>
public sealed partial class LocalAuthOptionsPostConfigure : IPostConfigureOptions<LocalAuthOptions>
{
	private readonly ILogger<LocalAuthOptionsPostConfigure> _logger;

	public LocalAuthOptionsPostConfigure(ILogger<LocalAuthOptionsPostConfigure> logger)
	{
		_logger = logger;
	}

	public void PostConfigure(string? name, LocalAuthOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		string? fromFile = TryReadFile(options.AdminPasswordHashFile);
		if (fromFile is not null)
		{
			options.AdminPasswordHash = fromFile;
			return;
		}

		if (!string.IsNullOrEmpty(options.AdminPasswordHash))
		{
			LogEnvVarDeprecated();
		}
	}

	private static string? TryReadFile(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}

		string content;
		try
		{
			content = File.ReadAllText(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Fall back to the env var rather than failing startup — see the type's doc
			// comment for why a bad file path degrades instead of throwing.
			return null;
		}

		string trimmed = content.TrimEnd('\r', '\n');
		return trimmed.Length == 0 ? null : trimmed;
	}

	[LoggerMessage(
		Level = LogLevel.Warning,
		Message = "LocalAuth:AdminPasswordHash is set via environment variable/inline configuration. " +
			"This works but is deprecated: env vars leak via /proc/<pid>/environ, 'docker inspect', " +
			"and crash dumps. Prefer LocalAuth:AdminPasswordHashFile (env LocalAuth__AdminPasswordHashFile) " +
			"pointing at an operator-mounted file containing the hash. See deploy/compose.override.example.yaml.")]
	private partial void LogEnvVarDeprecated();
}
