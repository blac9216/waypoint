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

namespace Waypoint.Core.Downloads;

/// <summary>Configuration for the <c>download</c> job handler (issue #10, M1 vertical slice).</summary>
public sealed class DownloadOptions
{
	public const string SectionName = "Downloads";

	/// <summary>
	/// Root directory of the artifact store volume downloaded files land in — matches
	/// <c>deploy/compose.yaml</c>'s existing <c>artifacts</c> volume mount
	/// (<c>/var/lib/waypoint/artifacts</c>) in production; tests point this at a temp
	/// directory. A <c>quarantine</c> subdirectory holds files that failed sha256
	/// verification (see <see cref="Waypoint.Infrastructure.Downloads.DownloadJobHandler"/>).
	/// </summary>
	public string ArtifactStorePath { get; set; } = "/var/lib/waypoint/artifacts";
}
