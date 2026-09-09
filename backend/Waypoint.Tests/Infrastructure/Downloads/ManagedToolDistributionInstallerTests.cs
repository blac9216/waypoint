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
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// Issue #686: <c>ManagedToolDistributionInstaller</c>'s safe extraction, layout
/// validation, bounded smoke-test execution, and atomic activation of a verified VCFDT
/// <c>.tar.gz</c> distribution -- the fix for the <c>Exec format error</c> regression
/// where the archive itself was copied straight into the executable path.
/// <see cref="ManagedToolDistributionFixture"/> builds invented archives modeling the
/// sibling <c>../vcf-docker-download/Dockerfile</c> layout; no real vendor bytes are
/// used anywhere in this file.
/// </summary>
public sealed class ManagedToolDistributionInstallerTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("wp-vcfdt-installer-").FullName;

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	private ManagedToolDistributionInstaller CreateInstaller(int maxEntries = 20_000, long maxBytes = 2L * 1024 * 1024 * 1024)
	{
		ManagedToolOptions options = new()
		{
			ToolStatePath = _root,
			ExecutableRelativePath = "bin/vcf-download-tool",
			LibraryRelativePath = "lib",
			ActiveDirectoryName = "active",
			StagingDirectoryName = "staging",
			MaxArchiveEntries = maxEntries,
			MaxExtractedTotalBytes = maxBytes,
			SmokeTestTimeout = TimeSpan.FromSeconds(10),
			SmokeTestArgument = "--help",
		};
		return new ManagedToolDistributionInstaller(Options.Create(options));
	}

	private string ArchivePath([System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
		Path.Combine(_root, $"{name}.tar.gz");

	private string ActivePath => Path.Combine(_root, "active");

	private string StagingRoot => Path.Combine(_root, "staging");

	[Fact]
	public async Task HappyPath_ExtractsSmokeTestsAndActivatesAtomically()
	{
		string archive = ArchivePath();
		ManagedToolDistributionFixture.WriteHappyPathArchive(archive);
		ManagedToolDistributionInstaller installer = CreateInstaller();

		ManagedToolDistributionInstallResult result = await installer.InstallAsync(archive, CancellationToken.None);

		Assert.True(result.Succeeded);
		string executablePath = Path.Combine(ActivePath, "bin", "vcf-download-tool");
		Assert.True(File.Exists(executablePath));
		Assert.True(Directory.Exists(Path.Combine(ActivePath, "lib")));
		Assert.True(File.Exists(Path.Combine(ActivePath, "lib", "libvcfdt-fixture.so.1")));

		// Staging is cleaned up on every path, including success.
		if (Directory.Exists(StagingRoot))
		{
			Assert.Empty(Directory.GetDirectories(StagingRoot));
		}
	}

	[Fact]
	public async Task ArchiveAsExecutable_Regression_IsRejectedAndNeverActivated_EvenOnRetry()
	{
		string archive = ArchivePath();
		ManagedToolDistributionFixture.WriteArchiveAsExecutableArchive(archive);
		ManagedToolDistributionInstaller installer = CreateInstaller();

		ManagedToolDistributionInstallResult first = await installer.InstallAsync(archive, CancellationToken.None);
		Assert.False(first.Succeeded);
		Assert.Equal(ManagedToolDistributionRejectionKind.SmokeTestFailed, first.RejectionKind);
		Assert.False(Directory.Exists(ActivePath));

		// A retry of the exact same bad archive must never succeed either -- proves
		// there is no path (e.g. a cached "looks extracted" shortcut) that lets a
		// non-executable "archive as binary" slip through on a second attempt.
		ManagedToolDistributionInstallResult second = await installer.InstallAsync(archive, CancellationToken.None);
		Assert.False(second.Succeeded);
		Assert.Equal(ManagedToolDistributionRejectionKind.SmokeTestFailed, second.RejectionKind);
		Assert.False(Directory.Exists(ActivePath));
	}

	[Fact]
	public async Task MissingLibDirectory_IsRejected_NeverActivated()
	{
		string archive = ArchivePath();
		ManagedToolDistributionFixture.WriteMissingLibArchive(archive);
		ManagedToolDistributionInstaller installer = CreateInstaller();

		ManagedToolDistributionInstallResult result = await installer.InstallAsync(archive, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Equal(ManagedToolDistributionRejectionKind.MissingLayout, result.RejectionKind);
		Assert.False(Directory.Exists(ActivePath));
	}

	[Fact]
	public async Task MissingExecutable_IsRejected_NeverActivated()
	{
		string archive = ArchivePath();
		ManagedToolDistributionFixture.WriteMissingExecutableArchive(archive);
		ManagedToolDistributionInstaller installer = CreateInstaller();

		ManagedToolDistributionInstallResult result = await installer.InstallAsync(archive, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Equal(ManagedToolDistributionRejectionKind.MissingLayout, result.RejectionKind);
	}

	[Fact]
	public async Task AbsolutePathEntry_IsRejected_NeverActivated()
	{
		string archive = ArchivePath();
		ManagedToolDistributionFixture.WriteAbsolutePathArchive(archive);
		ManagedToolDistributionInstaller installer = CreateInstaller();

		ManagedToolDistributionInstallResult result = await installer.InstallAsync(archive, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Equal(ManagedToolDistributionRejectionKind.UnsafePath, result.RejectionKind);
		Assert.False(File.Exists("/etc/waypoint-fixture-canary"));
	}

	[Fact]
	public async Task TraversalEntry_IsRejected_NeverEscapesStaging()
	{
		string archive = ArchivePath();
		ManagedToolDistributionFixture.WriteTraversalArchive(archive);
		ManagedToolDistributionInstaller installer = CreateInstaller();

		ManagedToolDistributionInstallResult result = await installer.InstallAsync(archive, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Equal(ManagedToolDistributionRejectionKind.UnsafePath, result.RejectionKind);
		Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "waypoint-fixture-canary")));
	}

	[Fact]
	public async Task SymlinkEscape_IsRejected_NeverActivated()
	{
		string archive = ArchivePath();
		ManagedToolDistributionFixture.WriteSymlinkEscapeArchive(archive);
		ManagedToolDistributionInstaller installer = CreateInstaller();

		ManagedToolDistributionInstallResult result = await installer.InstallAsync(archive, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Equal(ManagedToolDistributionRejectionKind.UnsafeLink, result.RejectionKind);
		Assert.False(Directory.Exists(ActivePath));
	}

	[Fact]
	public async Task SpecialFileEntry_IsRejected_NeverActivated()
	{
		string archive = ArchivePath();
		ManagedToolDistributionFixture.WriteSpecialFileArchive(archive);
		ManagedToolDistributionInstaller installer = CreateInstaller();

		ManagedToolDistributionInstallResult result = await installer.InstallAsync(archive, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Equal(ManagedToolDistributionRejectionKind.SpecialFile, result.RejectionKind);
	}

	[Fact]
	public async Task TooManyEntries_IsRejected_NeverActivated()
	{
		string archive = ArchivePath();
		ManagedToolDistributionFixture.WriteTooManyEntriesArchive(archive, maxEntries: 5);
		ManagedToolDistributionInstaller installer = CreateInstaller(maxEntries: 5);

		ManagedToolDistributionInstallResult result = await installer.InstallAsync(archive, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Equal(ManagedToolDistributionRejectionKind.ExpansionLimitExceeded, result.RejectionKind);
	}

	[Fact]
	public async Task OversizedExpansion_IsRejected_NeverActivated()
	{
		string archive = ArchivePath();
		const long cap = 4096;
		ManagedToolDistributionFixture.WriteOversizedArchive(archive, cap);
		ManagedToolDistributionInstaller installer = CreateInstaller(maxBytes: cap);

		ManagedToolDistributionInstallResult result = await installer.InstallAsync(archive, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Equal(ManagedToolDistributionRejectionKind.ExpansionLimitExceeded, result.RejectionKind);
	}

	[Fact]
	public async Task MalformedArchive_IsRejected_NeverActivated()
	{
		string archive = ArchivePath();
		ManagedToolDistributionFixture.WriteMalformedArchive(archive);
		ManagedToolDistributionInstaller installer = CreateInstaller();

		ManagedToolDistributionInstallResult result = await installer.InstallAsync(archive, CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.Equal(ManagedToolDistributionRejectionKind.MalformedArchive, result.RejectionKind);
	}

	[Fact]
	public async Task RejectedInstall_PreservesPriorGoodInstallation()
	{
		ManagedToolDistributionInstaller installer = CreateInstaller();

		string goodArchive = ArchivePath();
		ManagedToolDistributionFixture.WriteHappyPathArchive(goodArchive);
		ManagedToolDistributionInstallResult goodResult = await installer.InstallAsync(goodArchive, CancellationToken.None);
		Assert.True(goodResult.Succeeded);
		byte[] goodExecutableBytes = File.ReadAllBytes(Path.Combine(ActivePath, "bin", "vcf-download-tool"));

		string badArchive = Path.Combine(_root, "bad.tar.gz");
		ManagedToolDistributionFixture.WriteArchiveAsExecutableArchive(badArchive);
		ManagedToolDistributionInstallResult badResult = await installer.InstallAsync(badArchive, CancellationToken.None);

		Assert.False(badResult.Succeeded);
		// The prior-good installation is untouched: same bytes, still present.
		Assert.True(File.Exists(Path.Combine(ActivePath, "bin", "vcf-download-tool")));
		Assert.Equal(goodExecutableBytes, File.ReadAllBytes(Path.Combine(ActivePath, "bin", "vcf-download-tool")));
	}

	[Fact]
	public async Task SuccessfulReinstall_ReplacesPriorInstallation_NoLeftoverPreviousDirectory()
	{
		ManagedToolDistributionInstaller installer = CreateInstaller();

		string firstArchive = ArchivePath();
		ManagedToolDistributionFixture.WriteHappyPathArchive(firstArchive, extraLibFileName: "lib/libvcfdt-fixture.so.1");
		Assert.True((await installer.InstallAsync(firstArchive, CancellationToken.None)).Succeeded);

		string secondArchive = Path.Combine(_root, "second.tar.gz");
		ManagedToolDistributionFixture.WriteHappyPathArchive(secondArchive, extraLibFileName: "lib/libvcfdt-fixture.so.2");
		ManagedToolDistributionInstallResult secondResult = await installer.InstallAsync(secondArchive, CancellationToken.None);

		Assert.True(secondResult.Succeeded);
		Assert.True(File.Exists(Path.Combine(ActivePath, "lib", "libvcfdt-fixture.so.2")));
		Assert.False(File.Exists(Path.Combine(ActivePath, "lib", "libvcfdt-fixture.so.1")));

		// No stray ".previous-*" directory left behind under the tool state root.
		Assert.DoesNotContain(
			Directory.GetDirectories(_root),
			path => Path.GetFileName(path).Contains(".previous-", StringComparison.Ordinal));
	}

	[Fact]
	public async Task StagingIsCleanedUp_OnRejection()
	{
		string archive = ArchivePath();
		ManagedToolDistributionFixture.WriteMissingLibArchive(archive);
		ManagedToolDistributionInstaller installer = CreateInstaller();

		await installer.InstallAsync(archive, CancellationToken.None);

		if (Directory.Exists(StagingRoot))
		{
			Assert.Empty(Directory.GetDirectories(StagingRoot));
		}
	}
}
