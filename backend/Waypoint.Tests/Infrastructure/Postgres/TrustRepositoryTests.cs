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

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Trust;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Trust;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #753 (migration 0059, ADR-0025): proves storage/versioning/delete-safety
/// against real Postgres -- the partial unique indexes, <c>ON DELETE RESTRICT</c>, and
/// the supersede-in-one-transaction shape are all Postgres-specific and have no
/// meaningful fake. Every certificate PEM used here comes from
/// <see cref="InventedCertificateFactory"/> -- freshly generated, self-signed,
/// <c>*.example.internal</c> subjects, never real or captured CA material.
/// </summary>
[Collection("Postgres")]
public sealed class TrustRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private TrustRepository _trust = null!;

	public TrustRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();
		_trust = new TrustRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("TRUNCATE TABLE trust_policies, trust_bundles RESTART IDENTITY CASCADE", connection);
		await command.ExecuteNonQueryAsync();
	}

	private async Task<TrustBundle> UploadValidAsync(string commonName = "ca.example.internal", string label = "Lab CA", string actor = "admin@example.internal")
	{
		string pem = InventedCertificateFactory.CreateSelfSignedPem(commonName);
		TrustBundleValidationResult validated = TrustBundleValidator.Validate(label, pem, DateTimeOffset.UtcNow);
		Assert.True(validated.IsValid);
		return await _trust.CreateAsync(
			validated.Label!, validated.PemChain!, validated.Subject!, validated.Issuer!, validated.FingerprintSha256!,
			validated.NotBefore!.Value, validated.NotAfter!.Value, actor, supersedesId: null, CancellationToken.None);
	}

	[Fact]
	public async Task CreateAsync_persists_a_bundle_and_it_is_listed_active()
	{
		TrustBundle created = await UploadValidAsync();

		TrustBundle? fetched = await _trust.GetAsync(created.Id, CancellationToken.None);
		Assert.NotNull(fetched);
		Assert.Equal(TrustBundleStatuses.Active, fetched!.Status);

		IReadOnlyList<TrustBundle> all = await _trust.ListAsync(CancellationToken.None);
		Assert.Contains(all, b => b.Id == created.Id);
	}

	[Fact]
	public async Task FindActiveByFingerprintAsync_detects_a_duplicate()
	{
		TrustBundle created = await UploadValidAsync();

		TrustBundle? duplicate = await _trust.FindActiveByFingerprintAsync(created.FingerprintSha256, CancellationToken.None);

		Assert.NotNull(duplicate);
		Assert.Equal(created.Id, duplicate!.Id);
	}

	[Fact]
	public async Task FindActiveByFingerprintAsync_ignores_a_superseded_bundle()
	{
		TrustBundle original = await UploadValidAsync("ca.example.internal");
		string replacementPem = InventedCertificateFactory.CreateSelfSignedPem("replacement.example.internal");
		TrustBundleValidationResult validated = TrustBundleValidator.Validate("Replacement", replacementPem, DateTimeOffset.UtcNow);
		await _trust.CreateAsync(
			validated.Label!, validated.PemChain!, validated.Subject!, validated.Issuer!, validated.FingerprintSha256!,
			validated.NotBefore!.Value, validated.NotAfter!.Value, "admin@example.internal", supersedesId: original.Id, CancellationToken.None);

		// The ORIGINAL fingerprint is now superseded -- re-uploading identical material
		// for a DIFFERENT bundle must not see it as a live duplicate.
		TrustBundle? found = await _trust.FindActiveByFingerprintAsync(original.FingerprintSha256, CancellationToken.None);
		Assert.Null(found);
	}

	[Fact]
	public async Task CreateAsync_with_supersedesId_supersedes_the_old_row_without_mutating_its_content()
	{
		TrustBundle original = await UploadValidAsync("ca.example.internal", "Original label");
		string replacementPem = InventedCertificateFactory.CreateSelfSignedPem("replacement.example.internal");
		TrustBundleValidationResult validated = TrustBundleValidator.Validate("Replacement label", replacementPem, DateTimeOffset.UtcNow);

		TrustBundle replacement = await _trust.CreateAsync(
			validated.Label!, validated.PemChain!, validated.Subject!, validated.Issuer!, validated.FingerprintSha256!,
			validated.NotBefore!.Value, validated.NotAfter!.Value, "admin@example.internal", supersedesId: original.Id, CancellationToken.None);

		TrustBundle? reReadOriginal = await _trust.GetAsync(original.Id, CancellationToken.None);
		Assert.NotNull(reReadOriginal);
		Assert.Equal(TrustBundleStatuses.Superseded, reReadOriginal!.Status);
		Assert.Equal(replacement.Id, reReadOriginal.SupersededById);
		Assert.NotNull(reReadOriginal.SupersededAt);
		// Content is UNCHANGED, not mutated to the replacement's -- supersede, don't mutate.
		Assert.Equal("Original label", reReadOriginal.Label);
		Assert.Equal(original.PemChain, reReadOriginal.PemChain);
		Assert.Equal(original.FingerprintSha256, reReadOriginal.FingerprintSha256);

		TrustBundle? reReadReplacement = await _trust.GetAsync(replacement.Id, CancellationToken.None);
		Assert.Equal(TrustBundleStatuses.Active, reReadReplacement!.Status);
	}

	[Fact]
	public async Task DeleteAsync_returns_NotFound_for_an_unknown_id()
	{
		TrustBundleDeleteOutcome outcome = await _trust.DeleteAsync(Guid.NewGuid(), CancellationToken.None);
		Assert.Equal(TrustBundleDeleteOutcome.NotFound, outcome);
	}

	[Fact]
	public async Task DeleteAsync_deletes_an_unreferenced_bundle()
	{
		TrustBundle created = await UploadValidAsync();

		TrustBundleDeleteOutcome outcome = await _trust.DeleteAsync(created.Id, CancellationToken.None);

		Assert.Equal(TrustBundleDeleteOutcome.Deleted, outcome);
		Assert.Null(await _trust.GetAsync(created.Id, CancellationToken.None));
	}

	[Fact]
	public async Task DeleteAsync_is_blocked_while_referenced_by_a_trust_policy()
	{
		TrustBundle bundle = await _trust.CreateAsync(
			"Referenced CA", InventedCertificateFactory.CreateSelfSignedPem("referenced.example.internal"),
			"CN=referenced.example.internal", "CN=referenced.example.internal", RandomFingerprint(),
			DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365), "admin@example.internal", null, CancellationToken.None);

		(TrustPolicyWriteOutcome writeOutcome, TrustPolicy? policy) = await _trust.SetPolicyAsync(
			TrustScopeTypes.Target, "target-1", TrustPolicyModes.Bundle, bundle.Id, null, "admin@example.internal", CancellationToken.None);
		Assert.Equal(TrustPolicyWriteOutcome.Written, writeOutcome);
		Assert.NotNull(policy);

		TrustBundleDeleteOutcome deleteOutcome = await _trust.DeleteAsync(bundle.Id, CancellationToken.None);

		Assert.Equal(TrustBundleDeleteOutcome.Referenced, deleteOutcome);
		Assert.NotNull(await _trust.GetAsync(bundle.Id, CancellationToken.None));
	}

	[Fact]
	public async Task SetPolicyAsync_bundle_mode_requires_an_active_bundle()
	{
		(TrustPolicyWriteOutcome outcome, TrustPolicy? policy) = await _trust.SetPolicyAsync(
			TrustScopeTypes.Target, "target-missing", TrustPolicyModes.Bundle, Guid.NewGuid(), null, "admin@example.internal", CancellationToken.None);

		Assert.Equal(TrustPolicyWriteOutcome.TrustBundleNotFound, outcome);
		Assert.Null(policy);
	}

	[Fact]
	public async Task SetPolicyAsync_rejects_binding_a_new_policy_to_an_already_superseded_bundle()
	{
		TrustBundle original = await UploadValidAsync("ca.example.internal");
		string replacementPem = InventedCertificateFactory.CreateSelfSignedPem("replacement2.example.internal");
		TrustBundleValidationResult validated = TrustBundleValidator.Validate("Replacement", replacementPem, DateTimeOffset.UtcNow);
		await _trust.CreateAsync(
			validated.Label!, validated.PemChain!, validated.Subject!, validated.Issuer!, validated.FingerprintSha256!,
			validated.NotBefore!.Value, validated.NotAfter!.Value, "admin@example.internal", supersedesId: original.Id, CancellationToken.None);

		(TrustPolicyWriteOutcome outcome, TrustPolicy? policy) = await _trust.SetPolicyAsync(
			TrustScopeTypes.Target, "target-2", TrustPolicyModes.Bundle, original.Id, null, "admin@example.internal", CancellationToken.None);

		Assert.Equal(TrustPolicyWriteOutcome.TrustBundleSuperseded, outcome);
		Assert.Null(policy);
	}

	[Fact]
	public async Task SetPolicyAsync_bypass_mode_persists_the_reason_and_is_current()
	{
		(TrustPolicyWriteOutcome outcome, TrustPolicy? policy) = await _trust.SetPolicyAsync(
			TrustScopeTypes.Target, "target-lab-1", TrustPolicyModes.Bypass, null, "Lab appliance with a self-signed cert we cannot re-issue", "admin@example.internal", CancellationToken.None);

		Assert.Equal(TrustPolicyWriteOutcome.Written, outcome);
		Assert.NotNull(policy);
		Assert.Equal(TrustPolicyModes.Bypass, policy!.Mode);
		Assert.Equal("Lab appliance with a self-signed cert we cannot re-issue", policy.BypassReason);
		Assert.Equal(TrustPolicyStatuses.Current, policy.Status);
		Assert.Null(policy.TrustBundleId);
	}

	[Fact]
	public async Task SetPolicyAsync_supersedes_the_prior_current_policy_for_the_same_scope()
	{
		(_, TrustPolicy? first) = await _trust.SetPolicyAsync(
			TrustScopeTypes.Target, "target-scoped", TrustPolicyModes.Bypass, null, "initial reason", "admin@example.internal", CancellationToken.None);

		(TrustPolicyWriteOutcome outcome, TrustPolicy? second) = await _trust.SetPolicyAsync(
			TrustScopeTypes.Target, "target-scoped", TrustPolicyModes.Bypass, null, "updated reason", "admin@example.internal", CancellationToken.None);

		Assert.Equal(TrustPolicyWriteOutcome.Written, outcome);

		TrustPolicy? reReadFirst = await _trust.GetPolicyAsync(first!.Id, CancellationToken.None);
		Assert.Equal(TrustPolicyStatuses.Superseded, reReadFirst!.Status);
		Assert.NotNull(reReadFirst.SupersededAt);

		TrustPolicy? current = await _trust.GetCurrentPolicyAsync(TrustScopeTypes.Target, "target-scoped", CancellationToken.None);
		Assert.NotNull(current);
		Assert.Equal(second!.Id, current!.Id);
		Assert.Equal("updated reason", current.BypassReason);
	}

	[Fact]
	public async Task SetPolicyAsync_scopes_independently_two_different_scope_ids_do_not_interfere()
	{
		await _trust.SetPolicyAsync(TrustScopeTypes.Target, "target-A", TrustPolicyModes.Bypass, null, "reason A", "admin@example.internal", CancellationToken.None);
		await _trust.SetPolicyAsync(TrustScopeTypes.Target, "target-B", TrustPolicyModes.Bypass, null, "reason B", "admin@example.internal", CancellationToken.None);

		TrustPolicy? policyA = await _trust.GetCurrentPolicyAsync(TrustScopeTypes.Target, "target-A", CancellationToken.None);
		TrustPolicy? policyB = await _trust.GetCurrentPolicyAsync(TrustScopeTypes.Target, "target-B", CancellationToken.None);

		Assert.NotNull(policyA);
		Assert.NotNull(policyB);
		Assert.Equal("reason A", policyA!.BypassReason);
		Assert.Equal("reason B", policyB!.BypassReason);
	}

	[Fact]
	public async Task SetPolicyAsync_scopes_independently_same_scope_id_different_scope_type_do_not_interfere()
	{
		// Same scope_id string, different scope_type -- the composite key must isolate
		// these, not collide on the id half alone.
		await _trust.SetPolicyAsync(TrustScopeTypes.Target, "shared-id", TrustPolicyModes.Bypass, null, "target reason", "admin@example.internal", CancellationToken.None);
		await _trust.SetPolicyAsync(TrustScopeTypes.StigManagerGlobal, "shared-id", TrustPolicyModes.Bypass, null, "stigman reason", "admin@example.internal", CancellationToken.None);

		TrustPolicy? targetPolicy = await _trust.GetCurrentPolicyAsync(TrustScopeTypes.Target, "shared-id", CancellationToken.None);
		TrustPolicy? stigmanPolicy = await _trust.GetCurrentPolicyAsync(TrustScopeTypes.StigManagerGlobal, "shared-id", CancellationToken.None);

		Assert.Equal("target reason", targetPolicy!.BypassReason);
		Assert.Equal("stigman reason", stigmanPolicy!.BypassReason);
	}

	[Fact]
	public async Task ListPoliciesAsync_returns_every_policy_current_and_superseded()
	{
		(_, TrustPolicy? first) = await _trust.SetPolicyAsync(
			TrustScopeTypes.Target, "target-list", TrustPolicyModes.Bypass, null, "first", "admin@example.internal", CancellationToken.None);
		(_, TrustPolicy? second) = await _trust.SetPolicyAsync(
			TrustScopeTypes.Target, "target-list", TrustPolicyModes.Bypass, null, "second", "admin@example.internal", CancellationToken.None);

		IReadOnlyList<TrustPolicy> all = await _trust.ListPoliciesAsync(CancellationToken.None);

		Assert.Contains(all, p => p.Id == first!.Id && p.Status == TrustPolicyStatuses.Superseded);
		Assert.Contains(all, p => p.Id == second!.Id && p.Status == TrustPolicyStatuses.Current);
	}

	private static string RandomFingerprint() => Convert.ToHexString(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).Take(32).ToArray()).ToLowerInvariant();
}
