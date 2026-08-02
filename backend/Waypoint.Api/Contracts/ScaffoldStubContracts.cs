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

namespace Waypoint.Api.Contracts;

/// <summary>A single row from the scaffold stub list — stands in for a real resource until one lands.</summary>
public sealed record ScaffoldStubItem(string Id, string Name);

/// <summary>Response body for <c>GET /api/v1/_stub/admin-only</c>.</summary>
public sealed record ScaffoldStubMessage(string Message);
