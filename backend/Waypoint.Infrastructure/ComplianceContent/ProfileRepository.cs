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
using NpgsqlTypes;
using Waypoint.Core.ComplianceContent;

namespace Waypoint.Infrastructure.ComplianceContent;

/// <inheritdoc cref="IProfileRepository"/>
public sealed class ProfileRepository : IProfileRepository
{
	private readonly string _connectionString;

	public ProfileRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<IReadOnlyList<Profile>> ListAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, profile_key, name, version, commit, state, updated_at
			FROM profiles
			ORDER BY name
			""", connection);

		List<Profile> profiles = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			profiles.Add(new Profile(
				reader.GetGuid(0),
				reader.GetString(1),
				reader.GetString(2),
				reader.IsDBNull(3) ? null : reader.GetString(3),
				reader.GetString(4),
				reader.GetString(5),
				reader.GetFieldValue<DateTimeOffset>(6)));
		}

		return profiles;
	}

	public async Task ReplaceAllAsync(IReadOnlyList<ProfileUpsert> profiles, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(profiles);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		string[] keys = [.. profiles.Select(profile => profile.ProfileKey)];

		foreach (ProfileUpsert profile in profiles)
		{
			await using NpgsqlCommand upsert = new(
				"""
				INSERT INTO profiles (profile_key, name, version, commit, state)
				VALUES ($1, $2, $3, $4, $5)
				ON CONFLICT (profile_key) DO UPDATE SET
					name = EXCLUDED.name,
					version = EXCLUDED.version,
					commit = EXCLUDED.commit,
					state = EXCLUDED.state
				""", connection, transaction);
			upsert.Parameters.AddWithValue(profile.ProfileKey);
			upsert.Parameters.AddWithValue(profile.Name);
			upsert.Parameters.AddWithValue((object?)profile.Version ?? DBNull.Value);
			upsert.Parameters.AddWithValue(profile.Commit);
			upsert.Parameters.AddWithValue(profile.State);
			await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await using (NpgsqlCommand delete = new("DELETE FROM profiles WHERE NOT (profile_key = ANY($1))", connection, transaction))
		{
			delete.Parameters.Add(new NpgsqlParameter { Value = keys, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
			await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}
}
