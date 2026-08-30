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
/// Storage for the <c>esx_acquisition_subscriptions</c> table (migration 0117, issue
/// #1470). One implementation
/// (<c>Waypoint.Infrastructure.Downloads.EsxAcquisitionSubscriptionRepository</c>,
/// plain Npgsql -- same "no ORM for this layer" convention as
/// <see cref="IDownloadRepository"/>'s own implementation).
/// </summary>
public interface IEsxAcquisitionSubscriptionRepository
{
	Task<EsxAcquisitionSubscription> CreateAsync(
		string name, IReadOnlyList<string> selectedPlatforms, bool enabled, CancellationToken cancellationToken);

	Task<EsxAcquisitionSubscription?> GetAsync(Guid id, CancellationToken cancellationToken);

	Task<IReadOnlyList<EsxAcquisitionSubscription>> ListAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Partial update: a null parameter leaves the corresponding column unchanged,
	/// matching <see cref="IDownloadRepository.UpdateProgressAsync"/>'s own
	/// leave-unspecified-columns-alone convention. Returns null when
	/// <paramref name="id"/> does not exist. Setting <paramref name="enabled"/> to
	/// false never deletes the row (issue #1470 AC: disabling preserves history).
	/// </summary>
	Task<EsxAcquisitionSubscription?> UpdateAsync(
		Guid id,
		string? name,
		IReadOnlyList<string>? selectedPlatforms,
		bool? enabled,
		CancellationToken cancellationToken);
}
