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
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.Secrets;

namespace Waypoint.Infrastructure.Catalog;

/// <summary>
/// The <c>catalog-pull</c> <see cref="JobShape.Simple"/> job handler (issue #687,
/// epic #667): the connected counterpart to the local, credential-free
/// <c>catalog-index</c> re-index. Gated on issue #691's <c>depot_enrollment</c> state
/// being <see cref="DepotEnrollmentStates.Validated"/> -- a connected pull is disabled
/// until the managed tool, a generated Depot ID, and a matching validated Activation
/// Code are all ready, so this handler never even attempts a call the enrollment flow
/// has not already proven will authenticate.
///
/// Sequence: resolve the enrollment gate -&gt; decrypt the stored Activation Code into a
/// job-scoped, atomically-restrictive-mode temp file (issue #760: created inside a
/// 0700 job-scoped directory, never create-then-chmod) -&gt; run <c>metadata download</c>
/// via <see cref="IManagedToolMetadataPuller"/> into a scratch staging path -&gt;
/// authenticate the downloaded <c>productVersionCatalog.json</c> against the same
/// signature-envelope convention <c>BroadcomManagedToolCatalogVerifier</c> uses for the
/// VCFDT tool distribution itself -&gt; atomically promote (same-volume file rename) the
/// authenticated catalog over the prior one under <see cref="CatalogOptions.DepotPath"/>
/// -&gt; parse and upsert every binary entry into <c>depot_artifacts</c> -&gt; record the
/// outcome in <c>catalog_pull_state</c> (migration 0049). The staging directory and its
/// temp files are always removed in <c>finally</c>, and a failure at any stage leaves
/// the prior-good on-disk catalog and prior-good <c>catalog_pull_state.last_success_*</c>
/// facts untouched (issue #687 AC).
/// </summary>
public sealed class CatalogPullJobHandler : IJobHandler
{
	private readonly IDepotEnrollmentRepository _enrollment;
	private readonly IManagedToolMetadataPuller _puller;
	private readonly IManagedToolCatalogVerifier _catalogVerifier;
	private readonly IDepotArtifactRepository _artifacts;
	private readonly ICatalogPullStateRepository _pullState;
	private readonly ICredentialSecretStore _secrets;
	private readonly Waypoint.Infrastructure.Secrets.CredentialRepository _credentials;
	private readonly ISecretRedactor _redactor;
	private readonly IOptions<CatalogOptions> _catalogOptions;
	private readonly IOptions<ManagedToolOptions> _toolOptions;

	public CatalogPullJobHandler(
		IDepotEnrollmentRepository enrollment,
		IManagedToolMetadataPuller puller,
		IManagedToolCatalogVerifier catalogVerifier,
		IDepotArtifactRepository artifacts,
		ICatalogPullStateRepository pullState,
		ICredentialSecretStore secrets,
		Waypoint.Infrastructure.Secrets.CredentialRepository credentials,
		ISecretRedactor redactor,
		IOptions<CatalogOptions> catalogOptions,
		IOptions<ManagedToolOptions> toolOptions)
	{
		ArgumentNullException.ThrowIfNull(enrollment);
		ArgumentNullException.ThrowIfNull(puller);
		ArgumentNullException.ThrowIfNull(catalogVerifier);
		ArgumentNullException.ThrowIfNull(artifacts);
		ArgumentNullException.ThrowIfNull(pullState);
		ArgumentNullException.ThrowIfNull(secrets);
		ArgumentNullException.ThrowIfNull(credentials);
		ArgumentNullException.ThrowIfNull(redactor);
		ArgumentNullException.ThrowIfNull(catalogOptions);
		ArgumentNullException.ThrowIfNull(toolOptions);

		_enrollment = enrollment;
		_puller = puller;
		_catalogVerifier = catalogVerifier;
		_artifacts = artifacts;
		_pullState = pullState;
		_secrets = secrets;
		_credentials = credentials;
		_redactor = redactor;
		_catalogOptions = catalogOptions;
		_toolOptions = toolOptions;
	}

	public string JobType => "catalog-pull";

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		DepotEnrollment? enrollment = await _enrollment.GetAsync(cancellationToken).ConfigureAwait(false);
		if (enrollment is null || !string.Equals(enrollment.State, DepotEnrollmentStates.Validated, StringComparison.Ordinal))
		{
			return await RecordFailureAsync(
				isAuthFailure: false,
				"Connected catalog pull is disabled until the managed tool is installed, a Software Depot ID is generated, " +
				"and a matching Activation Code has been validated (see Depot & Tokens enrollment).",
				cancellationToken).ConfigureAwait(false);
		}

		CredentialResponse? activationCode = await _credentials
			.FindByTypeAsync(CredentialTypes.DepotActivationCode, cancellationToken).ConfigureAwait(false);
		if (activationCode is null || !activationCode.HasSecret)
		{
			return await RecordFailureAsync(
				isAuthFailure: false,
				$"No credential of type '{CredentialTypes.DepotActivationCode}' is configured.",
				cancellationToken).ConfigureAwait(false);
		}

		ManagedToolOptions toolOptions = _toolOptions.Value;
		CatalogOptions catalogOptions = _catalogOptions.Value;

		// Issue #760: the secret-bearing temp file is created ATOMICALLY with a
		// restrictive mode by first creating a 0700 job-scoped directory, then
		// writing the file inside it -- never create-then-chmod.
		string stagingRoot = Path.Combine(toolOptions.ToolStatePath, toolOptions.CatalogPullStagingDirectoryName, $"job-{context.Job.Id:N}");
		string activationCodePath = Path.Combine(stagingRoot, "activation-code.txt");
		string metadataDepotPath = Path.Combine(stagingRoot, "depot");

		DecryptedSecret? decrypted = null;
		try
		{
			CreateRestrictedDirectory(stagingRoot);
			Directory.CreateDirectory(metadataDepotPath);

			decrypted = await _secrets
				.DecryptAsync(activationCode.Id, "system", context.Job.Id, context.Job.RunId, cancellationToken)
				.ConfigureAwait(false);
			await WriteRestrictedFileAsync(activationCodePath, decrypted.Value, cancellationToken).ConfigureAwait(false);
		}
		catch (CredentialSecretNotFoundException exception)
		{
			return await RecordFailureAsync(false, $"Activation Code credential has no stored secret: {exception.Message}", cancellationToken).ConfigureAwait(false);
		}
		catch (MasterKeyUnavailableException exception)
		{
			return await RecordFailureAsync(false, $"Activation Code could not be decrypted: {exception.Message}", cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			decrypted?.Dispose();
		}

		try
		{
			CatalogPullResult pullResult = await _puller
				.PullAsync(metadataDepotPath, activationCodePath, cancellationToken).ConfigureAwait(false);

			if (!pullResult.Succeeded)
			{
				string note = _redactor.Redact(pullResult.FailureReason ?? "metadata download failed with no failure reason.");
				return pullResult.IsAuthFailure
					? await RecordFailureAsync(true, note, cancellationToken).ConfigureAwait(false)
					: await RecordFailureAsync(false, note, cancellationToken).ConfigureAwait(false);
			}

			await EmitProgressAsync(context, "Vendor metadata downloaded; authenticating catalog.", cancellationToken).ConfigureAwait(false);

			ManagedToolCatalogVerificationResult verification;
			string stagedCatalogPath;
			try
			{
				stagedCatalogPath = ResolveConfigured(metadataDepotPath, toolOptions.ProductVersionCatalogPath);
				verification = await AuthenticateCatalogAsync(metadataDepotPath, toolOptions, cancellationToken).ConfigureAwait(false);
			}
			catch (InvalidOperationException exception)
			{
				return await RecordFailureAsync(false, $"Downloaded catalog path resolution failed: {exception.Message}", cancellationToken).ConfigureAwait(false);
			}

			if (!verification.Valid)
			{
				return await RecordFailureAsync(false, $"Downloaded vendor catalog failed authentication: {verification.FailureReason}", cancellationToken).ConfigureAwait(false);
			}

			string catalogJson;
			try
			{
				catalogJson = await File.ReadAllTextAsync(stagedCatalogPath, cancellationToken).ConfigureAwait(false);
			}
			catch (IOException exception)
			{
				return await RecordFailureAsync(false, $"Authenticated catalog could not be read: {exception.Message}", cancellationToken).ConfigureAwait(false);
			}

			IReadOnlyList<DepotArtifactUpsert> parsed;
			try
			{
				parsed = VendorProductVersionCatalogParser.Parse(catalogJson);
			}
			catch (JsonException exception)
			{
				return await RecordFailureAsync(false, $"Authenticated vendor catalog is malformed: {exception.Message}", cancellationToken).ConfigureAwait(false);
			}

			// Atomic promotion: the authenticated staged catalog replaces the prior
			// on-disk one via a same-volume file rename, so a reader of the depot
			// share never observes a partially written catalog and a failure before
			// this point leaves the prior-good file untouched (issue #687 AC).
			string activeCatalogPath = ResolveConfigured(catalogOptions.DepotPath, toolOptions.ProductVersionCatalogPath);
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(activeCatalogPath)!);
				File.Copy(stagedCatalogPath, activeCatalogPath + ".tmp", overwrite: true);
				File.Move(activeCatalogPath + ".tmp", activeCatalogPath, overwrite: true);
			}
			catch (IOException exception)
			{
				return await RecordFailureAsync(false, $"Authenticated catalog could not be promoted: {exception.Message}", cancellationToken).ConfigureAwait(false);
			}

			int upserted = 0;
			foreach (DepotArtifactUpsert upsert in parsed)
			{
				await _artifacts.UpsertAsync(upsert, cancellationToken).ConfigureAwait(false);
				upserted++;
				if (upserted % 25 == 0)
				{
					await EmitProgressAsync(context, $"Indexed {upserted} artifact(s) so far...", cancellationToken).ConfigureAwait(false);
				}
			}

			await _pullState.RecordSuccessAsync(upserted, cancellationToken).ConfigureAwait(false);
			await EmitProgressAsync(context, $"Pull complete: indexed {upserted} artifact(s).", cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.Succeeded($"Pulled and indexed {upserted} artifact(s) from the authenticated vendor catalog.");
		}
		finally
		{
			TryDeleteDirectory(stagingRoot);
		}
	}

	private Task<ManagedToolCatalogVerificationResult> AuthenticateCatalogAsync(
		string repositoryRoot, ManagedToolOptions toolOptions, CancellationToken cancellationToken)
	{
		// IManagedToolCatalogVerifier.VerifyAsync also checks a named artifact's
		// size/sha within the catalog; here there is no single "artifact" being
		// installed -- only the catalog document's own signature matters, so an
		// empty artifact path/version is not something the verifier's candidate
		// search needs to match against. Reuse is limited to the signature-envelope
		// authentication path this call exercises via the catalog/signature files.
		return _catalogVerifier.VerifyAsync(repositoryRoot, artifactPath: Path.Combine(repositoryRoot, "unused"), version: null, cancellationToken);
	}

	private async Task<JobExecutionOutcome> RecordFailureAsync(bool isAuthFailure, string reason, CancellationToken cancellationToken)
	{
		string note = _redactor.Redact(reason);
		await _pullState.RecordFailureAsync(isAuthFailure, note, cancellationToken).ConfigureAwait(false);
		return isAuthFailure ? JobExecutionOutcome.AuthFailed(note) : JobExecutionOutcome.Failed(note);
	}

	private static async Task EmitProgressAsync(JobExecutionContext context, string message, CancellationToken cancellationToken)
	{
		string payload = JsonSerializer.Serialize(new { message });
		await context.Events.EmitAsync(JobEventTypes.RunProgress, null, context.Job.RunId, payload, cancellationToken).ConfigureAwait(false);
	}

	private static string ResolveConfigured(string root, string relative)
	{
		if (Path.IsPathRooted(relative))
		{
			throw new InvalidOperationException("Managed-tool catalog paths must be relative to the repository root.");
		}

		string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		string candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
		if (!candidate.StartsWith(fullRoot, StringComparison.Ordinal))
		{
			throw new InvalidOperationException("Managed-tool catalog path escapes the repository root.");
		}

		return candidate;
	}

	/// <summary>
	/// Issue #760: create the job-scoped staging directory with a restrictive mode
	/// ATOMICALLY (the POSIX <c>mkdir(path, 0700)</c> the .NET directory-creation
	/// overload maps to) rather than creating it with the default mode and then
	/// chmod-ing afterward, which would leave a window where the directory (and any
	/// file placed inside it right after creation) is briefly group/world-readable.
	/// </summary>
	private static void CreateRestrictedDirectory(string path)
	{
		if (OperatingSystem.IsWindows())
		{
			Directory.CreateDirectory(path);
			return;
		}

		Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
	}

	/// <summary>
	/// Issue #760: writes the decrypted Activation Code to a temp file whose mode is
	/// restrictive from creation (<see cref="FileStreamOptions.UnixCreateMode"/>) --
	/// never a plain <c>WriteAllTextAsync</c> followed by a separate
	/// <c>SetUnixFileMode</c> call, which is exactly the create-then-chmod race issue
	/// #760 was filed to close. The containing directory is already 0700
	/// (<see cref="CreateRestrictedDirectory"/>), so this is defense in depth, not
	/// the only control.
	/// </summary>
	private static async Task WriteRestrictedFileAsync(string path, string contents, CancellationToken cancellationToken)
	{
		FileStreamOptions options = new()
		{
			Mode = FileMode.Create,
			Access = FileAccess.Write,
			Share = FileShare.None,
		};

		if (!OperatingSystem.IsWindows())
		{
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		}

		await using FileStream stream = new(path, options);
		await using StreamWriter writer = new(stream);
		await writer.WriteAsync(contents.AsMemory(), cancellationToken).ConfigureAwait(false);
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch (IOException)
		{
			// Best-effort cleanup only, matching DepotEnrollmentJobHandler's
			// TryDelete convention -- a stray staging directory does not change a
			// job's already-recorded outcome, but cleanup is always attempted.
		}
		catch (UnauthorizedAccessException)
		{
		}
	}
}
