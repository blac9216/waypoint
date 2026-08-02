namespace Waypoint.Core.Pagination;

/// <summary>
/// A page of results plus the total count backing <c>X-Total-Count</c>
/// (<c>docs/api-contract.md</c> Conventions: "Pagination: `?limit/offset` +
/// `X-Total-Count` on list endpoints").
/// </summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

/// <summary>Query parameters accepted by every list endpoint.</summary>
public sealed class PageRequest
{
	private const int DefaultLimit = 50;
	private const int MaxLimit = 200;

	private int _limit = DefaultLimit;
	private int _offset;

	public int Limit
	{
		get => _limit;
		set => _limit = Math.Clamp(value, 1, MaxLimit);
	}

	public int Offset
	{
		get => _offset;
		set => _offset = Math.Max(0, value);
	}
}
