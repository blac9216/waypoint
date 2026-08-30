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

namespace Waypoint.Infrastructure.Scans;

/// <summary>
/// Issue #1068 PR review rounds 1-2 (argument injection): the guard Waypoint puts on
/// its OWN side of the sibling boundary before an operator-authored target fact reaches
/// the vendored <c>New-CklConvertArgs</c>
/// (<c>runners/compliance-runner/powershell/module.common.ps1</c>), which interpolates
/// each value into a double-quoted segment of the <c>saf convert hdf2ckl</c> argument
/// string with NO escaping:
/// <code>if ($Hostname) { $CklArgs += " --hostname `"$Hostname`"" }</code>
/// That string is then handed to <c>ProcessStartInfo.Arguments</c>, whose parser is
/// .NET's own -- Windows-style, even on Linux -- so BOTH a double quote and a
/// BACKSLASH change the argv the child sees. Round 1's payload closed the quoted
/// segment with a literal <c>"</c>; round 2's closed it with a trailing <c>\</c>
/// (target name <c>target-a\</c>, host <c>x -o /w/pwned.ckl</c>), which turns the
/// builder's own closing quote into a literal character, keeps the token absorbing,
/// and realigns the quoting so the next field's contents become separate argv tokens
/// -- a SECOND <c>-o</c> reaching saf, redirecting the CKL write to an attacker-chosen
/// path inside the runner container. This is argument injection, not shell RCE
/// (<c>Invoke-ExternalCommand</c> runs with <c>UseShellExecute = $false</c>, so no
/// shell metacharacter is interpreted), but saf's whole option surface is exposed.
///
/// Round 2's lesson is why this is an ALLOW-LIST and not a reject list: neither the
/// vendored builder nor .NET's argument parser is Waypoint's code, and a deny list has
/// to be right about every escaping rule in a parser it does not own. The only rule
/// that survives that is a positive character class narrow enough that the value
/// cannot be re-parsed at all -- see <see cref="TryAccept"/>. The vendored builder is
/// NOT rewritten (sibling repo file, reused verbatim per AGENTS.md) and
/// <c>Invoke-ExternalCommand</c> takes only a <c>[string]$Arguments</c>, so there is no
/// <c>ArgumentList</c> path to switch to without forking the sibling; the fix is that
/// Waypoint never hands it a value that could escape its quoting.
///
/// <see cref="TryAccept"/> is a REJECT, not a sanitizer: a value outside the class is
/// omitted from the command line entirely (the CKL simply carries one fewer asset fact
/// -- a missing fact, never an invented or silently mangled one) and the caller logs a
/// warning naming the FIELD, never the value.
/// <c>Invoke-WaypointConvert</c> applies the identical class in PowerShell
/// (<c>Get-WaypointSafeCklAssetValue</c>) so a future non-C# caller is covered too;
/// the two implementations are pinned to each other by a single shared case table
/// (<c>CklAssetIdentityCaseTable</c>, driven through the real module by
/// <c>WaypointConvertAssetIdentityArgumentTests</c>), not by comments claiming they
/// agree.
/// </summary>
public static class CklAssetIdentity
{
	/// <summary>
	/// True when <paramref name="value"/> can be interpolated into the vendored
	/// builder's double-quoted segment without changing the argv the child process
	/// ultimately receives.
	/// </summary>
	public static bool IsAcceptable(string? value) => TryAccept(value, out _);

	/// <summary>
	/// Reject-or-pass-through against a conservative ALLOW-LIST. Accepted: ASCII
	/// letters and digits, <c>.</c>, <c>_</c>, <c>-</c>, <c>:</c> and the space -- the
	/// full character surface of the four facts Waypoint stamps (a target name, an
	/// FQDN, an IPv4/IPv6 literal, a MAC). Everything else is rejected, including the
	/// backslash and double quote that drive .NET's argument parser, every quoting and
	/// shell metacharacter, every control character (C0, DEL and C1), and all non-ASCII.
	/// A value whose first non-space character is <c>-</c> is rejected too: saf would
	/// parse it as a flag of its own even with the quoting intact.
	/// <paramref name="accepted"/> is the value UNCHANGED when the method returns true
	/// (deliberately never a stripped/escaped rewrite: a mangled asset name in a CKL is
	/// worse than an absent one, because it looks authoritative), and
	/// <see cref="string.Empty"/> when it returns false.
	/// </summary>
	public static bool TryAccept(string? value, out string accepted)
	{
		accepted = string.Empty;

		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}

		foreach (char character in value)
		{
			if (!IsAllowedCharacter(character))
			{
				return false;
			}
		}

		if (value.TrimStart().StartsWith('-'))
		{
			return false;
		}

		accepted = value;
		return true;
	}

	/// <summary>
	/// The allow-list itself, as a single expression so the PowerShell mirror's regex
	/// (<c>^[A-Za-z0-9._: -]+\z</c>) has exactly one thing to agree with.
	/// </summary>
	private static bool IsAllowedCharacter(char character) =>
		character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
			or '.' or '_' or '-' or ':' or ' ';
}
