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

using System.Management.Automation;

namespace Waypoint.Infrastructure.PowerShell;

/// <summary>
/// Issue #972: the single unwrap chokepoint for reading a NoteProperty value off a
/// <see cref="PSObject"/> that came out of the in-process SMA pipeline, whether that
/// property sits on a top-level output object or on a row nested inside it (e.g. one
/// <c>ContentEntries[]</c> element).
///
/// SMA wraps a CmdletProvider/cmdlet's own OWN output values -- even primitives like
/// the <see cref="string"/> <c>Get-Content -Raw</c> returns -- in a <see cref="PSObject"/>
/// as they cross out of that cmdlet's pipeline. Assigning that wrapped value straight
/// into a <c>[PSCustomObject]@{ Key = Get-Content ... -Raw }</c> literal does not strip
/// the wrapper: the hashtable literal stores whatever object reference it was handed,
/// and for cmdlet output that reference is the <see cref="PSObject"/>, not its
/// <see cref="PSObject.BaseObject"/>. <see cref="PowerShellExecutor.Unwrap"/> only ever
/// ran on each TOP-LEVET pipeline output object, so this one extra wrapper layer on a
/// NESTED property survived every read of <c>psObject.Properties["X"]?.Value as string</c>
/// as a silent <c>null</c> -- proven live by issue #972 (315 recognized-layout
/// <c>inspec.yml</c> profiles rejected "empty or missing" even though
/// <c>RawYaml</c> was populated correctly by the runner's own PowerShell walk).
///
/// A user-authored <c>[pscustomobject]@{ Key = "literal" }</c> does NOT hit this --
/// literal values already unwrap to their own type -- which is exactly why the
/// pre-#972 tests (constructing <see cref="PSNoteProperty"/> values directly in C#,
/// or a PowerShell literal string) never caught it: they never round-tripped a real
/// cmdlet's own wrapped output through a nested property (fixture-monoculture,
/// docs/testing.md).
/// </summary>
public static class PowerShellValueUnwrap
{
	/// <summary>
	/// Strips one layer of <see cref="PSObject"/> wrapping from a NoteProperty value,
	/// unless the wrapped object is itself a <see cref="PSCustomObject"/> sentinel --
	/// that sentinel carries no CLR-visible data of its own (see
	/// <see cref="PowerShellExecutor.Unwrap"/>'s doc comment), so its properties must
	/// stay reachable through the outer <see cref="PSObject"/> property bag rather than
	/// being unwrapped away. Anything that is not a <see cref="PSObject"/> (already a
	/// plain CLR value, e.g. a hand-authored fixture in a C# test) passes through
	/// unchanged -- this method is idempotent and safe to call on a value that was
	/// never wrapped in the first place.
	/// </summary>
	public static object? Unwrap(object? value)
	{
		if (value is not PSObject psObject)
		{
			return value;
		}

		return psObject.BaseObject is PSCustomObject ? psObject : psObject.BaseObject;
	}

	/// <summary>
	/// <see cref="Unwrap(object?)"/> plus an <c>as</c>-style cast, for the common
	/// "read one property as a concrete CLR type" call site
	/// (<c>psObject.Properties["Name"]?.Value</c>). Returns <c>null</c> both when the
	/// property is absent and when the unwrapped value is not a <typeparamref name="T"/>
	/// -- the same tolerance every existing <c>as string</c>/<c>as T</c> call site in
	/// this codebase already assumes (a malformed/missing field degrades the row, it
	/// never throws).
	/// </summary>
	public static T? UnwrapAs<T>(object? value)
		where T : class =>
		Unwrap(value) as T;

	/// <summary>
	/// Recursively unwraps every element of an <see cref="System.Collections.IEnumerable"/>
	/// property value (e.g. <c>ContentEntries</c>/<c>ControlFileNames</c>) -- each
	/// element gets the same one-layer strip <see cref="Unwrap(object?)"/> applies to a
	/// scalar property, since SMA wraps array elements exactly like it wraps scalar
	/// cmdlet output. Non-enumerable input yields an empty sequence rather than
	/// throwing, matching every existing "malformed shape means empty/dropped, not a
	/// fatal error" convention in the job handlers that consume this.
	/// </summary>
	public static IEnumerable<object?> UnwrapEach(object? value)
	{
		if (Unwrap(value) is not System.Collections.IEnumerable enumerable)
		{
			yield break;
		}

		foreach (object? item in enumerable)
		{
			yield return Unwrap(item);
		}
	}
}
