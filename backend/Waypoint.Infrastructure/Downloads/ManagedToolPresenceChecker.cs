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

using Microsoft.Extensions.Options;
using Waypoint.Core.Downloads;

namespace Waypoint.Infrastructure.Downloads;

/// <inheritdoc cref="IManagedToolPresenceChecker"/>
/// <remarks>
/// A plain file existence check against <see cref="ManagedToolOptions.ToolStatePath"/>
/// (issue #441). No version/signature validation here -- that belongs to the future
/// install flow (ADR-0015) that places the file; this checker only answers "is
/// anything installed at all", which is what distinguishes a clear tool-absent job
/// failure from an ordinary invocation failure.
/// </remarks>
public sealed class ManagedToolPresenceChecker : IManagedToolPresenceChecker
{
	private readonly IOptions<ManagedToolOptions> _options;

	public ManagedToolPresenceChecker(IOptions<ManagedToolOptions> options)
	{
		ArgumentNullException.ThrowIfNull(options);
		_options = options;
	}

	public bool IsPresent() => File.Exists(ExecutablePath());

	public string DescribeExpectedLocation() => ExecutablePath();

	private string ExecutablePath()
	{
		ManagedToolOptions options = _options.Value;
		return Path.Combine(options.ToolStatePath, options.ExecutableName);
	}
}
