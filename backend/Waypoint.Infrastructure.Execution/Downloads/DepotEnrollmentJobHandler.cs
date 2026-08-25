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

using System.Security.Cryptography;
using System.Text.Json;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.Secrets;

namespace Waypoint.Infrastructure.Downloads;

/// <summary>
/// The <c>depot-enrollment</c> <see cref="JobShape.Simple"/> job handler (issue #691):
/// invokes the installed <c>vcf-download-tool</c> noninteractively for the two
/// assisted-enrollment operations -- generating/reading the Software Depot ID, and
/// validating a stored Activation Code -- and persists the outcome to the
/// <c>depot_enrollment</c> singleton (migration 0048). Never resolves or stores the
/// Activation Code itself; <c>validate-code</c> decrypts the already-stored
/// <see cref="CredentialTypes.DepotActivationCode"/> credential for exactly the
/// duration of the bounded tool call (the same decrypt-for-one-call pattern
/// <c>ManagedToolInstallJobHandler</c>'s depot-fetch path uses), writes it to a
/// job-scoped temp file (never argv, never an environment variable, never a log line),
/// and always deletes that file in <c>finally</c> regardless of outcome.
///
/// Payload contract: <c>{"operation": "generate-depot-id"|"validate-code"}</c>.
/// </summary>
public sealed class DepotEnrollmentJobHandler : IJobHandler
{
	private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
	};

	private readonly IDepotIdentityTool _tool;
	private readonly IDepotEnrollmentRepository _enrollment;
	private readonly ICredentialSecretStore _secrets;
	private readonly Waypoint.Infrastructure.Secrets.CredentialRepository _credentials;
	private readonly ISecretRedactor _redactor;

	public DepotEnrollmentJobHandler(
		IDepotIdentityTool tool,
		IDepotEnrollmentRepository enrollment,
		ICredentialSecretStore secrets,
		Waypoint.Infrastructure.Secrets.CredentialRepository credentials,
		ISecretRedactor redactor)
	{
		ArgumentNullException.ThrowIfNull(tool);
		ArgumentNullException.ThrowIfNull(enrollment);
		ArgumentNullException.ThrowIfNull(secrets);
		ArgumentNullException.ThrowIfNull(credentials);
		ArgumentNullException.ThrowIfNull(redactor);
		_tool = tool;
		_enrollment = enrollment;
		_secrets = secrets;
		_credentials = credentials;
		_redactor = redactor;
	}

	public string JobType => "depot-enrollment";

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		EnrollmentPayload? payload;
		try
		{
			payload = JsonSerializer.Deserialize<EnrollmentPayload>(context.Job.Payload, PayloadOptions);
		}
		catch (JsonException exception)
		{
			return JobExecutionOutcome.Failed($"Malformed depot-enrollment payload: {exception.Message}");
		}

		return payload?.Operation switch
		{
			"generate-depot-id" => await GenerateDepotIdAsync(cancellationToken).ConfigureAwait(false),
			"validate-code" => await ValidateCodeAsync(context, cancellationToken).ConfigureAwait(false),
			_ => JobExecutionOutcome.Failed(
				$"depot-enrollment payload requires 'operation' to be 'generate-depot-id' or 'validate-code' (got '{payload?.Operation}')."),
		};
	}

	private async Task<JobExecutionOutcome> GenerateDepotIdAsync(CancellationToken cancellationToken)
	{
		DepotIdentityResult result = await _tool.GetDepotIdAsync(cancellationToken).ConfigureAwait(false);
		if (!result.Succeeded)
		{
			return JobExecutionOutcome.Failed(result.FailureReason);
		}

		await _enrollment.SetDepotIdAsync(result.DepotId!, cancellationToken).ConfigureAwait(false);
		return JobExecutionOutcome.Succeeded($"Software Depot ID generated: {result.DepotId}");
	}

	private async Task<JobExecutionOutcome> ValidateCodeAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		CredentialResponse? activationCode = await _credentials
			.FindByTypeAsync(CredentialTypes.DepotActivationCode, cancellationToken)
			.ConfigureAwait(false);
		if (activationCode is null || !activationCode.HasSecret)
		{
			return JobExecutionOutcome.Failed(
				$"No credential of type '{CredentialTypes.DepotActivationCode}' is configured; store an Activation Code before validating it.");
		}

		string actor = "system";
		string stagingPath = Path.Combine(Path.GetTempPath(), $"depot-activation-code-{Guid.NewGuid():N}.txt");
		string? pairedAssetId;
		DecryptedSecret? decrypted = null;
		try
		{
			// security.md control 4 / #8's fail-closed decrypt audit: writes the
			// secret.decrypted audit row before any plaintext reaches this method.
			decrypted = await _secrets
				.DecryptAsync(activationCode.Id, actor, context.Job.Id, context.Job.RunId, cancellationToken)
				.ConfigureAwait(false);

			// Job-scoped staging file, never argv/env (both are visible via /proc to
			// other processes on the host; a file with restrictive permissions is not).
			// Written for exactly the duration of the bounded tool call and removed in
			// finally below regardless of outcome.
			await File.WriteAllTextAsync(stagingPath, decrypted.Value, cancellationToken).ConfigureAwait(false);
			TryRestrictPermissions(stagingPath);

			// Issue #787: establish (or confirm) the identity the tool checks the code
			// against BEFORE invoking it. The decoded asset_id is non-secret; the raw code
			// value is never used here beyond decoding this pairing field.
			pairedAssetId = await EnsureMachineIdentityAsync(
				DepotActivationCodeCodec.TryExtractAssetId(decrypted.Value), cancellationToken).ConfigureAwait(false);
		}
		catch (CredentialSecretNotFoundException exception)
		{
			return JobExecutionOutcome.Failed($"Activation Code credential has no stored secret: {exception.Message}");
		}
		catch (MasterKeyUnavailableException exception)
		{
			return JobExecutionOutcome.Failed($"Activation Code could not be decrypted: {exception.Message}");
		}
		finally
		{
			decrypted?.Dispose();
		}

		try
		{
			DepotValidationResult result = await _tool.ValidateActivationCodeAsync(stagingPath, pairedAssetId, cancellationToken).ConfigureAwait(false);

			if (result.Succeeded)
			{
				await _enrollment.SetValidationOutcomeAsync(succeeded: true, failureNote: null, cancellationToken).ConfigureAwait(false);
				return JobExecutionOutcome.Succeeded("Activation Code validated successfully.");
			}

			// jobs.note is a sink too (security.md control 1) -- redact before it is
			// ever persisted, same as CredentialTestJobHandler.ClassifyResultAsync.
			string note = _redactor.Redact(result.FailureReason ?? "Activation Code validation failed with no failure reason.");

			if (result.IsAuthFailure)
			{
				// Issue #691 AC: "Missing Broadcom portal roles are surfaced as external
				// enrollment guidance, not retried as a runner failure" -- AuthFailed
				// counts toward the credential's auth-failure accounting the same way
				// every other credential type's rejected test does, but the enrollment
				// state itself moves to auth_failing (not back to activation_code_stored)
				// so the UI shows a terminal-until-operator-action fact, not "pending."
				await _enrollment.SetValidationOutcomeAsync(succeeded: false, note, cancellationToken).ConfigureAwait(false);
				return JobExecutionOutcome.AuthFailed(note);
			}

			// A non-auth failure (tool missing, timeout) never flips enrollment state --
			// it is a runner/environment problem, not new information about the code.
			return JobExecutionOutcome.Failed(note);
		}
		finally
		{
			TryDelete(stagingPath);
		}
	}

	/// <summary>
	/// Ensures the tool's <c>machine_id</c> identity is in place before validation, and
	/// returns the asset_id the tool must match (issue #787). Two paths converge here:
	/// <list type="bullet">
	/// <item>A pairing already exists (generate-first, or a prior adopt): re-seed the
	/// identity file from that stored pairing -- a no-op unless a container rebuild
	/// emptied the identity home -- and validate against it. The generated-first
	/// mismatch branch is untouched: a foreign code was already rejected at
	/// accept-time, so a stored pairing is always the code's own asset_id.</item>
	/// <item>No pairing exists but a code is stored (the credential-panel path the owner
	/// used): adopt the code's decoded asset_id as the managed Depot ID, seed the
	/// identity, and record the adoption in enrollment state so the recovery is durable
	/// and visible.</item>
	/// </list>
	/// Returns <c>null</c> only when neither a stored pairing nor a decodable code
	/// asset_id is available, in which case validation proceeds against whatever identity
	/// the tool already holds (and will fail closed if none).
	/// </summary>
	private async Task<string?> EnsureMachineIdentityAsync(string? decodedAssetId, CancellationToken cancellationToken)
	{
		DepotEnrollment? enrollment = await _enrollment.GetAsync(cancellationToken).ConfigureAwait(false);
		string? storedPairing = enrollment?.PairedAssetId;

		if (!string.IsNullOrWhiteSpace(storedPairing))
		{
			// Existing pairing (generate-first or already-adopted): re-seed from it so a
			// rebuilt/empty identity home recovers without re-entering the code.
			await _tool.SeedMachineIdentityAsync(storedPairing, cancellationToken).ConfigureAwait(false);
			return storedPairing;
		}

		if (string.IsNullOrWhiteSpace(decodedAssetId))
		{
			// No pairing and an undecodable code -- let the tool fail closed against its
			// own (absent) identity rather than fabricate one.
			return null;
		}

		// Adopt-on-validate: the credential-panel path with no prior enrollment. Persist
		// the adopted identity first so the recovery survives even if the tool call is
		// interrupted, then seed the identity file the tool checks the code against.
		await _enrollment.AdoptExistingCodeAsync(decodedAssetId, cancellationToken).ConfigureAwait(false);
		await _tool.SeedMachineIdentityAsync(decodedAssetId, cancellationToken).ConfigureAwait(false);
		return decodedAssetId;
	}

	private static void TryRestrictPermissions(string path)
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch (IOException)
		{
			// Best-effort cleanup only -- a stray staging file under the OS temp
			// directory is not a correctness issue for a job that has already recorded
			// its outcome, but the finally block always attempts it.
		}
	}

	private sealed record EnrollmentPayload(string? Operation);
}
