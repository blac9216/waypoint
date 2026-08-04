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

using Waypoint.Core.Jobs;

namespace Waypoint.Infrastructure.Jobs;

/// <summary>
/// Resolves the <see cref="IJobHandler"/> registered for a claimed job's
/// <c>job_type</c>. This issue (#5) registers none -- #8/#9 add the first real
/// handlers by registering <c>IJobHandler</c> in DI; the dispatcher asks this registry
/// for one on every claim and fails the job cleanly (see
/// <c>JobDispatcherHostedService</c>) when none is found, rather than hanging.
/// </summary>
public sealed class JobHandlerRegistry
{
	private readonly Dictionary<string, IJobHandler> _handlersByJobType;

	public JobHandlerRegistry(IEnumerable<IJobHandler> handlers)
	{
		ArgumentNullException.ThrowIfNull(handlers);

		Dictionary<string, IJobHandler> map = new(StringComparer.Ordinal);
		foreach (IJobHandler handler in handlers)
		{
			map[handler.JobType] = handler;
		}

		_handlersByJobType = map;
	}

	public bool TryResolve(string jobType, out IJobHandler? handler) => _handlersByJobType.TryGetValue(jobType, out handler);
}
