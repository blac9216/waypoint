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

using Waypoint.Core.Auth;

namespace Waypoint.Api.Diagnostics;

/// <summary>
/// The operator-facing generator for <c>LocalAuth__AdminPasswordHash</c>
/// (<see cref="Waypoint.Core.Configuration.LocalAuthOptions.AdminPasswordHash"/>).
///
/// <para>
/// Follows the same in-app-CLI-verb convention as <see cref="HealthCheckProbe"/>
/// (<c>backend/README.md</c> "Container health"): the same binary that serves the API
/// also answers <c>--hash-password</c>, so there is nothing extra to build, ship, or
/// keep in sync with the hashing implementation. Before issue #62, an operator ran
/// <c>printf '&lt;password&gt;' | sha256sum</c> — that only worked because the old
/// format was an unsalted, parameterless digest. <see cref="Pbkdf2PasswordHasher"/>'s
/// stored format carries a random salt and an iteration count, so it cannot be
/// reproduced with a shell one-liner; this verb replaces that instruction.
/// </para>
/// </summary>
public static class PasswordHashCli
{
	/// <summary>The command-line argument that switches the entry point into hash-generator mode.</summary>
	public const string Argument = "--hash-password";

	/// <summary>True when the process was started to generate a password hash rather than to serve traffic.</summary>
	/// <param name="args">The entry point's raw arguments.</param>
	public static bool IsHashPasswordInvocation(string[] args)
	{
		return args.Any(argument => string.Equals(argument, Argument, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Prompts for a password on the console (input not echoed) and prints the
	/// self-describing PBKDF2 stored hash to stdout for the operator to paste into
	/// <c>LocalAuth__AdminPasswordHash</c> / <c>WAYPOINT_ADMIN_PASSWORD_HASH</c>. Never
	/// accepts the password as an argument — that would land it in <c>argv</c>, readable
	/// via <c>/proc/&lt;pid&gt;/cmdline</c> (docs/security.md control 2).
	/// </summary>
	public static int Run()
	{
		string? password = ReadPasswordFromConsole();
		if (string.IsNullOrEmpty(password))
		{
			Console.Error.WriteLine("hash-password: no password entered.");
			return 1;
		}

		Console.WriteLine(Pbkdf2PasswordHasher.Hash(password));
		return 0;
	}

	private static string? ReadPasswordFromConsole()
	{
		Console.Error.Write("Password to hash (input hidden): ");

		if (Console.IsInputRedirected)
		{
			// Piped stdin (e.g. CI, scripting): no keystrokes to mask, just read the line.
			return Console.ReadLine();
		}

		System.Text.StringBuilder builder = new();
		while (true)
		{
			ConsoleKeyInfo key = Console.ReadKey(intercept: true);
			if (key.Key == ConsoleKey.Enter)
			{
				Console.Error.WriteLine();
				break;
			}

			if (key.Key == ConsoleKey.Backspace)
			{
				if (builder.Length > 0)
				{
					builder.Length--;
				}

				continue;
			}

			if (!char.IsControl(key.KeyChar))
			{
				builder.Append(key.KeyChar);
			}
		}

		return builder.ToString();
	}
}
