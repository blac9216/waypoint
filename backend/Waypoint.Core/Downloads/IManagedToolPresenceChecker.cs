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

namespace Waypoint.Core.Downloads;

/// <summary>
/// Reports whether the operator-installed, account-gated <c>vcf-download-tool</c>
/// (ADR-0015) is present on the managed-tool state volume. <c>catalog-index</c> never
/// consults this (domain-model.md: indexing is a pure filesystem walk); the
/// <c>download</c> job type's gate does, so it fails with a clear, actionable message
/// rather than an opaque PowerShell error when the tool has not been installed yet.
/// </summary>
public interface IManagedToolPresenceChecker
{
	/// <summary>True when the managed tool executable is present and usable.</summary>
	bool IsPresent();

	/// <summary>Operator-actionable explanation of where the checker looked, for use in a tool-absent failure note.</summary>
	string DescribeExpectedLocation();
}
