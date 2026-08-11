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

namespace Waypoint.Tests.Support;

/// <summary>Test double for <see cref="IManagedToolPresenceChecker"/> -- a fixed present/absent answer, no filesystem access.</summary>
public sealed class FakeManagedToolPresenceChecker : IManagedToolPresenceChecker
{
	private readonly bool _present;
	private readonly string _expectedLocation;

	public FakeManagedToolPresenceChecker(bool present, string expectedLocation = "/var/lib/waypoint/managed-tool/vcf-download-tool")
	{
		_present = present;
		_expectedLocation = expectedLocation;
	}

	public bool IsPresent() => _present;

	public string DescribeExpectedLocation() => _expectedLocation;
}
