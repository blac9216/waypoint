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

using Npgsql;
using Waypoint.Core.ComplianceContent;

namespace Waypoint.Infrastructure.ComplianceContent;

/// <inheritdoc cref="ICatalogRepository"/>
public sealed class CatalogRepository : ICatalogRepository
{
	private const string ExecutionProfileDetailProjectionSql = """
		SELECT
			ep.id, ep.component_id, ep.content_release_id, ep.report_group_id, ep.profile_version, ep.is_operator_override, ep.output_kind, ep.created_at,
			c.id, c.product_version_id, c.parent_component_id, c.component_key, c.display_name, c.transport, c.selector_kind, c.selector_name, c.created_at,
			pv.id, pv.product_id, pv.version_key, pv.display_name, pv.created_at,
			p.id, p.source_revision_id, p.vendor, p.product_key, p.display_name, p.created_at,
			cr.id, cr.source_revision_id, cr.kind, cr.release_key, cr.display_name, cr.created_at,
			rg.id, rg.group_key, rg.display_name, rg.priority, rg.created_at
		FROM catalog_execution_profiles ep
		JOIN catalog_components c ON c.id = ep.component_id
		JOIN catalog_product_versions pv ON pv.id = c.product_version_id
		JOIN catalog_products p ON p.id = pv.product_id
		JOIN catalog_content_releases cr ON cr.id = ep.content_release_id
		JOIN catalog_report_groups rg ON rg.id = ep.report_group_id
		""";

	private readonly string _connectionString;

	public CatalogRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<CatalogSourceRevision> UpsertSourceRevisionAsync(string revisionKey, string? description, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_source_revisions (revision_key, description)
			VALUES ($1, $2)
			ON CONFLICT (revision_key) DO UPDATE SET description = EXCLUDED.description
			RETURNING id, revision_key, description, recorded_at
			""", connection);
		command.Parameters.AddWithValue(revisionKey);
		command.Parameters.AddWithValue((object?)description ?? DBNull.Value);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return new CatalogSourceRevision(
			reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3));
	}

	public async Task<CatalogProduct> UpsertProductAsync(Guid sourceRevisionId, string vendor, string productKey, string displayName, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_products (source_revision_id, vendor, product_key, display_name)
			VALUES ($1, $2, $3, $4)
			ON CONFLICT (vendor, product_key) DO UPDATE SET display_name = EXCLUDED.display_name, source_revision_id = EXCLUDED.source_revision_id
			RETURNING id, source_revision_id, vendor, product_key, display_name, created_at
			""", connection);
		command.Parameters.AddWithValue(sourceRevisionId);
		command.Parameters.AddWithValue(vendor);
		command.Parameters.AddWithValue(productKey);
		command.Parameters.AddWithValue(displayName);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return new CatalogProduct(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5));
	}

	public async Task<CatalogProductVersion> UpsertProductVersionAsync(Guid productId, string versionKey, string displayName, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_product_versions (product_id, version_key, display_name)
			VALUES ($1, $2, $3)
			ON CONFLICT (product_id, version_key) DO UPDATE SET display_name = EXCLUDED.display_name
			RETURNING id, product_id, version_key, display_name, created_at
			""", connection);
		command.Parameters.AddWithValue(productId);
		command.Parameters.AddWithValue(versionKey);
		command.Parameters.AddWithValue(displayName);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return new CatalogProductVersion(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4));
	}

	public async Task<CatalogContentRelease> UpsertContentReleaseAsync(
		Guid sourceRevisionId, string kind, string releaseKey, string displayName, CancellationToken cancellationToken)
	{
		IReadOnlyList<string> kindErrors = CatalogVocabularyValidator.ValidateKind(kind);
		if (kindErrors.Count > 0)
		{
			throw new ArgumentException(string.Join("; ", kindErrors), nameof(kind));
		}

		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_content_releases (source_revision_id, kind, release_key, display_name)
			VALUES ($1, $2, $3, $4)
			ON CONFLICT (kind, release_key) DO UPDATE SET display_name = EXCLUDED.display_name, source_revision_id = EXCLUDED.source_revision_id
			RETURNING id, source_revision_id, kind, release_key, display_name, created_at
			""", connection);
		command.Parameters.AddWithValue(sourceRevisionId);
		command.Parameters.AddWithValue(kind);
		command.Parameters.AddWithValue(releaseKey);
		command.Parameters.AddWithValue(displayName);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return new CatalogContentRelease(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5));
	}

	public async Task<CatalogComponent> UpsertComponentAsync(Guid productVersionId, CatalogComponentDefinition definition, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(definition);
		IReadOnlyList<string> errors = CatalogVocabularyValidator.ValidateComponent(definition.Transport, definition.SelectorKind, definition.SelectorName);
		if (errors.Count > 0)
		{
			throw new ArgumentException(string.Join("; ", errors), nameof(definition));
		}

		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_components (product_version_id, parent_component_id, component_key, display_name, transport, selector_kind, selector_name)
			VALUES ($1, $2, $3, $4, $5, $6, $7)
			ON CONFLICT (product_version_id, parent_component_id, component_key) DO UPDATE SET
				display_name = EXCLUDED.display_name,
				transport = EXCLUDED.transport,
				selector_kind = EXCLUDED.selector_kind,
				selector_name = EXCLUDED.selector_name
			RETURNING id, product_version_id, parent_component_id, component_key, display_name, transport, selector_kind, selector_name, created_at
			""", connection);
		command.Parameters.AddWithValue(productVersionId);
		command.Parameters.AddWithValue((object?)definition.ParentComponentId ?? DBNull.Value);
		command.Parameters.AddWithValue(definition.ComponentKey);
		command.Parameters.AddWithValue(definition.DisplayName);
		command.Parameters.AddWithValue(definition.Transport);
		command.Parameters.AddWithValue(definition.SelectorKind);
		command.Parameters.AddWithValue((object?)definition.SelectorName ?? DBNull.Value);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return MapComponent(reader, 0);
	}

	public async Task<CatalogReportGroup> UpsertReportGroupAsync(string groupKey, string displayName, int priority, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_report_groups (group_key, display_name, priority)
			VALUES ($1, $2, $3)
			ON CONFLICT (group_key) DO UPDATE SET display_name = EXCLUDED.display_name, priority = EXCLUDED.priority
			RETURNING id, group_key, display_name, priority, created_at
			""", connection);
		command.Parameters.AddWithValue(groupKey);
		command.Parameters.AddWithValue(displayName);
		command.Parameters.AddWithValue(priority);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return new CatalogReportGroup(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetFieldValue<DateTimeOffset>(4));
	}

	public async Task<CatalogExecutionProfile> CreateExecutionProfileAsync(
		Guid componentId, Guid contentReleaseId, Guid reportGroupId, string profileVersion, string outputKind, CancellationToken cancellationToken)
	{
		IReadOnlyList<string> errors = CatalogVocabularyValidator.ValidateOutputKind(outputKind);
		if (errors.Count > 0)
		{
			throw new ArgumentException(string.Join("; ", errors), nameof(outputKind));
		}

		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

		await using (NpgsqlCommand exists = new(
			"SELECT 1 FROM catalog_execution_profiles WHERE component_id = $1 AND content_release_id = $2", connection))
		{
			exists.Parameters.AddWithValue(componentId);
			exists.Parameters.AddWithValue(contentReleaseId);
			object? found = await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (found is not null)
			{
				throw new InvalidOperationException(
					$"an execution profile already exists for component {componentId} and content release {contentReleaseId} -- execution profiles are immutable identity, never updated in place");
			}
		}

		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO catalog_execution_profiles (component_id, content_release_id, report_group_id, profile_version, output_kind)
			VALUES ($1, $2, $3, $4, $5)
			RETURNING id, component_id, content_release_id, report_group_id, profile_version, is_operator_override, output_kind, created_at
			""", connection);
		insert.Parameters.AddWithValue(componentId);
		insert.Parameters.AddWithValue(contentReleaseId);
		insert.Parameters.AddWithValue(reportGroupId);
		insert.Parameters.AddWithValue(profileVersion);
		insert.Parameters.AddWithValue(outputKind);

		await using NpgsqlDataReader reader = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return MapExecutionProfile(reader, 0);
	}

	public async Task<CatalogCredentialRequirement> AddCredentialRequirementAsync(
		Guid executionProfileId, string purpose, bool isRequired, CancellationToken cancellationToken)
	{
		IReadOnlyList<string> errors = CatalogVocabularyValidator.ValidateCredentialPurpose(purpose);
		if (errors.Count > 0)
		{
			throw new ArgumentException(string.Join("; ", errors), nameof(purpose));
		}

		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_credential_requirements (execution_profile_id, purpose, is_required)
			VALUES ($1, $2, $3)
			ON CONFLICT (execution_profile_id, purpose) DO UPDATE SET is_required = EXCLUDED.is_required
			RETURNING id, execution_profile_id, purpose, is_required, created_at
			""", connection);
		command.Parameters.AddWithValue(executionProfileId);
		command.Parameters.AddWithValue(purpose);
		command.Parameters.AddWithValue(isRequired);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return new CatalogCredentialRequirement(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetBoolean(3), reader.GetFieldValue<DateTimeOffset>(4));
	}

	public async Task<CatalogBenchmarkReference> SetBenchmarkReferenceAsync(
		Guid executionProfileId, string benchmarkKey, string benchmarkVersion, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_benchmark_references (execution_profile_id, benchmark_key, benchmark_version)
			VALUES ($1, $2, $3)
			ON CONFLICT (execution_profile_id) DO UPDATE SET benchmark_key = EXCLUDED.benchmark_key, benchmark_version = EXCLUDED.benchmark_version
			RETURNING id, execution_profile_id, benchmark_key, benchmark_version, created_at
			""", connection);
		command.Parameters.AddWithValue(executionProfileId);
		command.Parameters.AddWithValue(benchmarkKey);
		command.Parameters.AddWithValue(benchmarkVersion);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return new CatalogBenchmarkReference(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4));
	}

	public async Task<CatalogRemediationDefinition> SetRemediationDefinitionAsync(
		Guid executionProfileId, bool isSupported, string? mechanismNote, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_remediation_definitions (execution_profile_id, is_supported, mechanism_note)
			VALUES ($1, $2, $3)
			ON CONFLICT (execution_profile_id) DO UPDATE SET is_supported = EXCLUDED.is_supported, mechanism_note = EXCLUDED.mechanism_note
			RETURNING id, execution_profile_id, is_supported, mechanism_note, created_at
			""", connection);
		command.Parameters.AddWithValue(executionProfileId);
		command.Parameters.AddWithValue(isSupported);
		command.Parameters.AddWithValue((object?)mechanismNote ?? DBNull.Value);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return new CatalogRemediationDefinition(reader.GetGuid(0), reader.GetGuid(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4));
	}

	public async Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, source_revision_id, vendor, product_key, display_name, created_at
			FROM catalog_products
			ORDER BY vendor, product_key
			""", connection);

		List<CatalogProduct> products = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			products.Add(new CatalogProduct(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5)));
		}

		return products;
	}

	public async Task<IReadOnlyList<CatalogProductVersion>> ListProductVersionsAsync(Guid productId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, product_id, version_key, display_name, created_at
			FROM catalog_product_versions
			WHERE product_id = $1
			ORDER BY version_key
			""", connection);
		command.Parameters.AddWithValue(productId);

		List<CatalogProductVersion> versions = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			versions.Add(new CatalogProductVersion(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
		}

		return versions;
	}

	public async Task<IReadOnlyList<CatalogComponent>> ListComponentsAsync(Guid productVersionId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, product_version_id, parent_component_id, component_key, display_name, transport, selector_kind, selector_name, created_at
			FROM catalog_components
			WHERE product_version_id = $1
			ORDER BY component_key
			""", connection);
		command.Parameters.AddWithValue(productVersionId);

		List<CatalogComponent> components = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			components.Add(MapComponent(reader, 0));
		}

		return components;
	}

	public async Task<IReadOnlyList<CatalogExecutionProfileDetail>> ListExecutionProfilesByComponentAsync(Guid componentId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			ExecutionProfileDetailProjectionSql + " WHERE ep.component_id = $1 ORDER BY cr.release_key", connection);
		command.Parameters.AddWithValue(componentId);

		List<CatalogExecutionProfile> profiles = [];
		Dictionary<Guid, (CatalogComponent Component, CatalogProductVersion ProductVersion, CatalogProduct Product, CatalogContentRelease ContentRelease, CatalogReportGroup ReportGroup)> joined = [];
		await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				CatalogExecutionProfile profile = MapExecutionProfile(reader, 0);
				profiles.Add(profile);
				joined[profile.Id] = (MapComponent(reader, 8), MapProductVersion(reader, 17), MapProduct(reader, 22), MapContentRelease(reader, 28), MapReportGroup(reader, 34));
			}
		}

		return await HydrateDetailsAsync(connection, profiles, joined, cancellationToken).ConfigureAwait(false);
	}

	public async Task<CatalogExecutionProfileDetail?> GetExecutionProfileAsync(Guid executionProfileId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(ExecutionProfileDetailProjectionSql + " WHERE ep.id = $1", connection);
		command.Parameters.AddWithValue(executionProfileId);

		CatalogExecutionProfile profile;
		(CatalogComponent Component, CatalogProductVersion ProductVersion, CatalogProduct Product, CatalogContentRelease ContentRelease, CatalogReportGroup ReportGroup) joined;
		await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				return null;
			}

			profile = MapExecutionProfile(reader, 0);
			joined = (MapComponent(reader, 8), MapProductVersion(reader, 17), MapProduct(reader, 22), MapContentRelease(reader, 28), MapReportGroup(reader, 34));
		}

		IReadOnlyList<CatalogExecutionProfileDetail> details = await HydrateDetailsAsync(
			connection, [profile], new Dictionary<Guid, (CatalogComponent, CatalogProductVersion, CatalogProduct, CatalogContentRelease, CatalogReportGroup)> { [profile.Id] = joined },
			cancellationToken).ConfigureAwait(false);
		return details.Count == 0 ? null : details[0];
	}

	public async Task<IReadOnlyList<CatalogExecutionProfileDetail>> ListAllExecutionProfilesAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(ExecutionProfileDetailProjectionSql + " ORDER BY p.vendor, p.product_key, pv.version_key, c.component_key, cr.release_key", connection);

		List<CatalogExecutionProfile> profiles = [];
		Dictionary<Guid, (CatalogComponent Component, CatalogProductVersion ProductVersion, CatalogProduct Product, CatalogContentRelease ContentRelease, CatalogReportGroup ReportGroup)> joined = [];
		await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				CatalogExecutionProfile profile = MapExecutionProfile(reader, 0);
				profiles.Add(profile);
				joined[profile.Id] = (MapComponent(reader, 8), MapProductVersion(reader, 17), MapProduct(reader, 22), MapContentRelease(reader, 28), MapReportGroup(reader, 34));
			}
		}

		return await HydrateDetailsAsync(connection, profiles, joined, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Attaches credential requirements, the optional benchmark reference, and the
	/// optional remediation definition to each already-joined execution profile. Kept
	/// as a second pass (rather than a bigger single query) because credential
	/// requirements are one-to-many and would otherwise duplicate the joined
	/// component/product/release columns per requirement row.
	/// </summary>
	private static async Task<IReadOnlyList<CatalogExecutionProfileDetail>> HydrateDetailsAsync(
		NpgsqlConnection connection,
		List<CatalogExecutionProfile> profiles,
		Dictionary<Guid, (CatalogComponent Component, CatalogProductVersion ProductVersion, CatalogProduct Product, CatalogContentRelease ContentRelease, CatalogReportGroup ReportGroup)> joined,
		CancellationToken cancellationToken)
	{
		if (profiles.Count == 0)
		{
			return [];
		}

		Guid[] ids = [.. profiles.Select(p => p.Id)];

		Dictionary<Guid, List<CatalogCredentialRequirement>> requirementsByProfile = [];
		await using (NpgsqlCommand command = new(
			"""
			SELECT id, execution_profile_id, purpose, is_required, created_at
			FROM catalog_credential_requirements
			WHERE execution_profile_id = ANY($1)
			ORDER BY purpose
			""", connection))
		{
			command.Parameters.Add(new NpgsqlParameter { Value = ids, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid });
			await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				CatalogCredentialRequirement requirement = new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetBoolean(3), reader.GetFieldValue<DateTimeOffset>(4));
				if (!requirementsByProfile.TryGetValue(requirement.ExecutionProfileId, out List<CatalogCredentialRequirement>? list))
				{
					list = [];
					requirementsByProfile[requirement.ExecutionProfileId] = list;
				}

				list.Add(requirement);
			}
		}

		Dictionary<Guid, CatalogBenchmarkReference> benchmarksByProfile = [];
		await using (NpgsqlCommand command = new(
			"""
			SELECT id, execution_profile_id, benchmark_key, benchmark_version, created_at
			FROM catalog_benchmark_references
			WHERE execution_profile_id = ANY($1)
			""", connection))
		{
			command.Parameters.Add(new NpgsqlParameter { Value = ids, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid });
			await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				CatalogBenchmarkReference benchmark = new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4));
				benchmarksByProfile[benchmark.ExecutionProfileId] = benchmark;
			}
		}

		Dictionary<Guid, CatalogRemediationDefinition> remediationByProfile = [];
		await using (NpgsqlCommand command = new(
			"""
			SELECT id, execution_profile_id, is_supported, mechanism_note, created_at
			FROM catalog_remediation_definitions
			WHERE execution_profile_id = ANY($1)
			""", connection))
		{
			command.Parameters.Add(new NpgsqlParameter { Value = ids, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid });
			await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				CatalogRemediationDefinition remediation = new(reader.GetGuid(0), reader.GetGuid(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4));
				remediationByProfile[remediation.ExecutionProfileId] = remediation;
			}
		}

		List<CatalogExecutionProfileDetail> details = [];
		foreach (CatalogExecutionProfile profile in profiles)
		{
			(CatalogComponent component, CatalogProductVersion productVersion, CatalogProduct product, CatalogContentRelease contentRelease, CatalogReportGroup reportGroup) = joined[profile.Id];
			details.Add(new CatalogExecutionProfileDetail(
				profile,
				component,
				productVersion,
				product,
				contentRelease,
				reportGroup,
				requirementsByProfile.TryGetValue(profile.Id, out List<CatalogCredentialRequirement>? requirements) ? requirements : [],
				benchmarksByProfile.TryGetValue(profile.Id, out CatalogBenchmarkReference? benchmark) ? benchmark : null,
				remediationByProfile.TryGetValue(profile.Id, out CatalogRemediationDefinition? remediation) ? remediation : null));
		}

		return details;
	}

	private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
	{
		NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		return connection;
	}

	private static CatalogExecutionProfile MapExecutionProfile(NpgsqlDataReader reader, int offset) => new(
		reader.GetGuid(offset),
		reader.GetGuid(offset + 1),
		reader.GetGuid(offset + 2),
		reader.GetGuid(offset + 3),
		reader.GetString(offset + 4),
		reader.GetBoolean(offset + 5),
		reader.GetString(offset + 6),
		reader.GetFieldValue<DateTimeOffset>(offset + 7));

	private static CatalogComponent MapComponent(NpgsqlDataReader reader, int offset) => new(
		reader.GetGuid(offset),
		reader.GetGuid(offset + 1),
		reader.IsDBNull(offset + 2) ? null : reader.GetGuid(offset + 2),
		reader.GetString(offset + 3),
		reader.GetString(offset + 4),
		reader.GetString(offset + 5),
		reader.GetString(offset + 6),
		reader.IsDBNull(offset + 7) ? null : reader.GetString(offset + 7),
		reader.GetFieldValue<DateTimeOffset>(offset + 8));

	private static CatalogProductVersion MapProductVersion(NpgsqlDataReader reader, int offset) => new(
		reader.GetGuid(offset), reader.GetGuid(offset + 1), reader.GetString(offset + 2), reader.GetString(offset + 3), reader.GetFieldValue<DateTimeOffset>(offset + 4));

	private static CatalogProduct MapProduct(NpgsqlDataReader reader, int offset) => new(
		reader.GetGuid(offset), reader.GetGuid(offset + 1), reader.GetString(offset + 2), reader.GetString(offset + 3), reader.GetString(offset + 4), reader.GetFieldValue<DateTimeOffset>(offset + 5));

	private static CatalogContentRelease MapContentRelease(NpgsqlDataReader reader, int offset) => new(
		reader.GetGuid(offset), reader.GetGuid(offset + 1), reader.GetString(offset + 2), reader.GetString(offset + 3), reader.GetString(offset + 4), reader.GetFieldValue<DateTimeOffset>(offset + 5));

	private static CatalogReportGroup MapReportGroup(NpgsqlDataReader reader, int offset) => new(
		reader.GetGuid(offset), reader.GetString(offset + 1), reader.GetString(offset + 2), reader.GetInt32(offset + 3), reader.GetFieldValue<DateTimeOffset>(offset + 4));
}
