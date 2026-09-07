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

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Waypoint.Core.Downloads;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// Issue #1470: <see cref="CatalogFileEsxPlatformVocabularyReader"/> reads
/// <c>lcm.esx.supported.host.platforms</c> fresh from disk on every call -- proving
/// the vocabulary is sourced, never hardcoded, by mutating the on-disk document
/// between two calls and observing the second call reflect the change.
/// Issue #1602: every degrade path (unset path, absent/unreadable/permission-denied
/// file, malformed JSON, absent vocabulary key) logs a distinct WARNING and never
/// throws.
/// </summary>
public sealed class CatalogFileEsxPlatformVocabularyReaderTests : IDisposable
{
	private readonly string _tempDirectory = Directory.CreateTempSubdirectory("waypoint-esx-vocab-test-").FullName;

	public void Dispose()
	{
		if (Directory.Exists(_tempDirectory))
		{
			Directory.Delete(_tempDirectory, recursive: true);
		}
	}

	private static CatalogFileEsxPlatformVocabularyReader CreateReader(
		string documentPath, ILogger<CatalogFileEsxPlatformVocabularyReader>? logger = null) =>
		new(Options.Create(new EsxAcquisitionOptions { VocabularyDocumentPath = documentPath }), logger ?? new CapturingLogger<CatalogFileEsxPlatformVocabularyReader>());

	[Fact]
	public async Task GetSupportedPlatformsAsync_DocumentMissing_ReturnsEmpty()
	{
		CatalogFileEsxPlatformVocabularyReader reader = CreateReader(Path.Combine(_tempDirectory, "does-not-exist.json"));

		IReadOnlyList<string> platforms = await reader.GetSupportedPlatformsAsync(CancellationToken.None);

		Assert.Empty(platforms);
	}

	[Fact]
	public async Task GetSupportedPlatformsAsync_DocumentMissing_LogsDistinctWarning()
	{
		CapturingLogger<CatalogFileEsxPlatformVocabularyReader> logger = new();
		string documentPath = Path.Combine(_tempDirectory, "does-not-exist.json");
		CatalogFileEsxPlatformVocabularyReader reader = CreateReader(documentPath, logger);

		await reader.GetSupportedPlatformsAsync(CancellationToken.None);

		CapturedLogEntry entry = logger.OnlyEntryAt(LogLevel.Warning);
		Assert.Contains(documentPath, entry.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetSupportedPlatformsAsync_PathUnset_ReturnsEmptyAndLogsDistinctWarning()
	{
		CapturingLogger<CatalogFileEsxPlatformVocabularyReader> logger = new();
		CatalogFileEsxPlatformVocabularyReader reader = new(Options.Create(new EsxAcquisitionOptions { VocabularyDocumentPath = "" }), logger);

		IReadOnlyList<string> platforms = await reader.GetSupportedPlatformsAsync(CancellationToken.None);

		Assert.Empty(platforms);
		CapturedLogEntry entry = logger.OnlyEntryAt(LogLevel.Warning);
		Assert.Contains("unset", entry.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task GetSupportedPlatformsAsync_MutatingTheDocument_ReflectedOnTheVeryNextCall_NotHardcoded()
	{
		// Issue #1470 AC, still proven with the issue #1603 cache in place: the
		// mutated document has a different length, so it always misses the
		// last-write-time/length cache key and is re-parsed.
		string documentPath = Path.Combine(_tempDirectory, "productVersionCatalog.json");
		await File.WriteAllTextAsync(documentPath, """{ "lcm.esx.supported.host.platforms": ["esx-8.0-standard"] }""");
		CatalogFileEsxPlatformVocabularyReader reader = CreateReader(documentPath);

		IReadOnlyList<string> before = await reader.GetSupportedPlatformsAsync(CancellationToken.None);
		Assert.Equal(["esx-8.0-standard"], before);

		// Mutate the source document -- if this were hardcoded, the second read would
		// be identical to the first.
		await File.WriteAllTextAsync(
			documentPath, """{ "lcm.esx.supported.host.platforms": ["esx-8.0-standard", "esx-8.0-hpe", "esx-8.0-dell"] }""");

		IReadOnlyList<string> after = await reader.GetSupportedPlatformsAsync(CancellationToken.None);
		Assert.Equal(["esx-8.0-standard", "esx-8.0-hpe", "esx-8.0-dell"], after);
	}

	/// <summary>
	/// Issue #1603: an unchanged document (same last-write time and length) is not
	/// re-parsed on the second call -- proved by making the document unreadable
	/// AFTER the first successful call, with its mtime/length otherwise untouched,
	/// and observing the second call still return the cached values rather than
	/// degrading to empty (which is what would happen if it attempted a fresh read).
	/// Skipped when the test process runs as root (root reads through a mode that
	/// denies every other user, so this precondition cannot be constructed).
	/// </summary>
	[Fact]
	public async Task GetSupportedPlatformsAsync_UnchangedDocument_IsNotReReadOnTheSecondCall()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		if (IsRoot())
		{
			return;
		}

		string documentPath = Path.Combine(_tempDirectory, "productVersionCatalog.json");
		await File.WriteAllTextAsync(documentPath, """{ "lcm.esx.supported.host.platforms": ["esx-8.0-standard"] }""");
		CatalogFileEsxPlatformVocabularyReader reader = CreateReader(documentPath);

		IReadOnlyList<string> before = await reader.GetSupportedPlatformsAsync(CancellationToken.None);
		Assert.Equal(["esx-8.0-standard"], before);

		File.SetUnixFileMode(documentPath, UnixFileMode.None);
		try
		{
			IReadOnlyList<string> after = await reader.GetSupportedPlatformsAsync(CancellationToken.None);

			// If the cache were bypassed, this read would fail (permission denied)
			// and degrade to empty -- getting the same values back proves the second
			// call never touched the file's content.
			Assert.Equal(["esx-8.0-standard"], after);
		}
		finally
		{
			File.SetUnixFileMode(documentPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}
	}

	[Fact]
	public async Task GetSupportedPlatformsAsync_KeyAbsent_ReturnsEmpty()
	{
		string documentPath = Path.Combine(_tempDirectory, "productVersionCatalog.json");
		await File.WriteAllTextAsync(documentPath, """{ "patches": {} }""");
		CatalogFileEsxPlatformVocabularyReader reader = CreateReader(documentPath);

		IReadOnlyList<string> platforms = await reader.GetSupportedPlatformsAsync(CancellationToken.None);

		Assert.Empty(platforms);
	}

	[Fact]
	public async Task GetSupportedPlatformsAsync_KeyAbsent_LogsDistinctWarning()
	{
		CapturingLogger<CatalogFileEsxPlatformVocabularyReader> logger = new();
		string documentPath = Path.Combine(_tempDirectory, "productVersionCatalog.json");
		await File.WriteAllTextAsync(documentPath, """{ "patches": {} }""");
		CatalogFileEsxPlatformVocabularyReader reader = CreateReader(documentPath, logger);

		await reader.GetSupportedPlatformsAsync(CancellationToken.None);

		CapturedLogEntry entry = logger.OnlyEntryAt(LogLevel.Warning);
		Assert.Contains("lcm.esx.supported.host.platforms", entry.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetSupportedPlatformsAsync_MalformedJson_ReturnsEmptyRatherThanThrowing()
	{
		string documentPath = Path.Combine(_tempDirectory, "productVersionCatalog.json");
		await File.WriteAllTextAsync(documentPath, "{ not valid json");
		CatalogFileEsxPlatformVocabularyReader reader = CreateReader(documentPath);

		IReadOnlyList<string> platforms = await reader.GetSupportedPlatformsAsync(CancellationToken.None);

		Assert.Empty(platforms);
	}

	[Fact]
	public async Task GetSupportedPlatformsAsync_MalformedJson_LogsDistinctWarningWithException()
	{
		CapturingLogger<CatalogFileEsxPlatformVocabularyReader> logger = new();
		string documentPath = Path.Combine(_tempDirectory, "productVersionCatalog.json");
		await File.WriteAllTextAsync(documentPath, "{ not valid json");
		CatalogFileEsxPlatformVocabularyReader reader = CreateReader(documentPath, logger);

		await reader.GetSupportedPlatformsAsync(CancellationToken.None);

		CapturedLogEntry entry = logger.OnlyEntryAt(LogLevel.Warning);
		Assert.NotNull(entry.Exception);
		Assert.Contains("not valid JSON", entry.Message, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Issue #1602: <c>File.ReadAllTextAsync</c> throws <see cref="UnauthorizedAccessException"/>
	/// (a <see cref="SystemException"/>, not an <see cref="IOException"/>) for a
	/// permission-denied file that <see cref="File.Exists"/> still reports as present --
	/// that path must degrade to empty like every other unavailable-document case, not
	/// surface as an unhandled 500. Skipped when the test process runs as root
	/// (<c>id -u</c> == 0): root can read through a mode that denies every other user,
	/// so the unreadable-file precondition cannot be constructed at all.
	/// </summary>
	[Fact]
	public async Task GetSupportedPlatformsAsync_PermissionDenied_ReturnsEmptyRatherThanThrowing()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		if (IsRoot())
		{
			return;
		}

		string documentPath = Path.Combine(_tempDirectory, "productVersionCatalog.json");
		await File.WriteAllTextAsync(documentPath, """{ "lcm.esx.supported.host.platforms": ["esx-8.0-standard"] }""");
		File.SetUnixFileMode(documentPath, UnixFileMode.None);

		try
		{
			CapturingLogger<CatalogFileEsxPlatformVocabularyReader> logger = new();
			CatalogFileEsxPlatformVocabularyReader reader = CreateReader(documentPath, logger);

			IReadOnlyList<string> platforms = await reader.GetSupportedPlatformsAsync(CancellationToken.None);

			Assert.Empty(platforms);
			CapturedLogEntry entry = logger.OnlyEntryAt(LogLevel.Warning);
			Assert.Contains(documentPath, entry.Message, StringComparison.Ordinal);
		}
		finally
		{
			File.SetUnixFileMode(documentPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}
	}

	/// <summary>
	/// Issue #1602: <see cref="EsxAcquisitionOptions.VocabularyDocumentPath"/>'s default
	/// is derived by <see cref="EsxAcquisitionOptionsPostConfigure"/> from
	/// <see cref="ManagedToolOptions.LocalRepositoryPath"/> and
	/// <see cref="ManagedToolOptions.ProductVersionCatalogPath"/> -- proved here by
	/// running the post-configure step with a reconfigured <c>LocalRepositoryPath</c>
	/// and asserting the derived path moves with it.
	/// </summary>
	[Fact]
	public void EsxAcquisitionOptionsPostConfigure_DerivesDefaultFromManagedToolOptions()
	{
		ManagedToolOptions managedTool = new() { LocalRepositoryPath = "/custom/depot" };
		EsxAcquisitionOptions options = new();
		EsxAcquisitionOptionsPostConfigure postConfigure = new(Options.Create(managedTool));

		postConfigure.PostConfigure(name: null, options);

		Assert.Equal(
			Path.Combine("/custom/depot", managedTool.ProductVersionCatalogPath),
			options.VocabularyDocumentPath);
	}

	[Fact]
	public void EsxAcquisitionOptionsPostConfigure_ExplicitOverride_IsNotReplaced()
	{
		ManagedToolOptions managedTool = new() { LocalRepositoryPath = "/custom/depot" };
		EsxAcquisitionOptions options = new() { VocabularyDocumentPath = "/explicit/override.json" };
		EsxAcquisitionOptionsPostConfigure postConfigure = new(Options.Create(managedTool));

		postConfigure.PostConfigure(name: null, options);

		Assert.Equal("/explicit/override.json", options.VocabularyDocumentPath);
	}

	/// <summary>Shells out to <c>id -u</c> -- the simplest portable check for effective root on Linux/macOS.</summary>
	private static bool IsRoot()
	{
		using Process process = Process.Start(new ProcessStartInfo("id", "-u")
		{
			RedirectStandardOutput = true,
			UseShellExecute = false,
		})!;
		string output = process.StandardOutput.ReadToEnd().Trim();
		process.WaitForExit();
		return output == "0";
	}
}
