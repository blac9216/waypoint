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

using Microsoft.Extensions.Options;
using Waypoint.Core.Downloads;
using Waypoint.Infrastructure.Downloads;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>The filesystem-backed default <see cref="IManagedToolPresenceChecker"/> implementation (issue #441).</summary>
public sealed class ManagedToolPresenceCheckerTests : IDisposable
{
	private readonly string _tempDirectory = Directory.CreateTempSubdirectory("waypoint-managed-tool-test-").FullName;

	public void Dispose()
	{
		if (Directory.Exists(_tempDirectory))
		{
			Directory.Delete(_tempDirectory, recursive: true);
		}
	}

	private ManagedToolPresenceChecker CreateChecker(string executableName = "vcf-download-tool") =>
		new(Options.Create(new ManagedToolOptions { ToolStatePath = _tempDirectory, ExecutableName = executableName }));

	[Fact]
	public void NoFile_IsNotPresent()
	{
		Assert.False(CreateChecker().IsPresent());
	}

	[Fact]
	public void FileAtExpectedPath_IsPresent()
	{
		File.WriteAllBytes(Path.Combine(_tempDirectory, "vcf-download-tool"), [1, 2, 3]);
		Assert.True(CreateChecker().IsPresent());
	}

	[Fact]
	public void DescribeExpectedLocation_JoinsStatePathAndExecutableName()
	{
		string description = CreateChecker("vcf-download-tool").DescribeExpectedLocation();
		Assert.Equal(Path.Combine(_tempDirectory, "vcf-download-tool"), description);
	}

	[Fact]
	public void DifferentExecutableName_IsHonoured()
	{
		File.WriteAllBytes(Path.Combine(_tempDirectory, "custom-tool.bin"), [1]);
		Assert.False(CreateChecker("vcf-download-tool").IsPresent());
		Assert.True(CreateChecker("custom-tool.bin").IsPresent());
	}
}
