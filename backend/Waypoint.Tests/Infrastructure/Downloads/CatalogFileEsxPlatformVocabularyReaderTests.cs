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

/// <summary>
/// Issue #1470: <see cref="CatalogFileEsxPlatformVocabularyReader"/> reads
/// <c>lcm.esx.supported.host.platforms</c> fresh from disk on every call -- proving
/// the vocabulary is sourced, never hardcoded, by mutating the on-disk document
/// between two calls and observing the second call reflect the change.
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

	private static CatalogFileEsxPlatformVocabularyReader CreateReader(string documentPath) =>
		new(Options.Create(new EsxAcquisitionOptions { VocabularyDocumentPath = documentPath }));

	[Fact]
	public async Task GetSupportedPlatformsAsync_DocumentMissing_ReturnsEmpty()
	{
		CatalogFileEsxPlatformVocabularyReader reader = CreateReader(Path.Combine(_tempDirectory, "does-not-exist.json"));

		IReadOnlyList<string> platforms = await reader.GetSupportedPlatformsAsync(CancellationToken.None);

		Assert.Empty(platforms);
	}

	[Fact]
	public async Task GetSupportedPlatformsAsync_ReadsTheVocabularyKey_FreshOnEveryCall_NotHardcoded()
	{
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
	public async Task GetSupportedPlatformsAsync_MalformedJson_ReturnsEmptyRatherThanThrowing()
	{
		string documentPath = Path.Combine(_tempDirectory, "productVersionCatalog.json");
		await File.WriteAllTextAsync(documentPath, "{ not valid json");
		CatalogFileEsxPlatformVocabularyReader reader = CreateReader(documentPath);

		IReadOnlyList<string> platforms = await reader.GetSupportedPlatformsAsync(CancellationToken.None);

		Assert.Empty(platforms);
	}
}
