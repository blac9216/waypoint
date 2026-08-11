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
using Waypoint.ComplianceRunner.Readiness;
using Waypoint.Core.PowerShell;
using Waypoint.Core.Scans;
using Waypoint.Core.Secrets;
using Xunit;

namespace Waypoint.Tests.ComplianceRunner;

/// <summary>
/// Issue #440 AC4: "The host fails readiness when required compliance dependencies are
/// missing and reports its capabilities." Each test isolates exactly one dependency
/// (module path, one of the three content roots, the artifact store, or the master
/// key) so a readiness regression names precisely what broke.
/// </summary>
public sealed class ComplianceReadinessCheckTests : IDisposable
{
	private readonly string _tempRoot;

	public ComplianceReadinessCheckTests()
	{
		_tempRoot = Directory.CreateTempSubdirectory("waypoint-compliance-readiness-").FullName;
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_tempRoot, recursive: true);
		}
		catch (IOException)
		{
			// Best effort; the OS temp directory gets swept eventually regardless.
		}
	}

	[Fact]
	public void AllDependenciesPresent_ReportsReady()
	{
		ComplianceReadinessCheck check = BuildCheck(out _);

		ReadinessReport report = check.Evaluate();

		Assert.True(report.Ready);
		Assert.Empty(report.Problems);
	}

	[Fact]
	public void MissingModulePreloadPath_FailsReadinessAndNamesThePath()
	{
		ComplianceReadinessCheck check = BuildCheck(out Paths paths, modulePreloadPaths:
			[Path.Combine(_tempRoot, "does-not-exist-module")]);

		ReadinessReport report = check.Evaluate();

		Assert.False(report.Ready);
		Assert.Contains(report.Problems, problem => problem.Contains("does-not-exist-module", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("profile")]
	[InlineData("nsx")]
	[InlineData("srg")]
	public void MissingContentRoot_FailsReadiness(string which)
	{
		ComplianceReadinessCheck check = BuildCheck(out Paths paths, missingContentRoot: which);

		ReadinessReport report = check.Evaluate();

		Assert.False(report.Ready);
		Assert.NotEmpty(report.Problems);
	}

	[Fact]
	public void MissingArtifactStore_FailsReadiness()
	{
		ComplianceReadinessCheck check = BuildCheck(out Paths paths, missingArtifactStore: true);

		ReadinessReport report = check.Evaluate();

		Assert.False(report.Ready);
		Assert.Contains(report.Problems, problem => problem.Contains("ArtifactStorePath", StringComparison.Ordinal));
	}

	[Fact]
	public void UnavailableMasterKey_FailsReadinessWithoutThrowing()
	{
		ComplianceReadinessCheck check = BuildCheck(out _, masterKeyThrows: true);

		ReadinessReport report = check.Evaluate();

		Assert.False(report.Ready);
		Assert.Contains(report.Problems, problem => problem.Contains("Master key", StringComparison.Ordinal));
	}

	private ComplianceReadinessCheck BuildCheck(
		out Paths paths,
		IReadOnlyList<string>? modulePreloadPaths = null,
		string? missingContentRoot = null,
		bool missingArtifactStore = false,
		bool masterKeyThrows = false)
	{
		string modulesDir = Path.Combine(_tempRoot, "modules");
		string profileDir = Path.Combine(_tempRoot, "profiles", "vsphere");
		string nsxDir = Path.Combine(_tempRoot, "profiles", "nsx");
		string srgDir = Path.Combine(_tempRoot, "profiles", "srg");
		string artifactDir = Path.Combine(_tempRoot, "artifacts");

		Directory.CreateDirectory(modulesDir);
		if (missingContentRoot != "profile")
		{
			Directory.CreateDirectory(profileDir);
		}

		if (missingContentRoot != "nsx")
		{
			Directory.CreateDirectory(nsxDir);
		}

		if (missingContentRoot != "srg")
		{
			Directory.CreateDirectory(srgDir);
		}

		if (!missingArtifactStore)
		{
			Directory.CreateDirectory(artifactDir);
		}

		paths = new Paths(modulesDir, profileDir, nsxDir, srgDir, artifactDir);

		PowerShellOptions powerShellOptions = new();
		foreach (string path in modulePreloadPaths ?? [modulesDir])
		{
			powerShellOptions.ModulePreloadPaths.Add(path);
		}

		ScanOptions scanOptions = new()
		{
			ProfilePath = profileDir,
			NsxProfilePath = nsxDir,
			SrgProfilePath = srgDir,
			ArtifactStorePath = artifactDir,
		};

		IMasterKeyProvider masterKeyProvider = masterKeyThrows
			? new ThrowingMasterKeyProvider()
			: new FakeMasterKeyProvider();

		return new ComplianceReadinessCheck(
			Options.Create(powerShellOptions),
			Options.Create(scanOptions),
			masterKeyProvider);
	}

	private sealed record Paths(string ModulesDir, string ProfileDir, string NsxDir, string SrgDir, string ArtifactDir);

	private sealed class FakeMasterKeyProvider : IMasterKeyProvider
	{
		public MasterKey GetKey() => new(new byte[32], "wpk-fake0000");
	}

	private sealed class ThrowingMasterKeyProvider : IMasterKeyProvider
	{
		public MasterKey GetKey() => throw new MasterKeyUnavailableException("no key configured for this test");
	}
}
