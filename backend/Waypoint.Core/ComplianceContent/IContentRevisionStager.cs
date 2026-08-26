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

namespace Waypoint.Core.ComplianceContent;

/// <summary>
/// Snapshots a content-pull working tree into an immutable, digest-addressed revision
/// directory and records the resulting <see cref="ContentRevision"/> (issue #731). See
/// <c>Waypoint.Infrastructure.Execution.ComplianceContent.ContentRevisionStager</c> for
/// the real filesystem-backed implementation; this seam exists so
/// <c>ContentPullJobHandler</c> unit tests can fake staging without touching a real
/// filesystem path.
/// </summary>
public interface IContentRevisionStager
{
	Task<ContentRevision> StageAsync(string contentPath, string sourceCommit, string contentDigest, CancellationToken cancellationToken);
}
