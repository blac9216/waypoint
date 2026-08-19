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

using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;

namespace Waypoint.Infrastructure.Downloads;

/// <summary>
/// Wraps <see cref="DownloadJobHandler"/> with the ADR-0015 tool-presence gate (issue
/// #441's acceptance criterion: "Download clearly reports tool absence and succeeds
/// when an operator-installed tool is present"). Composition, not a handler rewrite --
/// <see cref="DownloadJobHandler"/>'s own progress/checksum/retry/disk-accounting
/// behavior is untouched; this type only decides whether it runs at all.
///
/// Deliberately checked here rather than inside <see cref="DownloadJobHandler"/>
/// itself: M1's <c>Invoke-WaypointDownload</c> calls the sibling repo's <c>Save-WebFile</c>
/// directly and does not (yet) shell out to <c>vcf-download-tool</c> at all, so
/// today's actual transfer path does not strictly require the tool -- but ADR-0015
/// models the tool as required managed state for the download domain going forward,
/// and this issue's acceptance criteria are explicit that a missing tool must produce
/// a clear, distinct failure. Gating at the handler-composition boundary keeps that
/// policy decision out of the transfer/verification logic and easy to remove or relax
/// later without touching it.
///
/// <c>catalog-index</c> is never wrapped this way (domain-model.md: indexing is a
/// pure filesystem walk that does not need the tool) -- registering only this gated
/// download handler alongside the ungated <see cref="Catalog.CatalogIndexJobHandler"/>
/// is what keeps catalog browse/index functional with the tool absent.
/// </summary>
public sealed class ToolGatedDownloadJobHandler : IJobHandler
{
	private readonly IJobHandler _inner;
	private readonly IManagedToolPresenceChecker _toolPresence;

	/// <summary>
	/// <paramref name="inner"/> is typed as <see cref="IJobHandler"/>, not the concrete
	/// <see cref="DownloadJobHandler"/>, purely for testability (a plain
	/// <c>FakeJobHandler</c> can stand in without constructing the real handler's full
	/// Postgres/PowerShell dependency graph); production DI always wires the real
	/// <see cref="DownloadJobHandler"/> here (see <c>ServiceCollectionExtensions</c>).
	/// </summary>
	public ToolGatedDownloadJobHandler(DownloadJobHandler inner, IManagedToolPresenceChecker toolPresence)
		: this((IJobHandler)inner, toolPresence)
	{
	}

	internal ToolGatedDownloadJobHandler(IJobHandler inner, IManagedToolPresenceChecker toolPresence)
	{
		ArgumentNullException.ThrowIfNull(inner);
		ArgumentNullException.ThrowIfNull(toolPresence);
		_inner = inner;
		_toolPresence = toolPresence;
	}

	public string JobType => "download";

	public Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		if (!_toolPresence.IsPresent())
		{
			return Task.FromResult(JobExecutionOutcome.Failed(
				"vcf-download-tool is not installed on this appliance. An operator must install it " +
				$"(expected at '{_toolPresence.DescribeExpectedLocation()}') before download jobs can run; " +
				"catalog browsing and indexing remain available without it."));
		}

		return _inner.ExecuteAsync(context, cancellationToken);
	}
}
