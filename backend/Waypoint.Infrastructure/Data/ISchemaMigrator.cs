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

namespace Waypoint.Infrastructure.Data;

/// <summary>
/// Applies pending Postgres schema migrations. ADR-0009 expects the schema to be
/// current before the API takes traffic, so <c>Program.cs</c> calls
/// <see cref="ApplyAsync"/> during startup — not lazily on first request — when
/// <c>Database:RunMigrationsOnStartup</c> is enabled.
/// </summary>
public interface ISchemaMigrator
{
	/// <summary>
	/// Applies every migration not yet recorded in the <c>schema_migrations</c>
	/// tracking table, in order. Safe to call repeatedly: an already-migrated database
	/// is a no-op.
	/// </summary>
	Task ApplyAsync(CancellationToken cancellationToken = default);
}
