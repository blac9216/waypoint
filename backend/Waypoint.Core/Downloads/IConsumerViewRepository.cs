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
/// Storage for the <c>consumer_views</c> table (migration 0131, issue #1464). One
/// implementation (<c>Waypoint.Infrastructure.Downloads.ConsumerViewRepository</c>,
/// plain Npgsql -- same "no ORM for this layer" convention as
/// <see cref="IEsxAcquisitionSubscriptionRepository"/>'s own implementation).
/// </summary>
public interface IConsumerViewRepository
{
	/// <summary>
	/// Inserts a new view. Throws <see cref="ConsumerViewNameConflictException"/> on
	/// a duplicate <paramref name="name"/> (unique index) or
	/// <see cref="ConsumerViewDefaultConflictException"/> when <paramref name="isDefault"/>
	/// is <c>true</c> and a different row is already the default (partial unique
	/// index) -- the database half of the exactly-one-default guarantee; the API
	/// checks first too (belt and suspenders, issue #1464 AC).
	/// </summary>
	Task<ConsumerView> CreateAsync(
		string name, IReadOnlyList<string> platforms, bool isDefault, CancellationToken cancellationToken);

	Task<ConsumerView?> GetAsync(Guid id, CancellationToken cancellationToken);

	/// <summary>Every view, ordered newest-first then by id -- same ordering convention as <see cref="IEsxAcquisitionSubscriptionRepository.ListAsync"/>.</summary>
	Task<IReadOnlyList<ConsumerView>> ListAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Partial update: a null parameter leaves the corresponding column unchanged
	/// (same convention as <see cref="IEsxAcquisitionSubscriptionRepository.UpdateAsync"/>).
	/// Returns null when <paramref name="id"/> does not exist. Throws the same
	/// conflict exceptions as <see cref="CreateAsync"/> on a name or default clash.
	/// </summary>
	Task<ConsumerView?> UpdateAsync(
		Guid id,
		string? name,
		IReadOnlyList<string>? platforms,
		bool? isDefault,
		CancellationToken cancellationToken);

	/// <summary>Deletes a view. Returns <c>false</c> when <paramref name="id"/> does not exist.</summary>
	Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Thrown by <see cref="IConsumerViewRepository"/> writes on a duplicate <c>name</c> (unique index violation).</summary>
public sealed class ConsumerViewNameConflictException : Exception
{
	public ConsumerViewNameConflictException(string name)
		: base($"A consumer view named '{name}' already exists.")
	{
	}
}

/// <summary>
/// Thrown by <see cref="IConsumerViewRepository"/> writes when <c>is_default = true</c>
/// is requested but a different row already holds the default (partial unique index
/// violation) -- the database-layer half of the exactly-one-default guarantee.
/// </summary>
public sealed class ConsumerViewDefaultConflictException : Exception
{
	public ConsumerViewDefaultConflictException()
		: base("Another consumer view is already marked as the default.")
	{
	}
}
