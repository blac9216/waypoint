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

namespace Waypoint.Core.PowerShell;

/// <summary>
/// The ADR-0006 execution seam: runs one PowerShell command or script in an
/// in-process runspace and reports the outcome. Implementations capture every
/// PowerShell output stream as <c>job.log</c> events (through the redacting,
/// batched <see cref="Jobs.IJobLogBuffer"/> path) and contain a hung or crashing
/// pipeline without taking the pool down. Job handlers (epic #6 slice 3) are the
/// intended callers; nothing in this contract is HTTP- or queue-aware.
/// </summary>
public interface IPowerShellExecutor
{
	Task<PowerShellExecutionResult> ExecuteAsync(PowerShellRequest request, CancellationToken cancellationToken);
}

/// <summary>How <see cref="PowerShellRequest.Command"/> is interpreted.</summary>
public enum PowerShellRequestKind
{
	/// <summary>A single command/function name invoked with bound parameters -- the normal, injection-safe path.</summary>
	Command,

	/// <summary>A script block body. Parameters still bind as named variables -- secrets are NEVER interpolated into the text (security.md control 1/2).</summary>
	Script
}

/// <summary>
/// One unit of PowerShell work. <paramref name="Parameters"/> values are passed as
/// typed parameter bindings, never spliced into command text -- the executor has no
/// code path that concatenates a parameter value into a script string.
/// <paramref name="JobId"/>/<paramref name="RunId"/> scope the captured stream
/// events; a null <paramref name="Timeout"/> uses the configured default.
/// </summary>
public sealed record PowerShellRequest(
	string Command,
	PowerShellRequestKind Kind = PowerShellRequestKind.Command,
	IReadOnlyDictionary<string, object?>? Parameters = null,
	Guid? JobId = null,
	Guid? RunId = null,
	TimeSpan? Timeout = null);

/// <summary>
/// The outcome of one invocation. <paramref name="Succeeded"/> is false only for a
/// terminating error, a timeout, or a non-zero native exit code -- non-terminating
/// errors keep the PowerShell convention (the pipeline ran to completion; they are
/// surfaced on the error stream and via <paramref name="HadErrors"/>).
/// <paramref name="Output"/> holds the pipeline's returned objects unwrapped to
/// their base .NET objects. <paramref name="ErrorLines"/> is the non-terminating
/// error stream's own messages, in arrival order -- the same text already forwarded
/// into <c>job.log</c> as "error"-severity events (issue #921: a wrapper module's own
/// returned failure summary can name a downstream symptom, e.g. a missing report
/// file, while the underlying cause -- a transport connection failure -- only ever
/// reached the error stream; callers that want the more specific diagnostic read it
/// from here rather than re-deriving it from job.log).
/// </summary>
public sealed record PowerShellExecutionResult(
	bool Succeeded,
	IReadOnlyList<object?> Output,
	bool HadErrors,
	bool TimedOut,
	string? FailureReason,
	int? NativeExitCode,
	IReadOnlyList<string>? ErrorLines = null);
