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

namespace Waypoint.Core.Pagination;

/// <summary>
/// Query parameters accepted by every list endpoint. There is deliberately no paired
/// "page of results" wrapper type: per <c>docs/api-contract.md</c> Conventions
/// ("Pagination: `?limit/offset` + `X-Total-Count` on list endpoints") and
/// <c>backend/README.md</c>, each collection endpoint returns a bare array and sets the
/// <c>X-Total-Count</c> response header itself (see
/// <c>CatalogController.ListArtifacts</c>) — there is no generic envelope because every
/// resource's collection endpoint composes its own query.
/// </summary>
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
