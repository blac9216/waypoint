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

using Waypoint.Core.ComplianceContent.SemanticImport;
using Waypoint.Core.ComplianceContent.Xccdf;
using Xunit;
using Xunit.Abstractions;

namespace Waypoint.Tests.Core.ComplianceContent.ShapeInventory;

/// <summary>
/// Issue #1077's opt-in real-content conformance check: walks a locally cloned vendor
/// content repository (read-only) and reports, per vendor-content parser, how many
/// real artifacts it accepts versus rejects. This is deliberately NOT a completeness
/// guard by itself -- see the PR #1084 round-2 review recorded on issue #1077: a real
/// artifact accepting today proves nothing about a shape the parser silently STOPPED
/// accepting, because the shape it lost may simply not appear in today's content. Pair
/// this with the shape inventory's per-shape assertions (documented drift-proof
/// coverage) and <c>scripts/parser-shape-diff.sh</c> (old-vs-new differential) for the
/// full guard the issue describes.
///
/// Every test here is a clean no-op (no assertion executed, reported via test output)
/// when the vendor content clone is absent -- CI never depends on vendor content, and
/// no vendor/DISA content is ever read into this repository.
/// </summary>
public sealed class RealContentConformanceTests
{
	private const string VendorContentRepoRoot = "/workspaces/git/dod-compliance-and-automation";

	private readonly ITestOutputHelper _output;

	public RealContentConformanceTests(ITestOutputHelper output) => _output = output;

	/// <summary>
	/// Issue #1073's evidence table, reproduced live: every <c>*.zip</c> under the
	/// vendor clone's <c>docs/</c> directories should parse to at least one benchmark
	/// with no reader error now that #1073 is fixed. Reports the accept/reject count
	/// either way; fails only if the current (post-fix) parser rejects any -- if this
	/// ever regresses, it means a real vendor package shape is being missed again.
	/// </summary>
	[Fact]
	public void StigZipReader_RealVendorPackages_AcceptRejectCounts()
	{
		if (!Directory.Exists(VendorContentRepoRoot))
		{
			_output.WriteLine($"Skipping: vendor content clone not present at {VendorContentRepoRoot}.");
			return;
		}

		string[] packages = Directory.GetFiles(VendorContentRepoRoot, "*.zip", SearchOption.AllDirectories)
			.Where(path => path.Contains($"{Path.DirectorySeparatorChar}docs{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
			.ToArray();
		Assert.NotEmpty(packages);

		int accepted = 0;
		List<string> rejected = [];
		foreach (string packagePath in packages)
		{
			byte[] zipBytes = File.ReadAllBytes(packagePath);
			bool ok = StigZipReader.TryReadXccdfEntries(zipBytes, out IReadOnlyList<XccdfZipEntry> entries, out string? error);
			if (ok && entries.Count > 0)
			{
				accepted++;
			}
			else
			{
				rejected.Add($"{packagePath}: ok={ok} count={entries.Count} error={error}");
			}
		}

		_output.WriteLine($"StigZipReader: {accepted}/{packages.Length} real packages accepted, {rejected.Count} rejected.");
		Assert.True(rejected.Count == 0, "StigZipReader rejected real vendor packages:\n" + string.Join("\n", rejected));
	}

	/// <summary>
	/// Issue #1099's third acceptance criterion ("the real-content conformance check
	/// reports per-parser counts for all three [remaining parsers]"), extended to
	/// <see cref="XccdfParser"/>: every real XCCDF XML entry <see cref="StigZipReader"/>
	/// already resolves out of a real vendor package should also parse cleanly through
	/// <see cref="XccdfParser"/> into at least one rule -- exercising the real-content
	/// side of the namespace/prefix/encoding shapes this PR's new inventory section
	/// documents, at effectively no extra traversal cost by reusing the entries
	/// <see cref="StigZipReader_RealVendorPackages_AcceptRejectCounts"/> above already reads.
	/// </summary>
	[Fact]
	public void XccdfParser_RealVendorBenchmarks_AcceptRejectCounts()
	{
		if (!Directory.Exists(VendorContentRepoRoot))
		{
			_output.WriteLine($"Skipping: vendor content clone not present at {VendorContentRepoRoot}.");
			return;
		}

		string[] packages = Directory.GetFiles(VendorContentRepoRoot, "*.zip", SearchOption.AllDirectories)
			.Where(path => path.Contains($"{Path.DirectorySeparatorChar}docs{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
			.ToArray();
		Assert.NotEmpty(packages);

		int accepted = 0;
		int totalEntries = 0;
		int skippedMacResourceForkEntries = 0;
		List<string> rejected = [];
		foreach (string packagePath in packages)
		{
			byte[] zipBytes = File.ReadAllBytes(packagePath);
			if (!StigZipReader.TryReadXccdfEntries(zipBytes, out IReadOnlyList<XccdfZipEntry> entries, out _))
			{
				continue;
			}

			foreach (XccdfZipEntry entry in entries)
			{
				// A handful of real vendor zips carry macOS "AppleDouble" resource-fork
				// junk entries (`__MACOSX/.../._Some-xccdf.xml`) that happen to satisfy
				// StigZipReader's name-suffix match but contain binary metadata, not XML
				// -- StigZipReader itself does not validate entry CONTENT (only the
				// entry NAME), so these are a pre-existing StigZipReader gap, not an
				// XccdfParser shape this PR is scoped to fix (filed as issue #1207).
				// Excluded here so this floor check measures genuine benchmark entries.
				if (entry.EntryPath.Contains("__MACOSX/", StringComparison.Ordinal) ||
					Path.GetFileName(entry.EntryPath).StartsWith('.'))
				{
					skippedMacResourceForkEntries++;
					continue;
				}

				totalEntries++;
				XccdfDocument? document = XccdfParser.TryParse(entry.XmlText, out string? error);
				if (document is not null && document.Rules.Count > 0)
				{
					accepted++;
				}
				else
				{
					rejected.Add($"{packagePath}!{entry.EntryPath}: error={error} ruleCount={document?.Rules.Count ?? -1}");
				}
			}
		}

		_output.WriteLine($"XccdfParser: {accepted}/{totalEntries} real benchmark entries accepted, {rejected.Count} rejected " +
			$"({skippedMacResourceForkEntries} macOS resource-fork junk entries excluded).");
		Assert.True(rejected.Count == 0, "XccdfParser rejected real vendor benchmark entries:\n" + string.Join("\n", rejected));
	}

	/// <summary>
	/// Issue #1071's evidence, reproduced live: every real <c>inspec.yml</c> under the
	/// vendor clone should resolve at least as many declared inputs as it has
	/// <c>inputs:</c>/<c>attributes:</c> entries in the manifest text (a rough but
	/// real-content-grounded sanity floor -- the exact expected count per manifest is
	/// not hand-curated here, only that the manifest parses and is not silently
	/// treated as input-less when it plainly declares inputs).
	/// </summary>
	[Fact]
	public void InspecManifestParser_RealVendorManifests_AcceptRejectCounts()
	{
		if (!Directory.Exists(VendorContentRepoRoot))
		{
			_output.WriteLine($"Skipping: vendor content clone not present at {VendorContentRepoRoot}.");
			return;
		}

		string[] manifests = Directory.GetFiles(VendorContentRepoRoot, "inspec.yml", SearchOption.AllDirectories);
		Assert.NotEmpty(manifests);

		int accepted = 0;
		List<string> rejected = [];
		foreach (string manifestPath in manifests)
		{
			string text = File.ReadAllText(manifestPath);
			InspecManifest? manifest = InspecManifestParser.TryParse(text, out string? error);
			bool declaresInputs = text.Contains("\ninputs:", StringComparison.Ordinal) || text.Contains("\nattributes:", StringComparison.Ordinal);

			if (manifest is not null && (!declaresInputs || manifest.Inputs.Count > 0))
			{
				accepted++;
			}
			else
			{
				rejected.Add($"{manifestPath}: parsed={manifest is not null} inputCount={manifest?.Inputs.Count ?? -1} declaresInputs={declaresInputs} error={error}");
			}
		}

		_output.WriteLine($"InspecManifestParser: {accepted}/{manifests.Length} real manifests accepted, {rejected.Count} rejected.");
		Assert.True(rejected.Count == 0, "InspecManifestParser rejected/under-resolved real manifests:\n" + string.Join("\n", rejected));
	}
}
