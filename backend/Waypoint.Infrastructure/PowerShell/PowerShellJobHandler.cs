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
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.PowerShell;

namespace Waypoint.Infrastructure.PowerShell;

/// <summary>
/// The bridge between the #5 job engine and the #6 PowerShell host: executes a job
/// whose payload describes one PowerShell invocation, maps the execution result onto
/// the job state machine, and classifies authentication failures so the engine's
/// durable credential halt (migration 0005) can trip.
///
/// <c>jobs.job_type</c> is a closed CHECK set with no generic "powershell" member,
/// so this class is deliberately type-parameterized rather than self-registering:
/// the real job types (<c>catalog-index</c> #8, <c>download</c> #9) construct one
/// per type with their own defaults. Payload contract (JSON object):
/// <c>{"command": "Verb-Noun", "kind": "command"|"script", "parameters": {...},
/// "timeoutSeconds": n}</c> -- parameter values reach PowerShell as typed bindings
/// via <see cref="PowerShellRequest.Parameters"/>, never as script text.
///
/// The outcome note is scrubbed through <see cref="ISecretRedactor"/> before it is
/// returned: <c>jobs.note</c> is a sink too (security.md control 1), and a vendor
/// error message is exactly where a rejected credential's value would surface.
/// </summary>
public sealed class PowerShellJobHandler : IJobHandler
{
	private readonly IPowerShellExecutor _executor;
	private readonly ISecretRedactor _redactor;
	private readonly IOptions<PowerShellOptions> _options;

	public PowerShellJobHandler(string jobType, IPowerShellExecutor executor, ISecretRedactor redactor, IOptions<PowerShellOptions> options)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(jobType);
		ArgumentNullException.ThrowIfNull(executor);
		ArgumentNullException.ThrowIfNull(redactor);
		ArgumentNullException.ThrowIfNull(options);

		JobType = jobType;
		_executor = executor;
		_redactor = redactor;
		_options = options;
	}

	public string JobType { get; }

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		PowerShellRequest request;
		try
		{
			request = ParsePayload(context.Job.Payload, context.Job.Id, context.Job.RunId);
		}
		catch (JsonException exception)
		{
			return JobExecutionOutcome.Failed($"Job payload is not a valid PowerShell invocation: {exception.Message}");
		}
		catch (ArgumentException exception)
		{
			return JobExecutionOutcome.Failed($"Job payload is not a valid PowerShell invocation: {exception.Message}");
		}

		PowerShellExecutionResult result = await _executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
		if (result.Succeeded)
		{
			return JobExecutionOutcome.Succeeded(
				result.HadErrors ? "Completed with non-terminating errors (see job log)." : null);
		}

		string note = result.FailureReason ?? "PowerShell invocation failed with no failure reason.";
		return IsAuthFailure(note) ? JobExecutionOutcome.AuthFailed(note) : JobExecutionOutcome.Failed(note);
	}

	private static PowerShellRequest ParsePayload(string payloadJson, Guid jobId, Guid? runId)
	{
		using JsonDocument document = JsonDocument.Parse(payloadJson);
		JsonElement root = document.RootElement;
		if (!root.TryGetProperty("command", out JsonElement commandElement) || commandElement.ValueKind != JsonValueKind.String)
		{
			throw new ArgumentException("payload requires a string 'command' property");
		}

		PowerShellRequestKind kind = PowerShellRequestKind.Command;
		if (root.TryGetProperty("kind", out JsonElement kindElement) &&
			string.Equals(kindElement.GetString(), "script", StringComparison.OrdinalIgnoreCase))
		{
			kind = PowerShellRequestKind.Script;
		}

		Dictionary<string, object?>? parameters = null;
		if (root.TryGetProperty("parameters", out JsonElement parametersElement) && parametersElement.ValueKind == JsonValueKind.Object)
		{
			parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
			foreach (JsonProperty property in parametersElement.EnumerateObject())
			{
				parameters[property.Name] = ToParameterValue(property.Value);
			}
		}

		TimeSpan? timeout = null;
		if (root.TryGetProperty("timeoutSeconds", out JsonElement timeoutElement) && timeoutElement.TryGetInt32(out int timeoutSeconds) && timeoutSeconds > 0)
		{
			timeout = TimeSpan.FromSeconds(timeoutSeconds);
		}

		return new PowerShellRequest(commandElement.GetString()!, kind, parameters, jobId, runId, timeout);
	}

	/// <summary>JSON scalars to the CLR types PowerShell binds naturally; nested arrays/objects pass as raw JSON text for the function to parse.</summary>
	private static object? ToParameterValue(JsonElement element)
	{
		return element.ValueKind switch
		{
			JsonValueKind.String => element.GetString(),
			JsonValueKind.Number => element.TryGetInt64(out long integer) ? integer : element.GetDouble(),
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			JsonValueKind.Null => null,
			_ => element.GetRawText(),
		};
	}

	private bool IsAuthFailure(string failureReason)
	{
		foreach (string marker in _options.Value.AuthFailureMarkers)
		{
			if (failureReason.Contains(marker, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}
}
