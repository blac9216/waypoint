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

using System.Diagnostics;

namespace Waypoint.Tests.Support;

/// <summary>
/// Shared root/OS precondition check for tests that depend on constructing an
/// unreadable-or-unwritable file/directory (a mode denying every other user) --
/// impossible under an effective-root process, which reads/writes through any mode.
/// Issue #1830: previously copied per-file (three near-identical <c>IsRoot()</c>
/// shell-outs); consolidated here so every call site shares one implementation.
/// </summary>
public static class RootPrecondition
{
	/// <summary>
	/// True when running as effective root (<c>id -u</c> == 0, shelled out as the
	/// simplest portable check on Linux/macOS): root reads/writes through any mode, so
	/// an unreadable/unwritable-file precondition cannot be constructed. Callers on
	/// Windows must guard separately with <see cref="OperatingSystem.IsWindows"/> --
	/// kept as a distinct, inline check at each call site so the CA1416 platform-compat
	/// analyzer recognizes it as a guard clause.
	/// </summary>
	public static bool IsRoot()
	{
		using Process process = Process.Start(new ProcessStartInfo("id", "-u")
		{
			RedirectStandardOutput = true,
			UseShellExecute = false,
		})!;
		string output = process.StandardOutput.ReadToEnd().Trim();
		process.WaitForExit();
		return output == "0";
	}
}
