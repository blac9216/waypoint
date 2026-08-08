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

namespace Waypoint.Infrastructure.Scans;

/// <summary>
/// The <c>scan</c> job type's handler (issue #273, first slice of the #23 split).
/// Registering this now -- rather than leaving <c>scan</c> with no handler at all --
/// means a fanned-out scan job fails cleanly and immediately (<see cref="JobDispatcherHostedService"/>'s
/// existing "no handler for job type" path is for a genuinely *unregistered* type; a
/// registered handler that always reports <see cref="JobExecutionOutcome.Failed"/> is
/// the correct way to say "the control plane for this job type exists, execution does
/// not yet" without a special-case in the dispatcher). InSpec execution is #274 --
/// this handler is replaced (not extended) when that lands.
/// </summary>
public sealed class ScanJobHandler : IJobHandler
{
	public string JobType => "scan";

	public Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		_ = cancellationToken;

		return Task.FromResult(JobExecutionOutcome.Failed(
			"scan execution is not implemented yet -- run creation and fan-out landed in #273, InSpec execution lands in #274."));
	}
}
