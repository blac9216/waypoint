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
using Waypoint.Core.SystemState;

namespace Waypoint.Infrastructure.SystemState;

/// <inheritdoc cref="IApplianceStateRepository"/>
public sealed class ApplianceStateRepository : IApplianceStateRepository
{
	private readonly string _connectionString;

	public ApplianceStateRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<ApplianceState?> GetAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT version, build_sha, mode, update_status, update_available_version
			FROM appliance_state
			WHERE id = 1
			""", connection);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return null;
		}

		return new ApplianceState(
			Version: reader.GetString(0),
			BuildSha: reader.IsDBNull(1) ? null : reader.GetString(1),
			Mode: reader.GetString(2),
			UpdateStatus: reader.GetString(3),
			UpdateAvailableVersion: reader.IsDBNull(4) ? null : reader.GetString(4));
	}
}
