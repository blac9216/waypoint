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

namespace Waypoint.ComplianceRunner.Readiness;

/// <summary>
/// The JSON shape written to <see cref="RunnerHealthOptions.ReportFilePath"/> and read
/// back by <c>--health-check</c> (see <see cref="RunnerHealthCheckProbe"/>). Kept
/// deliberately small and dependency-free (no Waypoint.Core reference) so it can be
/// serialized/deserialized with the BCL's source-generated <c>System.Text.Json</c>
/// support without pulling ASP.NET's JSON options into a non-ASP.NET host.
/// </summary>
public sealed record RunnerHealthReport(
	bool Ready,
	IReadOnlyList<string> Capabilities,
	IReadOnlyList<string> Problems,
	DateTimeOffset Timestamp);
