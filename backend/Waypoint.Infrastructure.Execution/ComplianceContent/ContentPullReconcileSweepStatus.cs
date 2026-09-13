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

namespace Waypoint.Infrastructure.Execution.ComplianceContent;

/// <summary>
/// Issue #1762: the one channel through which <see cref="ContentPullReconcileHostedService"/>
/// tells anything downstream that its sweep loop has stopped after a non-transient
/// (<see cref="SweepOutcome.AuthorizationDenied"/>) failure -- currently read only by
/// <c>Waypoint.ComplianceRunner.Readiness.ComplianceReadinessCheck</c>. Deliberately just
/// a flag rather than a pub/sub mechanism: exactly one hosted service ever writes it and
/// exactly one readiness check ever reads it. Registered as a singleton unconditionally
/// in <c>ExecutionServiceCollectionExtensions.AddWaypointExecution</c> (both runner hosts
/// get one; only compliance-runner's sweep ever sets or reads it) so
/// <c>ComplianceReadinessCheck</c> can take it as a plain required dependency instead of
/// a nullable one whose absence would need its own code path.
/// </summary>
public sealed class ContentPullReconcileSweepStatus
{
	private volatile bool _stopped;

	/// <summary>
	/// True once <see cref="ContentPullReconcileHostedService.ExecuteAsync"/> has stopped
	/// its sweep loop after a <see cref="SweepOutcome.AuthorizationDenied"/> outcome.
	/// </summary>
	public bool Stopped => _stopped;

	/// <summary>
	/// Called once, from <see cref="ContentPullReconcileHostedService.ExecuteAsync"/>,
	/// the moment it stops the sweep loop rather than keep ticking.
	/// </summary>
	public void MarkStopped() => _stopped = true;
}
