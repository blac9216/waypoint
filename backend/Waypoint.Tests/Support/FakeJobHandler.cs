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

namespace Waypoint.Tests.Support;

/// <summary>An <see cref="IJobHandler"/> whose behavior is supplied by the test -- the seam #8/#9 will fill with real PowerShell-backed handlers.</summary>
public sealed class FakeJobHandler : IJobHandler
{
	private readonly Func<JobExecutionContext, CancellationToken, Task<JobExecutionOutcome>> _execute;

	public FakeJobHandler(string jobType, Func<JobExecutionContext, CancellationToken, Task<JobExecutionOutcome>> execute)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jobType);
		ArgumentNullException.ThrowIfNull(execute);

		JobType = jobType;
		_execute = execute;
	}

	public string JobType { get; }

	public Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken) =>
		_execute(context, cancellationToken);
}
