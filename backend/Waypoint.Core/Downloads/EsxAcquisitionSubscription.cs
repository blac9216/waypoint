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

/// <summary>
/// One row of the <c>esx_acquisition_subscriptions</c> table (migration 0117, issue
/// #1470, epic #1181 -- split B of design record #1159): a named preset selecting
/// which ESX platform keys (from the <c>lcm.esx.supported.host.platforms</c> vendor
/// vocabulary, see <see cref="IEsxPlatformVocabularyReader"/>) an operator wants
/// acquisition to cover. Model/API only -- this slice does not run a sync (#1484) or
/// invoke the tool wrapper (#1459).
/// </summary>
public sealed record EsxAcquisitionSubscription(
	Guid Id,
	string Name,
	IReadOnlyList<string> SelectedPlatforms,
	bool Enabled,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);
