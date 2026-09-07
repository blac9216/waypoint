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
using Xunit;

namespace Waypoint.Tests.Core.Downloads;

/// <summary>Issue #1464: <see cref="ConsumerViewPlatformVocabulary"/>'s static shape.</summary>
public sealed class ConsumerViewTests
{
	[Fact]
	public void All_ContainsTheCanonicalShapedKeys()
	{
		Assert.Contains("embeddedEsx-7.0-INTL", ConsumerViewPlatformVocabulary.All);
	}

	[Fact]
	public void All_HasNoDuplicates()
	{
		Assert.Equal(ConsumerViewPlatformVocabulary.All.Distinct(), ConsumerViewPlatformVocabulary.All);
	}

	[Fact]
	public void All_IsNotEmpty()
	{
		Assert.NotEmpty(ConsumerViewPlatformVocabulary.All);
	}
}
