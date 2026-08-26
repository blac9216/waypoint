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

using Waypoint.Core.ComplianceContent.Xccdf;
using Xunit;

namespace Waypoint.Tests.Core.ComplianceContent.Xccdf;

/// <summary>
/// Pure unit coverage for the closed benchmark vocabulary (issue #730), mirroring
/// <c>CatalogVocabularyValidatorTests</c>'s convention -- no Postgres dependency;
/// <c>BenchmarkRepositoryTests</c> covers the same fail-closed behavior end to end.
/// </summary>
public sealed class BenchmarkVocabularyValidatorTests
{
	[Theory]
	[InlineData(BenchmarkSources.ManualUpload)]
	[InlineData(BenchmarkSources.StigManager)]
	public void ValidateSource_KnownValue_NoErrors(string source)
	{
		Assert.Empty(BenchmarkVocabularyValidator.ValidateSource(source));
	}

	[Fact]
	public void ValidateSource_UnknownValue_ReturnsActionableError()
	{
		IReadOnlyList<string> errors = BenchmarkVocabularyValidator.ValidateSource("disa-direct-download");

		Assert.Single(errors);
		Assert.Contains("disa-direct-download", errors[0]);
		Assert.Contains("not in the closed benchmark vocabulary", errors[0]);
	}

	[Theory]
	[InlineData(BenchmarkRuleSeverities.Low)]
	[InlineData(BenchmarkRuleSeverities.Medium)]
	[InlineData(BenchmarkRuleSeverities.High)]
	public void ValidateSeverity_KnownValue_NoErrors(string severity)
	{
		Assert.Empty(BenchmarkVocabularyValidator.ValidateSeverity(severity));
	}

	[Fact]
	public void ValidateSeverity_UnknownValue_ReturnsActionableError()
	{
		IReadOnlyList<string> errors = BenchmarkVocabularyValidator.ValidateSeverity("critical");

		Assert.Single(errors);
		Assert.Contains("critical", errors[0]);
	}

	[Theory]
	[InlineData(BenchmarkMappingStatuses.Mapped)]
	[InlineData(BenchmarkMappingStatuses.Suggested)]
	[InlineData(BenchmarkMappingStatuses.Ambiguous)]
	[InlineData(BenchmarkMappingStatuses.Unmapped)]
	public void ValidateMappingStatus_KnownValue_NoErrors(string status)
	{
		Assert.Empty(BenchmarkVocabularyValidator.ValidateMappingStatus(status));
	}

	[Fact]
	public void ValidateMappingStatus_UnknownValue_ReturnsActionableError()
	{
		IReadOnlyList<string> errors = BenchmarkVocabularyValidator.ValidateMappingStatus("auto-approved");

		Assert.Single(errors);
		Assert.Contains("auto-approved", errors[0]);
	}

	[Theory]
	[InlineData(BenchmarkLifecycleStates.Staged)]
	[InlineData(BenchmarkLifecycleStates.Active)]
	[InlineData(BenchmarkLifecycleStates.Superseded)]
	[InlineData(BenchmarkLifecycleStates.Rejected)]
	public void ValidateLifecycleState_KnownValue_NoErrors(string lifecycleState)
	{
		Assert.Empty(BenchmarkVocabularyValidator.ValidateLifecycleState(lifecycleState));
	}
}
