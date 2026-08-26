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

namespace Waypoint.Core.ComplianceContent.Xccdf;

/// <summary>
/// One safely parsed XCCDF <c>Rule</c> (issue #730 AC "rule/vulnerability/severity
/// identifiers"). <see cref="VulnId"/> is read from the rule's <c>ident</c>/version
/// group-id convention (the DISA-published "V-######" identifier); when a document
/// does not declare one, it is left null rather than fabricated -- the importer
/// surfaces that as a diagnostic, never a guess.
/// </summary>
public sealed record XccdfRule(string RuleId, string? VulnId, string Severity, string Title);

/// <summary>
/// A safely parsed XCCDF <c>Benchmark</c> document (issue #730 AC "benchmark ID,
/// title, version/release, ... rule/vulnerability/severity identifiers"). This is a
/// narrow projection of untrusted vendor input -- <see cref="XccdfParser"/> never
/// deserializes into arbitrary CLR types and never resolves external entities.
/// </summary>
public sealed record XccdfDocument(
	string BenchmarkId,
	string Title,
	string Version,
	string Release,
	IReadOnlyList<XccdfRule> Rules);
