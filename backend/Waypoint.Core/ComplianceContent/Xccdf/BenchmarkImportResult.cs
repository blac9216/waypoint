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

using System.Security.Cryptography;
using System.Text;

namespace Waypoint.Core.ComplianceContent.Xccdf;

/// <summary>
/// The normalized, not-yet-persisted result of safely importing one XCCDF/STIG-zip
/// package -- everything <see cref="Waypoint.Core.ComplianceContent.IBenchmarkRepository"/>
/// needs to create a <see cref="BenchmarkRevision"/> plus its <see cref="BenchmarkRule"/>
/// rows, without the repository knowing anything about zip/XML parsing.
/// </summary>
public sealed record BenchmarkImportCandidate(
	string BenchmarkKey,
	string Title,
	string Version,
	string Release,
	string ContentDigest,
	IReadOnlyList<XccdfRule> Rules);

/// <summary>
/// The outcome of <see cref="BenchmarkImporter"/> attempting one package: either a
/// ready-to-persist <see cref="Candidate"/>, or an actionable <see cref="Error"/> --
/// exactly one is non-null. Issue #730 AC "malformed input protections ... never a
/// crash": every failure mode from zip-slip to XXE to oversized/malformed XML collapses
/// into this single rejection shape rather than an exception escaping to the caller.
/// </summary>
public sealed record BenchmarkImportResult(BenchmarkImportCandidate? Candidate, string? Error)
{
	public bool Succeeded => Candidate is not null;

	public static BenchmarkImportResult Ok(BenchmarkImportCandidate candidate) => new(candidate, null);

	public static BenchmarkImportResult Fail(string error) => new(null, error);
}

/// <summary>
/// Safely imports a DISA XCCDF/STIG package -- either a raw XCCDF XML document or a
/// STIG distribution zip containing one -- into a <see cref="BenchmarkImportCandidate"/>.
/// This is the single entry point <c>IBenchmarkRepository</c> consumers (manual Admin
/// upload, and the future STIG Manager sync source under #729) both call, so the
/// untrusted-input discipline lives in exactly one place.
/// </summary>
public static class BenchmarkImporter
{
	/// <summary>
	/// Imports a raw XCCDF XML document (e.g. already extracted upstream, or a
	/// STIG Manager API response body).
	/// </summary>
	public static BenchmarkImportResult ImportXml(string? xmlText)
	{
		XccdfDocument? document = XccdfParser.TryParse(xmlText, out string? error);
		if (document is null)
		{
			return BenchmarkImportResult.Fail(error ?? "unknown XCCDF parse failure");
		}

		return BuildCandidate(document);
	}

	/// <summary>Imports a STIG distribution zip (Admin manual upload's expected shape).</summary>
	public static BenchmarkImportResult ImportZip(byte[]? zipBytes)
	{
		string? xmlText = StigZipReader.TryReadXccdfEntry(zipBytes, out string? zipError);
		if (xmlText is null)
		{
			return BenchmarkImportResult.Fail(zipError ?? "unknown STIG package read failure");
		}

		return ImportXml(xmlText);
	}

	private static BenchmarkImportResult BuildCandidate(XccdfDocument document)
	{
		string digest = ComputeDigest(document);
		return BenchmarkImportResult.Ok(new BenchmarkImportCandidate(
			document.BenchmarkId,
			document.Title,
			document.Version,
			document.Release,
			digest,
			document.Rules));
	}

	/// <summary>
	/// Deterministic content digest over the full parsed shape (issue #730 AC
	/// "digest-addressed"): two imports of byte-identical logical content always
	/// produce the same digest regardless of incidental XML formatting/whitespace/
	/// attribute order, because this hashes the ALREADY-PARSED, order-stabilized
	/// structure rather than the raw document bytes.
	/// </summary>
	private static string ComputeDigest(XccdfDocument document)
	{
		StringBuilder builder = new();
		builder.Append(document.BenchmarkId).Append('\n');
		builder.Append(document.Title).Append('\n');
		builder.Append(document.Version).Append('\n');
		builder.Append(document.Release).Append('\n');

		foreach (XccdfRule rule in document.Rules.OrderBy(r => r.RuleId, StringComparer.Ordinal))
		{
			builder.Append(rule.RuleId).Append(':').Append(rule.VulnId).Append(':').Append(rule.Severity).Append(':').Append(rule.Title).Append(';');
		}

		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
		return Convert.ToHexString(hash).ToLowerInvariant();
	}
}
