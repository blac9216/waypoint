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
	/// Inserts a new view. Throws <see cref="ConsumerViewNameConflictException"/> on a
	/// duplicate <paramref name="name"/> (unique index). When <paramref name="isDefault"/>
	/// is <c>true</c> this MOVES the default onto the new row: the incumbent default is
	/// demoted and the row inserted already-default inside one transaction, so the
	/// exactly-one-default invariant (issue #1464 AC 2) never breaks and this is a
	/// success, not a conflict. <see cref="ConsumerViewDefaultConflictException"/>
	/// remains only as the database's own backstop (partial unique index, 23505) for a
	/// writer that bypasses that transaction.
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
	/// conflict exceptions as <see cref="CreateAsync"/> on a name clash, or
	/// <see cref="ConsumerViewSoleDefaultException"/> when <paramref name="isDefault"/>
	/// is explicitly <c>false</c> and this row currently holds the default (issue #1464
	/// AC "exactly one default at any time"). <paramref name="isDefault"/> explicitly
	/// <c>true</c> MOVES the default to this row -- demote-incumbent-then-promote inside
	/// one transaction, so it succeeds rather than conflicting, and a promotion of an
	/// id that does not exist rolls the demotion back and returns null.
	/// </summary>
	Task<ConsumerView?> UpdateAsync(
		Guid id,
		string? name,
		IReadOnlyList<string>? platforms,
		bool? isDefault,
		CancellationToken cancellationToken);

	/// <summary>
	/// Deletes a view. Returns <c>false</c> when <paramref name="id"/> does not exist.
	/// Throws <see cref="ConsumerViewSoleDefaultException"/> when <paramref name="id"/>
	/// currently holds the default (issue #1464 AC -- move the default to another view
	/// first with <see cref="UpdateAsync"/>, then delete this one; the default is never
	/// deletable outright, because that would leave zero defaults).
	/// </summary>
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
/// Thrown when a write would leave TWO rows with <c>is_default = true</c> -- migration
/// 0131's partial unique index rejecting it with a 23505. This is the database-layer
/// backstop of the exactly-one-default guarantee, not the ordinary promotion path:
/// <see cref="IConsumerViewRepository.CreateAsync"/> and
/// <see cref="IConsumerViewRepository.UpdateAsync"/> MOVE the default atomically
/// (demote-then-promote in one transaction, serialised by an advisory lock), so a
/// normal <c>is_default: true</c> write succeeds. Reaching this exception means a
/// writer that never took that lock raced a second default in.
/// </summary>
public sealed class ConsumerViewDefaultConflictException : Exception
{
	public ConsumerViewDefaultConflictException()
		: base("Another consumer view was concurrently marked as the default. Retry the request.")
	{
	}
}

/// <summary>
/// Thrown by <see cref="IConsumerViewRepository.DeleteAsync"/> and
/// <see cref="IConsumerViewRepository.UpdateAsync"/> when the requested write would
/// leave zero consumer views with <c>is_default = true</c> -- deleting the row that
/// currently holds the default, or explicitly clearing <c>is_default</c> on it (issue
/// #1464 AC "exactly one default at any time"; #1464's Proposed Changes: model
/// <c>IsDefault</c> as a boolean singleton, "not a special-cased absence"). The
/// default can only be MOVED, never removed: mark a different row as the new default
/// (a single <c>is_default: true</c> write, which
/// <see cref="IConsumerViewRepository.UpdateAsync"/> performs as an atomic transfer),
/// then the row that used to hold it is an ordinary row and can be cleared or deleted.
/// </summary>
public sealed class ConsumerViewSoleDefaultException : Exception
{
	public ConsumerViewSoleDefaultException(string message)
		: base(message)
	{
	}
}
