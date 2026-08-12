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

using Waypoint.Core.Secrets;
using Xunit;

namespace Waypoint.Tests.Core.Secrets;

/// <summary>
/// Issue #469: <see cref="RunSecretOptions.Expiry"/>'s default is a documented
/// contract, not an arbitrary number -- it shortened from 24h to 8h when
/// <c>expires_at</c> became a sliding window (every <c>RunSecretStore.DecryptAsync</c>
/// pushes it back out), so an unnoticed edit here silently changes how long an
/// abandoned run's secret lingers. Pinned the same way <see cref="Waypoint.Tests.Core.Jobs.JobEngineOptionsTests"/>
/// pins <c>JobEngineOptions</c>'s defaults.
/// </summary>
public sealed class RunSecretOptionsTests
{
	[Fact]
	public void Defaults_MatchTheDocumentedContract()
	{
		RunSecretOptions options = new();

		Assert.Equal(TimeSpan.FromHours(8), options.Expiry);
		Assert.Equal(TimeSpan.FromMinutes(5), options.CleanupInterval);
	}
}
