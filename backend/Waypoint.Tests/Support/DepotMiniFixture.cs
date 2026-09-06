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

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Waypoint.Tests.Support;

/// <summary>
/// Materializes the shared, checked-in <c>Fixtures/depot-mini/</c> tree (issue #1696)
/// into a throwaway temp directory for a single test: strips every binary
/// placeholder's <c>.placeholder</c> suffix (see the fixture's own README for why that
/// suffix exists -- the sanitize scanner refuses <c>.zip</c>/<c>.gz</c> unconditionally),
/// assembles the two UMDS metadata zips from checked-in XML parts, and rewrites the
/// catalog document's <c>{{sha256:...}}</c>/<c>{{size:...}}</c> template tokens against
/// the materialized files' real bytes. The PowerShell equivalent,
/// <c>New-DepotMiniFixture.ps1</c>, performs the identical rewrite against the SAME
/// checked-in tree so both consumers see byte-identical content.
/// </summary>
public sealed class DepotMiniFixture : IDisposable
{
	private static readonly string SourceRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "depot-mini");
	private static readonly Regex SizeTokenPattern = new("\"\\{\\{size:([^}]+)\\}\\}\"", RegexOptions.Compiled);
	private static readonly Regex HashTokenPattern = new("\\{\\{sha256:([^}]+)\\}\\}", RegexOptions.Compiled);

	/// <summary>The two ESX patch store layouts this fixture stages, both fed by the same checked-in <c>umds-parts/*.xml</c>.</summary>
	private static readonly string[] EsxVendorDirsRelative =
	[
		"PROD/COMP/ESX_HOST/patch-store/hostupdate/vmw",
		"ESX_LEGACY_STORE/hostupdate/vmw",
	];

	public string RootPath { get; }

	public string CatalogPath { get; }

	public string CatalogJson { get; }

	public DepotMiniFixture()
	{
		if (!Directory.Exists(SourceRoot))
		{
			throw new DirectoryNotFoundException($"depot-mini fixture source not found at '{SourceRoot}' -- expected Fixtures/depot-mini to be copied to test output.");
		}

		RootPath = Directory.CreateTempSubdirectory("wp-depot-mini-").FullName;
		CopyStrippingPlaceholders(SourceRoot, RootPath);
		AssembleEsxMetadataZips();

		CatalogPath = Path.Combine(RootPath, "PROD", "metadata", "productVersionCatalog", "v1", "productVersionCatalog.json");
		CatalogJson = RewriteTemplateTokens(File.ReadAllText(CatalogPath));
		File.WriteAllText(CatalogPath, CatalogJson);
	}

	public void Dispose()
	{
		if (Directory.Exists(RootPath))
		{
			Directory.Delete(RootPath, recursive: true);
		}
	}

	/// <summary>Copies every file under <paramref name="sourceDir"/> into <paramref name="destDir"/>, skipping <c>README.md</c>, <c>umds-parts/</c> (source-only), and the loader script itself (mirrors <c>New-DepotMiniFixture.ps1</c>'s own <c>-ne 'New-DepotMiniFixture.ps1'</c> exclusion, so both loaders materialize the SAME tree -- <see cref="Parity.DepotMiniLoaderParityTests"/>), and stripping a trailing <c>.placeholder</c> suffix from any copied filename.</summary>
	private static void CopyStrippingPlaceholders(string sourceDir, string destDir)
	{
		Directory.CreateDirectory(destDir);
		foreach (string dir in Directory.GetDirectories(sourceDir))
		{
			string name = Path.GetFileName(dir);
			if (name == "umds-parts")
			{
				continue;
			}

			CopyStrippingPlaceholders(dir, Path.Combine(destDir, name));
		}

		foreach (string file in Directory.GetFiles(sourceDir))
		{
			string name = Path.GetFileName(file);
			if (name is "README.md" or "New-DepotMiniFixture.ps1")
			{
				continue;
			}

			if (name.EndsWith(".placeholder", StringComparison.Ordinal))
			{
				name = name[..^".placeholder".Length];
			}

			File.Copy(file, Path.Combine(destDir, name), overwrite: true);
		}
	}

	/// <summary>Builds <c>metadata-fixture1696.zip</c> (named by the checked-in consolidated metadata index) under each ESX vendor directory from the shared checked-in XML parts -- never a checked-in zip (README: the sanitize scanner refuses that extension unconditionally).</summary>
	private void AssembleEsxMetadataZips()
	{
		string[] vibParts = Directory.GetFiles(Path.Combine(SourceRoot, "umds-parts"), "vib-*.xml");

		foreach (string vendorDirRelative in EsxVendorDirsRelative)
		{
			string vendorDir = Path.Combine(RootPath, vendorDirRelative.Replace('/', Path.DirectorySeparatorChar));
			if (!Directory.Exists(vendorDir))
			{
				continue;
			}

			string zipPath = Path.Combine(vendorDir, "metadata-fixture1696.zip");
			using FileStream fileStream = File.Create(zipPath);
			using ZipArchive archive = new(fileStream, ZipArchiveMode.Create);

			WriteEntry(archive, "vendor-index.xml", Path.Combine(SourceRoot, "umds-parts", "vendor-index.xml"));
			WriteEntry(archive, "vmware.xml", Path.Combine(SourceRoot, "umds-parts", "vmware.xml"));

			int i = 0;
			foreach (string vibPart in vibParts)
			{
				WriteEntry(archive, $"vibs/vib-{i++}.xml", vibPart);
			}
		}
	}

	private static void WriteEntry(ZipArchive archive, string entryName, string sourceFile)
	{
		using StreamWriter writer = new(archive.CreateEntry(entryName).Open());
		writer.Write(File.ReadAllText(sourceFile));
	}

	private string RewriteTemplateTokens(string json)
	{
		json = SizeTokenPattern.Replace(json, match => MaterializedFileInfo(match.Groups[1].Value).Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
		json = HashTokenPattern.Replace(json, match => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(MaterializedFileInfo(match.Groups[1].Value).FullName))));
		return json;
	}

	private FileInfo MaterializedFileInfo(string fixtureRelativePath) =>
		new(Path.Combine(RootPath, fixtureRelativePath.Replace('/', Path.DirectorySeparatorChar)));
}
