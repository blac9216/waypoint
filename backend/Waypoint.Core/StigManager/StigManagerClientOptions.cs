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

namespace Waypoint.Core.StigManager;

/// <summary>
/// Tuning for the STIG Manager network boundary (issue #320, found during #318's review
/// of #311). <see cref="HttpClient"/> instances created through
/// <c>IHttpClientFactory</c> inherit a 100 s default <c>Timeout</c>; that bound was
/// judged acceptable in #318 (a hang still degrades to <c>upload_status=failed</c>, it
/// just takes up to 100 s to do so -- see <c>ScanUploadCoordinator</c>'s "never fails
/// the scan run" contract), but a slow/hanging STIG Manager instance holds the convert
/// stage's terminal transition for that entire window. This option gives each upload /
/// benchmark-metadata call its own explicit, shorter budget.
/// </summary>
public sealed class StigManagerClientOptions
{
	public const string SectionName = "StigManager";

	/// <summary>
	/// Per-call budget for the CKL upload POST and the <c>/stigs</c> metadata GET
	/// (each call gets its own fresh budget, not a shared one across both). 45 s:
	/// comfortably inside the inherited 100 s <c>HttpClient.Timeout</c> default while
	/// still generous for a multipart CKL upload over a slow link -- tighter than the
	/// default per issue #320, not so tight it trips on ordinary latency.
	/// </summary>
	public TimeSpan UploadTimeout { get; set; } = TimeSpan.FromSeconds(45);
}
