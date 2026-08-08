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
/// The exact string values of <c>jobs.upload_status</c>, matching
/// <c>jobs_upload_status_check</c> in migration 0018 -- issue #311's per-target STIG
/// Manager upload outcome, independent of <c>jobs.state</c>/<c>stage</c> (see that
/// migration's comment for why). A NULL column value ("never attempted") has no
/// constant here; callers treat it as absent, not as one of these four.
/// </summary>
public static class JobUploadStatuses
{
	/// <summary>An upload attempt is in flight or has not yet resolved -- set immediately before the HTTP call so a crash mid-upload leaves an honest status rather than stale NULL.</summary>
	public const string Pending = "pending";

	public const string Uploaded = "uploaded";

	public const string Failed = "failed";

	/// <summary>HTTP 409 -- STIG Manager already has an asset/checklist for this benchmark+hostname.</summary>
	public const string Conflict = "conflict";
}
