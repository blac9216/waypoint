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

using Waypoint.Core.Downloads;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Core.Downloads;

/// <summary>
/// Drift guard for <see cref="VmToolsPlatforms.All"/>/<see cref="VmToolsFileTypes.All"/>
/// against migration 0109's NAMED <c>vmtools_artifact_index_platform_check</c> and
/// <c>vmtools_artifact_index_file_type_check</c> constraints. This file's own round-1
/// fix (PR #1765 relay) tried scoping purely by CREATE-TABLE paren position and found
/// it ALTER-invisible: a later migration re-declaring the constraint via this repo's
/// DROP CONSTRAINT/ADD CONSTRAINT idiom sits outside a CREATE TABLE's parens and would
/// have gone unseen, so that round instead matched on the constraint NAME alone
/// (table-scoped only by this repo's naming CONVENTION, not enforced). Issue #1814
/// closes that gap for real: <see cref="ConstraintDriftScan"/> is both genuinely
/// table-scoped (its own regression tests prove an identically-named CHECK on another
/// table is ignored) AND ALTER-visible (it also scans <c>ALTER TABLE &lt;table&gt; ADD
/// CONSTRAINT</c> restatements naming the same table), so this file no longer needs
/// its own copy of either concern -- table scoping AND ALTER visibility. Scans every
/// embedded migration in order and keeps the LAST declaration -- the one the
/// fully-migrated database actually enforces.
/// </summary>
public sealed class VmToolsConstraintDriftTests
{
	[Fact]
	public void VmToolsPlatformsAll_IsInLockstepWithPlatformCheckConstraintValueSet()
	{
		List<string> constraintValues = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(
			"vmtools_artifact_index", "vmtools_artifact_index_platform_check", "platform");
		Assert.Equal(VmToolsPlatforms.All, constraintValues);
	}

	[Fact]
	public void VmToolsFileTypesAll_IsInLockstepWithFileTypeCheckConstraintValueSet()
	{
		List<string> constraintValues = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(
			"vmtools_artifact_index", "vmtools_artifact_index_file_type_check", "file_type");
		Assert.Equal(VmToolsFileTypes.All, constraintValues);
	}
}
