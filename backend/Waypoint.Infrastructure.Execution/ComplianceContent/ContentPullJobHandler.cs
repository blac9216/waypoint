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

using System.Text.Json;
using Microsoft.Extensions.Options;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.Jobs;
using Waypoint.Core.PowerShell;

namespace Waypoint.Infrastructure.Execution.ComplianceContent;

/// <summary>
/// The <c>content-pull</c> <see cref="JobShape.Simple"/> job handler (issue #40, ADR-0017:
/// compliance-runner executes content-pull/content-import). Reads the singleton
/// <c>compliance_content</c> config (repository/ref), clones or fetches the working
/// tree at <see cref="ComplianceContentOptions.ContentPath"/>, checks out the
/// configured ref, and replaces the <c>profiles</c> inventory with what it finds --
/// mirroring <see cref="Waypoint.Infrastructure.Catalog.CatalogIndexJobHandler"/>'s
/// "not payload-driven; everything needed is configuration or resolved at execution
/// time" shape. No credential is threaded through this slice (see the PowerShell
/// module's doc comment) -- a private/token-gated content source is out of scope.
///
/// Every attempt -- success or failure -- is recorded via
/// <see cref="IComplianceContentRepository.RecordPullAsync"/> so pull history
/// (issue #40 AC "who/when/commit") always reflects what actually happened, including
/// a failed run, rather than only successes.
/// </summary>
public sealed class ContentPullJobHandler : IJobHandler
{
	private const string InvocationCommand = "Invoke-WaypointComplianceContentPull";

	private readonly IPowerShellExecutor _executor;
	private readonly IComplianceContentRepository _content;
	private readonly IProfileRepository _profiles;
	private readonly IJobRunnerRepository _jobs;
	private readonly IOptions<ComplianceContentOptions> _options;

	public ContentPullJobHandler(
		IPowerShellExecutor executor,
		IComplianceContentRepository content,
		IProfileRepository profiles,
		IJobRunnerRepository jobs,
		IOptions<ComplianceContentOptions> options)
	{
		ArgumentNullException.ThrowIfNull(executor);
		ArgumentNullException.ThrowIfNull(content);
		ArgumentNullException.ThrowIfNull(profiles);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(options);

		_executor = executor;
		_content = content;
		_profiles = profiles;
		_jobs = jobs;
		_options = options;
	}

	public string JobType => "content-pull";

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		ComplianceContentConfig? config = await _content.GetConfigAsync(cancellationToken).ConfigureAwait(false);
		if (config is null)
		{
			return JobExecutionOutcome.Failed("No compliance-content repository is configured (PUT /compliance-content first).");
		}

		string actor = await ResolveActorAsync(context.Job.RunId, cancellationToken).ConfigureAwait(false);

		Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
		{
			["RepositoryUrl"] = config.RepositoryUrl,
			["RefType"] = config.RefType,
			["RefValue"] = config.RefValue,
			["ContentPath"] = _options.Value.ContentPath,
		};

		PowerShellRequest request = new(InvocationCommand, PowerShellRequestKind.Command, parameters, context.Job.Id, context.Job.RunId);
		PowerShellExecutionResult result = await _executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

		if (!result.Succeeded)
		{
			string note = result.FailureReason ?? "content-pull invocation failed with no failure reason.";
			await _content.RecordPullAsync(
				context.Job.Id, config.RefType, config.RefValue, commit: null,
				ComplianceContentPullStatuses.Failed, note, actor, cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.Failed(note);
		}

		(string? commit, IReadOnlyList<ProfileUpsert> discoveredProfiles) = ParseOutput(result.Output, config);
		if (commit is null)
		{
			const string note = "content-pull invocation returned no commit.";
			await _content.RecordPullAsync(
				context.Job.Id, config.RefType, config.RefValue, commit: null,
				ComplianceContentPullStatuses.Failed, note, actor, cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.Failed(note);
		}

		await _profiles.ReplaceAllAsync(discoveredProfiles, cancellationToken).ConfigureAwait(false);
		await _content.RecordPullAsync(
			context.Job.Id, config.RefType, config.RefValue, commit,
			ComplianceContentPullStatuses.Succeeded, note: null, actor, cancellationToken).ConfigureAwait(false);

		string progressPayload = JsonSerializer.Serialize(new { commit, profile_count = discoveredProfiles.Count });
		await context.Events
			.EmitAsync(JobEventTypes.RunProgress, null, context.Job.RunId, progressPayload, cancellationToken)
			.ConfigureAwait(false);

		return JobExecutionOutcome.Succeeded($"Pulled '{config.RefValue}' at {commit}; {discoveredProfiles.Count} profile(s) found.");
	}

	/// <summary>
	/// Every profile discovered by a successful pull is labeled
	/// <see cref="ProfileStates.Pinned"/> when the config tracks a tag,
	/// <see cref="ProfileStates.Current"/> when it tracks a branch (issue #40 AC
	/// "current / update pending / pinned"). <see cref="ProfileStates.UpdatePending"/>
	/// is not produced by this handler -- it describes a profile whose recorded commit
	/// predates the latest available upstream commit, a comparison this slice's
	/// GET /compliance-content/check (part of the same PR's API surface) computes by
	/// diffing against upstream without mutating stored rows.
	/// </summary>
	private static (string? Commit, IReadOnlyList<ProfileUpsert> Profiles) ParseOutput(IReadOnlyList<object?> output, ComplianceContentConfig config)
	{
		string state = config.RefType == ComplianceContentRefTypes.Tag ? ProfileStates.Pinned : ProfileStates.Current;

		foreach (object? item in output)
		{
			if (item is not System.Management.Automation.PSObject psObject)
			{
				continue;
			}

			string? commit = psObject.Properties["Commit"]?.Value as string;
			if (string.IsNullOrWhiteSpace(commit))
			{
				continue;
			}

			List<ProfileUpsert> profiles = [];
			if (psObject.Properties["Profiles"]?.Value is System.Collections.IEnumerable rawProfiles)
			{
				foreach (object? rawProfile in rawProfiles)
				{
					ProfileUpsert? parsed = TryParseProfile(rawProfile, commit, state);
					if (parsed is not null)
					{
						profiles.Add(parsed);
					}
				}
			}

			return (commit, profiles);
		}

		return (null, []);
	}

	private static ProfileUpsert? TryParseProfile(object? item, string commit, string state)
	{
		if (item is not System.Management.Automation.PSObject psObject)
		{
			return null;
		}

		string? profileKey = psObject.Properties["ProfileKey"]?.Value as string;
		if (string.IsNullOrWhiteSpace(profileKey))
		{
			// One malformed profile row must not fail the whole pull -- same
			// "individual failures don't halt the batch" principle
			// CatalogIndexJobHandler.TryParseArtifact applies.
			return null;
		}

		string? name = psObject.Properties["Name"]?.Value as string;
		string? version = psObject.Properties["Version"]?.Value as string;
		return new ProfileUpsert(profileKey, string.IsNullOrWhiteSpace(name) ? profileKey : name, version, commit, state);
	}

	private async Task<string> ResolveActorAsync(Guid? runId, CancellationToken cancellationToken)
	{
		if (runId is null)
		{
			return "system";
		}

		RunQueueState? state = await _jobs.GetRunQueueStateAsync(runId.Value, cancellationToken).ConfigureAwait(false);
		return string.IsNullOrWhiteSpace(state?.InitiatedBy) ? "system" : state!.InitiatedBy!;
	}
}
