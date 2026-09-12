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

namespace Waypoint.Core.Subscriptions;

/// <summary>
/// The outcome of one lib.json version-counter GET (issue #1472, ratified amendment
/// #1031/#1032). <see cref="Success"/> is false for any transport/parse failure --
/// <see cref="SubscriptionEvaluationService"/> always fails OPEN on a pre-check error
/// (runs the full diff), never fails closed and stalls the lane, per this issue's own
/// Risks note.
/// </summary>
public sealed record LibraryVersionCounterResult(bool Success, long? VersionCounter, string? Error);

/// <summary>
/// A cheap change-poll against a library-mirror lane's (<see cref="Waypoint.Core.Secrets.RepoStores.ContentLibraries"/>)
/// upstream <c>lib.json</c>: unlike <c>contentVersion</c>, this counter increments on
/// EVERY library change, so an unchanged value proves nothing changed without ever
/// downloading and diffing the full <c>items.json</c>. <see cref="SubscriptionEvaluationService"/>
/// is the only caller; it persists the last-seen value via
/// <see cref="ISubscriptionEvaluationStateRepository"/> so a restart does not force a
/// full diff. The real network implementation
/// (<c>Waypoint.Infrastructure.Subscriptions.HttpLibraryVersionCounterGateway</c>)
/// is exercised in integration tests only through this interface's substitutes -- there
/// is no lab VCSP instance this repository can reach in CI (mirrors
/// <c>IStigManagerProbe</c>'s identical live-instance caveat); a real end-to-end check
/// against a live library mirror is this issue's stated pending-live verification.
/// </summary>
public interface ILibraryVersionCounterGateway
{
	/// <summary>
	/// Fetches the current lib.json version counter for <paramref name="product"/> on
	/// <paramref name="lane"/>. Never throws for an ordinary network/parse failure --
	/// that is reported as <see cref="LibraryVersionCounterResult.Success"/> <c>false</c>
	/// so the caller's fail-open contract has one thing to check.
	/// </summary>
	Task<LibraryVersionCounterResult> GetVersionCounterAsync(string product, string lane, CancellationToken cancellationToken);
}
