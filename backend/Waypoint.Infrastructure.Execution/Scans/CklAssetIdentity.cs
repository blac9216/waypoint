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
/// Issue #1068 PR review round 1 finding 2 (argument injection): the guard Waypoint
/// puts on its OWN side of the sibling boundary before an operator-authored target
/// fact reaches the vendored <c>New-CklConvertArgs</c>
/// (<c>runners/compliance-runner/powershell/module.common.ps1</c>), which interpolates
/// each value into a double-quoted segment of the <c>saf convert hdf2ckl</c> argument
/// string with NO escaping:
/// <code>if ($Hostname) { $CklArgs += " --hostname `"$Hostname`"" }</code>
/// A target named <c>evil" -o "/w/pwned.ckl</c> therefore closes the quoted segment and
/// appends a SECOND <c>-o</c> flag, redirecting saf's CKL write to an attacker-chosen
/// path inside the runner container; a bare <c>"</c> breaks the command line outright.
/// This is argument injection, not shell RCE -- <c>Invoke-ExternalCommand</c> runs with
/// <c>UseShellExecute = $false</c>, so no shell metacharacter is interpreted and the
/// exposure is scoped to saf's own option surface.
///
/// The vendored builder is NOT rewritten (it is the sibling repo's file, reused
/// verbatim per AGENTS.md); the fix is that Waypoint never hands it a value that could
/// escape its quoting. <see cref="TryAccept"/> is a REJECT, not a sanitizer: a value
/// that would need escaping is omitted from the command line entirely (the CKL simply
/// carries one fewer asset fact -- a missing fact, never an invented or silently
/// mangled one) and the caller logs a warning naming the FIELD, never the value.
/// <c>Invoke-WaypointConvert</c> applies the same rule again in PowerShell
/// (<c>Get-WaypointSafeCklAssetValue</c>) so a future non-C# caller is covered too.
/// </summary>
public static class CklAssetIdentity
{
	/// <summary>
	/// True when <paramref name="value"/> can be interpolated into the vendored
	/// builder's double-quoted segment without changing the argument string's shape.
	/// Rejected: null/whitespace-only (nothing to stamp); a double quote (closes the
	/// segment -- the injection vector); any control character (embedded newline/NUL
	/// can split or truncate the argument string); and a leading <c>-</c> after
	/// trimming (a value saf would parse as a flag of its own, e.g. <c>-o</c>).
	/// </summary>
	public static bool IsAcceptable(string? value) => TryAccept(value, out _);

	/// <summary>
	/// Reject-or-pass-through. <paramref name="accepted"/> is the value UNCHANGED when
	/// the method returns true (deliberately never a stripped/escaped rewrite: a
	/// mangled asset name in a CKL is worse than an absent one, because it looks
	/// authoritative), and <see cref="string.Empty"/> when it returns false.
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
			if (character == '"' || char.IsControl(character))
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
}
