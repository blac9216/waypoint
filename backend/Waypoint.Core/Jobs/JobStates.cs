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

namespace Waypoint.Core.Jobs;

/// <summary>
/// The exact string values of <c>jobs.state</c>, matching <c>jobs_state_check</c> in
/// <c>0001_initial_schema.sql</c> verbatim -- this is the closed set; nothing here may
/// diverge from that CHECK constraint or every write becomes a runtime constraint
/// violation instead of a compile-time typo.
/// </summary>
public static class JobStates
{
	public const string Queued = "queued";
	public const string Running = "running";
	public const string Attesting = "attesting";
	public const string Converting = "converting";
	public const string Uploaded = "uploaded";
	public const string Done = "done";
	public const string Failed = "failed";
	public const string AuthFailed = "auth-failed";
	public const string Blocked = "blocked";
	public const string Cancelled = "cancelled";
}
