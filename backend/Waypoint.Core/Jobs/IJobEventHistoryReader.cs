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
/// Issue #581 (ADR-0019): the bounded, cursor-paged historical counterpart to
/// <see cref="IJobEventFeed"/>'s live/replay stream. Where the SSE feed is an
/// indefinitely open subscription, this is a single bounded read over the same
/// append-only <c>job_events</c> table -- the shape a client uses once a run has
/// ended (or to page through a still-active run's history without holding a stream
/// open). Both read the same rows under the same authorization and redaction
/// contract (redaction already happened at write time -- see
/// <c>JobEventPublisher</c>/<c>BufferedJobEventWriter</c> -- so this reader performs
/// no additional scrubbing, and introduces no new leak surface: every column
/// returned is already what SSE would have sent).
/// </summary>
public interface IJobEventHistoryReader
{
	/// <summary>
	/// Reads one page of <c>job_events</c> rows scoped to a run (optionally narrowed to
	/// one job and/or a closed set of event types/severities), in the same commit-order
	/// <c>seq</c> ordering the SSE feed uses. See <see cref="JobEventHistoryQuery"/> for
	/// the request shape and <see cref="JobEventHistoryPage"/> for the cursor contract.
	/// </summary>
	Task<JobEventHistoryPage> ReadHistoryAsync(JobEventHistoryQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// One bounded history read. <see cref="RunId"/> scopes every read (there is no
/// global historical read -- <see cref="JobEventStreamScope.Global"/> has no
/// historical analogue in this API; that is what the global SSE stream is for).
/// <see cref="JobId"/> further narrows to one job's events when set.
/// <see cref="EventTypes"/> is an optional closed allow-list drawn from
/// <see cref="JobEventTypes"/> (e.g. just <c>job.log</c>); <see cref="Severities"/>
/// is an optional closed allow-list of <c>job.log</c> payload <c>severity</c> values
/// (<c>information</c>/<c>warning</c>/<c>error</c>/<c>verbose</c>/<c>debug</c>) and is
/// only meaningful when <see cref="JobEventTypes.JobLog"/> rows are in scope -- it
/// does not exclude non-job.log rows whose payload has no <c>severity</c> field.
/// </summary>
public sealed record JobEventHistoryQuery(
	Guid RunId,
	Guid? JobId,
	IReadOnlyList<string>? EventTypes,
	IReadOnlyList<string>? Severities,
	long? AfterSeq,
	int Limit);

/// <summary>
/// One page of history. <see cref="Items"/> is in ascending <c>seq</c> order (oldest
/// first, matching SSE replay order). <see cref="NextCursor"/> is non-null exactly
/// when the page was truncated by <see cref="JobEventHistoryQuery.Limit"/> and more
/// matching rows exist -- a null <see cref="NextCursor"/> means the caller has reached
/// the end of the run's currently-persisted history (which may still grow if the run
/// is active), never a silent truncation. Pass <see cref="NextCursor"/> back as the
/// next request's cursor query parameter (see <c>JobEventCursor</c> in
/// <c>Waypoint.Api</c> for its opaque wire encoding) to continue.
/// </summary>
public sealed record JobEventHistoryPage(IReadOnlyList<StreamedJobEvent> Items, long? NextCursor);
