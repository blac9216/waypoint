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
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Sites;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #584 (epic #582, ADR-0021 docs/adr/0021-credential-purpose-matrix.md) against
/// real Postgres: <see cref="TargetCredentialBindingRepository"/>'s CRUD, purpose/type
/// validation, uniqueness, and the dual-write mirror into the legacy
/// <c>targets.credential_id</c> column (migration 0043's documented contract). Also
/// covers migration 0043's one-time data-migration of existing
/// <c>targets.credential_id</c> rows into the kind-appropriate default-purpose binding.
/// </summary>
[Collection("Postgres")]
public sealed class TargetCredentialBindingRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private TargetRepository _targets = null!;
	private TargetCredentialBindingRepository _bindings = null!;
	private Guid _siteId;

	public TargetCredentialBindingRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();

		_targets = new TargetRepository(_fixture.ConnectionString);
		_bindings = new TargetCredentialBindingRepository(_fixture.ConnectionString);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insertSite = new("INSERT INTO sites (name) VALUES ($1) RETURNING id", connection);
		insertSite.Parameters.AddWithValue($"binding-test-site-{Guid.NewGuid():N}");
		_siteId = (Guid)(await insertSite.ExecuteScalarAsync())!;
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task SetAsync_ApplicablePurposeAndCompatibleType_Succeeds()
	{
		Guid credentialId = await SeedCredentialAsync("vcenter");
		Guid targetId = await CreateTargetAsync(TargetKinds.VSphere, "vcsa-01");

		TargetCredentialBindingWriteOutcome outcome = await _bindings.SetAsync(
			targetId, CredentialPurposes.VSphereApi, credentialId, CancellationToken.None);

		Assert.Equal(TargetCredentialBindingWriteOutcome.Ok, outcome);
		TargetCredentialBinding binding = Assert.Single(await _bindings.ListForTargetAsync(targetId, CancellationToken.None));
		Assert.Equal(CredentialPurposes.VSphereApi, binding.Purpose);
		Assert.Equal(credentialId, binding.CredentialId);
	}

	[Fact]
	public async Task SetAsync_VSphereTarget_CanCarryBothVSphereApiAndVcsaSshAtOnce()
	{
		Guid vcenterCredentialId = await SeedCredentialAsync("vcenter");
		Guid sshCredentialId = await SeedCredentialAsync("ssh");
		Guid targetId = await CreateTargetAsync(TargetKinds.VSphere, "vcsa-02");

		Assert.Equal(TargetCredentialBindingWriteOutcome.Ok, await _bindings.SetAsync(targetId, CredentialPurposes.VSphereApi, vcenterCredentialId, CancellationToken.None));
		Assert.Equal(TargetCredentialBindingWriteOutcome.Ok, await _bindings.SetAsync(targetId, CredentialPurposes.VcsaSsh, sshCredentialId, CancellationToken.None));

		IReadOnlyList<TargetCredentialBinding> bindings = await _bindings.ListForTargetAsync(targetId, CancellationToken.None);
		Assert.Equal(2, bindings.Count);
		Assert.Contains(bindings, b => b.Purpose == CredentialPurposes.VSphereApi && b.CredentialId == vcenterCredentialId);
		Assert.Contains(bindings, b => b.Purpose == CredentialPurposes.VcsaSsh && b.CredentialId == sshCredentialId);
	}

	[Fact]
	public async Task SetAsync_SamePurposeTwice_OverridesRatherThanDuplicating()
	{
		Guid firstCredentialId = await SeedCredentialAsync("vcenter");
		Guid secondCredentialId = await SeedCredentialAsync("vcenter");
		Guid targetId = await CreateTargetAsync(TargetKinds.VSphere, "override-target");

		await _bindings.SetAsync(targetId, CredentialPurposes.VSphereApi, firstCredentialId, CancellationToken.None);
		TargetCredentialBindingWriteOutcome overrideOutcome = await _bindings.SetAsync(targetId, CredentialPurposes.VSphereApi, secondCredentialId, CancellationToken.None);

		Assert.Equal(TargetCredentialBindingWriteOutcome.Ok, overrideOutcome);
		TargetCredentialBinding binding = Assert.Single(await _bindings.ListForTargetAsync(targetId, CancellationToken.None));
		Assert.Equal(secondCredentialId, binding.CredentialId);
	}

	[Fact]
	public async Task SetAsync_InapplicablePurposeForTargetKind_IsRejected()
	{
		Guid credentialId = await SeedCredentialAsync("nsx");
		Guid targetId = await CreateTargetAsync(TargetKinds.Ssh, "srg-box");

		// nsx-api is not an applicable purpose for a `ssh` (SRG) target.
		TargetCredentialBindingWriteOutcome outcome = await _bindings.SetAsync(
			targetId, CredentialPurposes.NsxApi, credentialId, CancellationToken.None);

		Assert.Equal(TargetCredentialBindingWriteOutcome.PurposeNotApplicable, outcome);
		Assert.Empty(await _bindings.ListForTargetAsync(targetId, CancellationToken.None));
	}

	[Fact]
	public async Task SetAsync_UnknownPurpose_IsRejected()
	{
		Guid credentialId = await SeedCredentialAsync("vcenter");
		Guid targetId = await CreateTargetAsync(TargetKinds.VSphere, "unknown-purpose-target");

		TargetCredentialBindingWriteOutcome outcome = await _bindings.SetAsync(targetId, "made-up-purpose", credentialId, CancellationToken.None);

		Assert.Equal(TargetCredentialBindingWriteOutcome.InvalidPurpose, outcome);
	}

	[Fact]
	public async Task SetAsync_IncompatibleCredentialType_IsRejected()
	{
		// vsphere-api requires a `vcenter`-type credential -- an `nsx` credential must
		// never be accepted (ADR-0021 SS2/SS4: "an nsx credential can never be offered
		// as an override for vcsa-ssh"; same rule applies here for vsphere-api).
		Guid nsxCredentialId = await SeedCredentialAsync("nsx");
		Guid targetId = await CreateTargetAsync(TargetKinds.VSphere, "incompatible-type-target");

		TargetCredentialBindingWriteOutcome outcome = await _bindings.SetAsync(
			targetId, CredentialPurposes.VSphereApi, nsxCredentialId, CancellationToken.None);

		Assert.Equal(TargetCredentialBindingWriteOutcome.IncompatibleCredentialType, outcome);
		Assert.Empty(await _bindings.ListForTargetAsync(targetId, CancellationToken.None));
	}

	[Fact]
	public async Task SetAsync_UnknownTarget_ReturnsTargetNotFound()
	{
		Guid credentialId = await SeedCredentialAsync("vcenter");

		TargetCredentialBindingWriteOutcome outcome = await _bindings.SetAsync(
			Guid.NewGuid(), CredentialPurposes.VSphereApi, credentialId, CancellationToken.None);

		Assert.Equal(TargetCredentialBindingWriteOutcome.TargetNotFound, outcome);
	}

	[Fact]
	public async Task SetAsync_UnknownCredential_ReturnsCredentialNotFound()
	{
		Guid targetId = await CreateTargetAsync(TargetKinds.VSphere, "unknown-cred-target");

		TargetCredentialBindingWriteOutcome outcome = await _bindings.SetAsync(
			targetId, CredentialPurposes.VSphereApi, Guid.NewGuid(), CancellationToken.None);

		Assert.Equal(TargetCredentialBindingWriteOutcome.CredentialNotFound, outcome);
	}

	[Fact]
	public async Task ClearAsync_RemovesTheBinding()
	{
		Guid credentialId = await SeedCredentialAsync("nsx");
		Guid targetId = await CreateTargetAsync(TargetKinds.NsxApi, "clear-target");
		await _bindings.SetAsync(targetId, CredentialPurposes.NsxApi, credentialId, CancellationToken.None);

		TargetCredentialBindingDeleteOutcome outcome = await _bindings.ClearAsync(targetId, CredentialPurposes.NsxApi, CancellationToken.None);

		Assert.Equal(TargetCredentialBindingDeleteOutcome.Deleted, outcome);
		Assert.Empty(await _bindings.ListForTargetAsync(targetId, CancellationToken.None));
	}

	[Fact]
	public async Task ClearAsync_NoExistingBinding_ReturnsNotFound()
	{
		Guid targetId = await CreateTargetAsync(TargetKinds.NsxApi, "clear-missing-target");

		TargetCredentialBindingDeleteOutcome outcome = await _bindings.ClearAsync(targetId, CredentialPurposes.NsxApi, CancellationToken.None);

		Assert.Equal(TargetCredentialBindingDeleteOutcome.NotFound, outcome);
	}

	// --- Dual-write contract (migration 0043) ---------------------------------------

	[Fact]
	public async Task SetAsync_DefaultPurposeBinding_MirrorsIntoLegacyCredentialId()
	{
		Guid credentialId = await SeedCredentialAsync("nsx");
		Guid targetId = await CreateTargetAsync(TargetKinds.NsxApi, "mirror-target");

		await _bindings.SetAsync(targetId, CredentialPurposes.NsxApi, credentialId, CancellationToken.None);

		Target? target = await _targets.GetAsync(targetId, CancellationToken.None);
		Assert.Equal(credentialId, target!.CredentialId);
	}

	[Fact]
	public async Task SetAsync_NonDefaultPurposeBinding_DoesNotTouchLegacyCredentialId()
	{
		Guid vcenterCredentialId = await SeedCredentialAsync("vcenter");
		Guid sshCredentialId = await SeedCredentialAsync("ssh");
		Guid targetId = await CreateTargetAsync(TargetKinds.VSphere, "non-default-mirror-target");

		// Set the default purpose first so credential_id has a known value...
		await _bindings.SetAsync(targetId, CredentialPurposes.VSphereApi, vcenterCredentialId, CancellationToken.None);
		// ...then set the NON-default purpose (vcsa-ssh) and confirm credential_id is unchanged.
		await _bindings.SetAsync(targetId, CredentialPurposes.VcsaSsh, sshCredentialId, CancellationToken.None);

		Target? target = await _targets.GetAsync(targetId, CancellationToken.None);
		Assert.Equal(vcenterCredentialId, target!.CredentialId);
	}

	[Fact]
	public async Task ClearAsync_DefaultPurposeBinding_ClearsLegacyCredentialId()
	{
		Guid credentialId = await SeedCredentialAsync("nsx");
		Guid targetId = await CreateTargetAsync(TargetKinds.NsxApi, "clear-mirror-target");
		await _bindings.SetAsync(targetId, CredentialPurposes.NsxApi, credentialId, CancellationToken.None);

		await _bindings.ClearAsync(targetId, CredentialPurposes.NsxApi, CancellationToken.None);

		Target? target = await _targets.GetAsync(targetId, CancellationToken.None);
		Assert.Null(target!.CredentialId);
	}

	[Fact]
	public async Task CreateAsync_WithLegacyCredentialRef_MirrorsIntoDefaultPurposeBinding()
	{
		Guid credentialId = await SeedCredentialAsync("vcenter");

		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			_siteId, TargetKinds.VSphere, $"legacy-create-{Guid.NewGuid():N}", "{}", credentialId, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);

		TargetCredentialBinding binding = Assert.Single(await _bindings.ListForTargetAsync(targetId!.Value, CancellationToken.None));
		Assert.Equal(CredentialPurposes.VSphereApi, binding.Purpose);
		Assert.Equal(credentialId, binding.CredentialId);
	}

	[Fact]
	public async Task UpdateAsync_ClearingLegacyCredentialRef_RemovesTheDefaultPurposeBinding()
	{
		Guid credentialId = await SeedCredentialAsync("nsx");
		(TargetWriteOutcome created, Guid? targetId) = await _targets.CreateAsync(
			_siteId, TargetKinds.NsxApi, $"legacy-clear-{Guid.NewGuid():N}", "{}", credentialId, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, created);
		Assert.Single(await _bindings.ListForTargetAsync(targetId!.Value, CancellationToken.None));

		TargetWriteOutcome updated = await _targets.UpdateAsync(
			targetId.Value, null, null, null, null, clearCredential: true, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, updated);

		Assert.Empty(await _bindings.ListForTargetAsync(targetId.Value, CancellationToken.None));
	}

	/// <summary>
	/// Issue #584 scope note: the legacy write path has never validated credential
	/// type (an operator could always point `credential_ref` at a mismatched-type
	/// credential). The dual-write mirror must preserve that -- a type-incompatible
	/// legacy write still succeeds (behavior-unchanged) but silently produces NO
	/// binding row, rather than rejecting the legacy write or writing an invalid
	/// binding the compatibility matrix would itself reject.
	/// </summary>
	[Fact]
	public async Task CreateAsync_WithLegacyCredentialRef_IncompatibleType_SucceedsWithoutMirroring()
	{
		Guid tokenCredentialId = await SeedCredentialAsync("token");

		(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
			_siteId, TargetKinds.VSphere, $"legacy-incompatible-{Guid.NewGuid():N}", "{}", tokenCredentialId, CancellationToken.None);

		Assert.Equal(TargetWriteOutcome.Ok, outcome);
		Target? target = await _targets.GetAsync(targetId!.Value, CancellationToken.None);
		Assert.Equal(tokenCredentialId, target!.CredentialId);
		Assert.Empty(await _bindings.ListForTargetAsync(targetId.Value, CancellationToken.None));
	}

	// --- Migration 0043 data-migration (existing refs -> default purpose) -----------

	[Fact]
	public async Task Migration0043_BackfillsVSphereLegacyCredentialIntoVSphereApiPurpose()
	{
		Guid credentialId = await SeedCredentialAsync("vcenter");
		Guid targetId = await InsertLegacyTargetDirectlyAsync(TargetKinds.VSphere, credentialId);

		// Re-run migration 0043 specifically (its tracking row removed) to prove the
		// backfill INSERT runs even against a target row that predates the binding
		// table -- the exact shape a real upgrade encounters. A bare ApplyAsync()
		// alone would be a no-op here since 0043 already ran in InitializeAsync().
		await ReapplyMigration0043Async();

		TargetCredentialBinding binding = Assert.Single(await _bindings.ListForTargetAsync(targetId, CancellationToken.None));
		Assert.Equal(CredentialPurposes.VSphereApi, binding.Purpose);
		Assert.Equal(credentialId, binding.CredentialId);
	}

	[Fact]
	public async Task Migration0043_BackfillsNsxLegacyCredentialIntoNsxApiPurpose()
	{
		Guid credentialId = await SeedCredentialAsync("nsx");
		Guid targetId = await InsertLegacyTargetDirectlyAsync(TargetKinds.NsxApi, credentialId);

		await ReapplyMigration0043Async();

		TargetCredentialBinding binding = Assert.Single(await _bindings.ListForTargetAsync(targetId, CancellationToken.None));
		Assert.Equal(CredentialPurposes.NsxApi, binding.Purpose);
	}

	[Fact]
	public async Task Migration0043_BackfillsSshLegacyCredentialIntoSrgSshPurpose()
	{
		Guid credentialId = await SeedCredentialAsync("ssh");
		Guid targetId = await InsertLegacyTargetDirectlyAsync(TargetKinds.Ssh, credentialId);

		await ReapplyMigration0043Async();

		TargetCredentialBinding binding = Assert.Single(await _bindings.ListForTargetAsync(targetId, CancellationToken.None));
		Assert.Equal(CredentialPurposes.SrgSsh, binding.Purpose);
	}

	[Fact]
	public async Task Migration0043_DoesNotBackfillATypeIncompatibleLegacyReference()
	{
		Guid tokenCredentialId = await SeedCredentialAsync("token");
		Guid targetId = await InsertLegacyTargetDirectlyAsync(TargetKinds.VSphere, tokenCredentialId);

		await ReapplyMigration0043Async();

		Assert.Empty(await _bindings.ListForTargetAsync(targetId, CancellationToken.None));
	}

	/// <summary>
	/// Re-runs migration 0043 specifically by deleting its <c>schema_migrations</c>
	/// tracking row and calling <see cref="NpgsqlSchemaMigrator.ApplyAsync"/> again --
	/// a bare re-call is normally a no-op (every migration is applied exactly once,
	/// tracked by version). This lets a test insert a "pre-#584" target row directly
	/// (<see cref="InsertLegacyTargetDirectlyAsync"/>) and then observe 0043's
	/// backfill INSERTs run against it, the same shape a real upgrade of an
	/// already-populated database encounters. 0043's CREATE TABLE/backfill INSERTs are
	/// idempotent (IF NOT EXISTS / ON CONFLICT DO NOTHING), so this is safe to run
	/// against a database where the table already exists from <see cref="InitializeAsync"/>.
	/// </summary>
	private async Task ReapplyMigration0043Async()
	{
		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync();
			await using NpgsqlCommand delete = new("DELETE FROM schema_migrations WHERE version = '0043_target_credential_bindings'", connection);
			await delete.ExecuteNonQueryAsync();
		}

		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
	}

	private async Task<Guid> CreateTargetAsync(string kind, string namePrefix)
	{
		(TargetWriteOutcome outcome, Guid? id) = await _targets.CreateAsync(
			_siteId, kind, $"{namePrefix}-{Guid.NewGuid():N}", "{}", null, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, outcome);
		return id!.Value;
	}

	/// <summary>Inserts a target row with a legacy credential_id directly via SQL, bypassing TargetRepository's dual-write -- simulates a pre-#584 row for the migration backfill tests.</summary>
	private async Task<Guid> InsertLegacyTargetDirectlyAsync(string kind, Guid credentialId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO targets (site_id, kind, name, connection, credential_id)
			VALUES ($1, $2, $3, '{}'::jsonb, $4)
			RETURNING id
			""", connection);
		insert.Parameters.AddWithValue(_siteId);
		insert.Parameters.AddWithValue(kind);
		insert.Parameters.AddWithValue($"legacy-{Guid.NewGuid():N}");
		insert.Parameters.AddWithValue(credentialId);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task<Guid> SeedCredentialAsync(string credentialType)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO credentials (name, credential_type, owner) VALUES ($1, $2, 'shared') RETURNING id", connection);
		insert.Parameters.AddWithValue($"binding-test-cred-{Guid.NewGuid():N}");
		insert.Parameters.AddWithValue(credentialType);
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand truncate = new(
			"TRUNCATE TABLE target_credential_bindings, targets, sites, downloads, credential_secrets, credentials RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync().ConfigureAwait(false);
	}
}
