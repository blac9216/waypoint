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

namespace Waypoint.Core.Logging;

/// <summary>
/// The log-scrubbing hook point required from the start by <c>docs/security.md</c>
/// control 1: "The logging pipeline maintains the set of secret values currently in
/// play ... and redacts every occurrence before any line reaches a sink." The API's
/// Serilog pipeline routes every rendered log line through the registered
/// implementation before it is written (console/file/Postgres sinks alike).
///
/// The registered implementation is <see cref="InPlaySecretRedactor"/> (epic #6
/// slice 1, replacing the scaffold's no-op): code that decrypts or receives a secret
/// registers it via <see cref="ISecretTracker.Track"/> for exactly as long as it is
/// in play. The ADR-0005 credential store (#8) becomes the main registrar.
/// </summary>
public interface ISecretRedactor
{
	/// <summary>Returns <paramref name="line"/> with any known secret values replaced.</summary>
	string Redact(string line);
}
