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
///
/// Concurrency (issue #790): machine_id is seeded into a job-scoped identity home
/// nested under this job's own <c>stagingRoot</c> (already unique per job), never the
/// single shared enrollment identity home -- so two concurrent catalog-pull jobs
/// seeding DIFFERENT asset_ids can never cause a tool invocation to authenticate under
/// another job's <c>machine_id</c>. Mirrors the job-scoped-identity-home contract
/// issue #1482's <c>BinariesDownloadJobHandler</c> already established.
/// </summary>
public sealed class CatalogPullJobHandler : IJobHandler
{
	private readonly IDepotEnrollmentRepository _enrollment;
	private readonly IDepotIdentityTool _identityTool;
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
		IDepotIdentityTool identityTool,
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
		ArgumentNullException.ThrowIfNull(identityTool);
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
		_identityTool = identityTool;
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

		// Issue #790: a fresh, job-scoped identity home nested under this job's own
		// stagingRoot (already unique per job and torn down in finally below) -- never
		// the shared enrollment identity home, so two concurrent catalog-pull jobs
		// seeding DIFFERENT asset_ids can never collide on machine_id.
		string identityHome = Path.Combine(stagingRoot, "identity");

		string? assetId;
		DecryptedSecret? decrypted = null;
		try
		{
			CreateRestrictedDirectory(stagingRoot);
			Directory.CreateDirectory(metadataDepotPath);

			decrypted = await _secrets
				.DecryptAsync(activationCode.Id, "system", context.Job.Id, context.Job.RunId, cancellationToken)
				.ConfigureAwait(false);
			await WriteRestrictedFileAsync(activationCodePath, decrypted.Value, cancellationToken).ConfigureAwait(false);

			// Issue #787 (owner decision 2026-08-25): every consumer seeds independently --
			// identity follows the code. Derive the non-secret asset_id from THIS code and
			// seed machine_id from it before invoking the tool, so a connected pull uses the
			// same code-derived identity the validate path proved, per run. The raw code
			// value is never used beyond decoding this field.
			assetId = DepotActivationCodeCodec.TryExtractAssetId(decrypted.Value);
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
			if (!string.IsNullOrWhiteSpace(assetId))
			{
				await _identityTool.SeedMachineIdentityAsync(assetId, identityHome, cancellationToken).ConfigureAwait(false);
			}

			CatalogPullResult pullResult = await _puller
				.PullAsync(metadataDepotPath, activationCodePath, identityHome, cancellationToken).ConfigureAwait(false);

			if (!pullResult.Succeeded)
			{
				string note = _redactor.Redact(pullResult.FailureReason ?? "metadata download failed with no failure reason.");
				return pullResult.IsAuthFailure
					? await RecordFailureAsync(true, note, cancellationToken).ConfigureAwait(false)
					: await RecordFailureAsync(false, note, cancellationToken).ConfigureAwait(false);
			}

			await EmitProgressAsync(context, "Vendor metadata downloaded; authenticating catalog.", cancellationToken).ConfigureAwait(false);

			ManagedToolCatalogAuthenticationResult verification;
			string stagedCatalogPath;
			try
			{
				stagedCatalogPath = ResolveConfigured(metadataDepotPath, toolOptions.ProductVersionCatalogPath);
				verification = await _catalogVerifier.AuthenticateCatalogAsync(metadataDepotPath, cancellationToken).ConfigureAwait(false);
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

			// Issue #764: index depot_artifacts and record catalog_pull_state success
			// BEFORE promoting the on-disk catalog, not after. The prior order promoted
			// first -- a crash between promotion and the index/state write left a NEW
			// on-disk catalog but STALE depot_artifacts rows and STALE
			// catalog_pull_state.last_success_* facts, disagreeing with what a reader
			// could see on disk. Promotion is the cheap, replayable step (a same-volume
			// rename over the already-authenticated staged file), so it moves last: a
			// crash before it completes leaves the prior on-disk catalog untouched
			// (still self-healing, same as before -- the next successful pull re-parses,
			// re-indexes, and re-promotes from scratch) while the facts this handler
			// records about what it fetched are never ahead of what it wrote to disk.
			// Issue #1784/#1818 reconciliation: prior to #1784, this parser identified
			// a binary by its bare fileName (the trailing segment of each entry's NEW
			// depot-relative identity below); a pre-#1784 pull may have left a row
			// under that legacy identity. Derive every artifact's candidate
			// legacy-identity -> new-identity pair up front (see BuildLegacyRenames,
			// which ENFORCES the bare-fileName uniqueness this comment used to merely
			// assert) and reconcile the WHOLE batch in one bounded
			// call, not one per artifact (#1818: the per-artifact shape this replaced
			// issued one round trip per artifact on EVERY pull -- 1291 on the owner's
			// live stack -- because the guard fires on identity SHAPE, not on whether
			// a legacy row exists; RekeyManyAsync's own first step is the query that
			// actually checks existence, so a steady-state pull with zero legacy rows
			// costs one query, not N). RekeyManyAsync also folds issue #1804's
			// collision case (the presence sweep already created the new-identity row
			// before this pull ever ran) rather than leaving it stale -- see its own
			// doc comment. Called BEFORE the upsert loop below so a rename has a row
			// to act on before UpsertAsync creates one at the TO identity itself.
			Dictionary<string, string> legacyRenames = BuildLegacyRenames(parsed, out IReadOnlyList<string> ambiguousLegacyIdentities);
			if (ambiguousLegacyIdentities.Count > 0)
			{
				await EmitProgressAsync(
					context,
					"Skipping legacy-identity reconciliation for " +
					$"{ambiguousLegacyIdentities.Count} ambiguous bare fileName(s) shared by more than one catalog entry: " +
					$"{string.Join(", ", ambiguousLegacyIdentities)}. Any pre-#1784 row still keyed under one of these " +
					"names cannot be attributed to a single artifact and is left as-is rather than renamed onto a guess.",
					cancellationToken).ConfigureAwait(false);
			}

			await _artifacts.RekeyManyAsync(legacyRenames, cancellationToken).ConfigureAwait(false);

			// Issue #797: reconcile a pre-fix stray row for the catalog DOCUMENT
			// itself (NULL product, NULL version -- a prior parser bug indexed it as
			// an artifact) the same self-healing way #764's reorder already treats
			// every other index fact -- every successful pull retries this until it
			// finds nothing left to reconcile, no manual DB surgery required. A no-op
			// on every stack that never hit the bug.
			await _artifacts.SupersedeCatalogDocumentRowAsync(toolOptions.ProductVersionCatalogPath, cancellationToken).ConfigureAwait(false);

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
			await EmitProgressAsync(context, $"Indexed {upserted} artifact(s); promoting catalog.", cancellationToken).ConfigureAwait(false);

			// Atomic promotion: the authenticated staged catalog replaces the prior
			// on-disk one via a same-volume file rename, so a reader of the depot
			// share never observes a partially written catalog. A failure here no
			// longer leaves index/state ahead of a stale on-disk file the way the
			// pre-#764 promote-first order could -- the freshly indexed/recorded facts
			// above already describe what was just fetched and authenticated, and the
			// on-disk catalog simply has not caught up to them yet (self-healing on the
			// next successful pull, same as issue #687's original AC).
			string activeCatalogPath = ResolveConfigured(catalogOptions.DepotPath, toolOptions.ProductVersionCatalogPath);
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(activeCatalogPath)!);
				File.Copy(stagedCatalogPath, activeCatalogPath + ".tmp", overwrite: true);
				File.Move(activeCatalogPath + ".tmp", activeCatalogPath, overwrite: true);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// UnauthorizedAccessException alongside IOException: on Linux a promotion
				// target that is unwritable (a permission error, or -- the crash-window
				// test's own probe -- a path component that is unexpectedly a directory)
				// surfaces as UnauthorizedAccessException, not IOException, so both must
				// be caught here for the #764 reorder's "index/state already recorded,
				// only the on-disk promotion failed" outcome to actually be reachable
				// rather than an unhandled exception escaping the handler.
				return JobExecutionOutcome.Failed(_redactor.Redact($"Indexed {upserted} artifact(s), but the authenticated catalog could not be promoted: {exception.Message}"));
			}

			await EmitProgressAsync(context, $"Pull complete: indexed {upserted} artifact(s).", cancellationToken).ConfigureAwait(false);

			// Issue #1887: the ambiguous-legacy-identity exclusion above is reported
			// only on run.progress's message field, which no operator surface renders
			// (neither frontend/src/screens/liverun/liverun.ts nor
			// .../livejobs/livejobs.ts reads it). Append it to the terminal Succeeded
			// note -- the operator-facing summary an operator actually reads -- so a
			// pull that skipped reconciliation for one or more ambiguous artifacts
			// says so on a surface that is rendered.
			string successNote = $"Pulled and indexed {upserted} artifact(s) from the authenticated vendor catalog.";
			if (ambiguousLegacyIdentities.Count > 0)
			{
				successNote += " Skipped legacy-identity reconciliation for " +
					$"{ambiguousLegacyIdentities.Count} ambiguous bare fileName(s): " +
					$"{string.Join(", ", ambiguousLegacyIdentities)}.";
			}

			return JobExecutionOutcome.Succeeded(successNote);
		}
		finally
		{
			TryDeleteDirectory(stagingRoot);
		}
	}

	private async Task<JobExecutionOutcome> RecordFailureAsync(bool isAuthFailure, string reason, CancellationToken cancellationToken)
	{
		string note = _redactor.Redact(reason);
		await _pullState.RecordFailureAsync(isAuthFailure, note, cancellationToken).ConfigureAwait(false);
		return isAuthFailure ? JobExecutionOutcome.AuthFailed(note) : JobExecutionOutcome.Failed(note);
	}

	/// <summary>
	/// Issue #1852's second gap: prior to this, the legacy-identity map was built by a
	/// bare indexer assignment (<c>legacyRenames[legacyIdentity] = candidate.RelativePath</c>)
	/// under a comment asserting that "two artifacts never share a bare fileName under
	/// this catalog's own uniqueness" -- an assumption with nothing enforcing it. The
	/// key is DERIVED from the value (the trailing segment of the depot-relative path),
	/// so the shape an actual violation takes is a KEY collision, not a value collision:
	/// <c>PROD/COMP/VCENTER/x.iso</c> and <c>PROD/COMP/NSX/x.iso</c> both derive the
	/// legacy identity <c>x.iso</c>, and the indexer assignment silently kept whichever
	/// one the parser happened to emit last. A pre-#1784 row keyed <c>x.iso</c> would
	/// then have been renamed onto that arbitrary winner -- attributing one product's
	/// downloaded binary to another, silently and non-deterministically.
	///
	/// This enforces the invariant where it can actually be violated. A legacy identity
	/// derived from two or more DIFFERENT depot-relative paths is genuinely ambiguous:
	/// nothing in the catalog says which artifact a legacy bare-fileName row belonged
	/// to, so there is no correct rename. Such identities are excluded from the map
	/// entirely (never renamed onto a guess) and returned in
	/// <paramref name="ambiguousLegacyIdentities"/> so the caller can surface them on
	/// the run rather than letting the collision pass unobserved. The consequence of
	/// exclusion is the pre-#1784 status quo for those artifacts alone -- the legacy row
	/// stays under its bare fileName while <c>UpsertAsync</c> creates the new-identity
	/// rows -- which is a visible stale row, not a mis-attribution. Deliberately NOT a
	/// throw: the ambiguity is confined to a backwards-compatibility reconciliation
	/// step, and aborting a whole 1000+ artifact catalog pull over it would turn a
	/// recoverable data oddity into an outage.
	/// </summary>
	internal static Dictionary<string, string> BuildLegacyRenames(
		IReadOnlyList<DepotArtifactUpsert> parsed,
		out IReadOnlyList<string> ambiguousLegacyIdentities)
	{
		ArgumentNullException.ThrowIfNull(parsed);

		Dictionary<string, string> legacyRenames = new(StringComparer.Ordinal);
		SortedSet<string> ambiguous = new(StringComparer.Ordinal);
		foreach (DepotArtifactUpsert candidate in parsed)
		{
			string legacyIdentity = candidate.RelativePath[(candidate.RelativePath.LastIndexOf('/') + 1)..];
			if (string.Equals(legacyIdentity, candidate.RelativePath, StringComparison.Ordinal))
			{
				continue;
			}

			if (legacyRenames.TryGetValue(legacyIdentity, out string? alreadyMapped))
			{
				if (string.Equals(alreadyMapped, candidate.RelativePath, StringComparison.Ordinal))
				{
					continue;
				}

				_ = ambiguous.Add(legacyIdentity);
				continue;
			}

			legacyRenames[legacyIdentity] = candidate.RelativePath;
		}

		foreach (string collided in ambiguous)
		{
			_ = legacyRenames.Remove(collided);
		}

		ambiguousLegacyIdentities = [.. ambiguous];
		return legacyRenames;
	}

	private static async Task EmitProgressAsync(JobExecutionContext context, string message, CancellationToken cancellationToken)
	{
		string payload = JsonSerializer.Serialize(new { message });
		await context.Events.EmitAsync(JobEventTypes.RunProgress, null, context.Job.RunId, payload, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Issue #1829: delegates to the one shared implementation (<see cref="ManagedToolRelativePathResolver"/>) rather than its own copy of the rooted/escape guard.</summary>
	private static string ResolveConfigured(string root, string relative) => ManagedToolRelativePathResolver.Resolve(root, relative);

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
