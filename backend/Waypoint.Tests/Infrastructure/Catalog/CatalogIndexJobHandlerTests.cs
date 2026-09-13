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

using System.Globalization;
using System.Management.Automation;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Waypoint.Core.Catalog;
using Waypoint.Core.Logging;
using Waypoint.Core.Pagination;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.Catalog;
using Waypoint.Infrastructure.PowerShell;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Catalog;

/// <summary>
/// Fast, fully-faked unit coverage for <see cref="CatalogIndexJobHandler"/>'s two
/// private-turned-internal parsing helpers (<c>InternalsVisibleTo("Waypoint.Tests")</c>
/// on <c>Waypoint.Infrastructure.Execution.csproj</c>) -- no Postgres, no real
/// PowerShell invocation. The full-loop/real-module coverage (a real depot-index
/// sweep landing rows in Postgres) lives in
/// <c>CatalogIndexJobHandlerEndToEndTests</c>/<c>CatalogIndexJobHandlerRealModuleEndToEndTests</c>;
/// this file targets the two gaps issues #1615 and #1840 closed.
/// </summary>
public sealed class CatalogIndexJobHandlerTests
{
	private sealed class UnreachableArtifactRepository : IDepotArtifactRepository
	{
		public Task<Guid> UpsertAsync(DepotArtifactUpsert artifact, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task<DepotArtifact?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task<IReadOnlyList<DepotArtifact>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task<(IReadOnlyList<DepotArtifact> Items, long TotalCount)> ListAsync(DepotArtifactFilter filter, PageRequest page, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task<int> RekeyManyAsync(IReadOnlyDictionary<string, string> renames, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task<bool> SupersedeCatalogDocumentRowAsync(string catalogDocumentRelativePath, CancellationToken cancellationToken) => throw new InvalidOperationException();
	}

	private sealed class UnreachableUnknownFileRepository : IUnknownCatalogFileRepository
	{
		public Task<Guid> RecordSeenAsync(string relativePath, long? sizeBytes, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task<IReadOnlyList<UnknownCatalogFile>> ListAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
	}

	private sealed class UnreachableExecutor : IPowerShellExecutor
	{
		public Task<PowerShellExecutionResult> ExecuteAsync(PowerShellRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException();
	}

	private static CatalogIndexJobHandler CreateHandler() => new(
		new UnreachableExecutor(),
		new UnreachableArtifactRepository(),
		new UnreachableUnknownFileRepository(),
		new InPlaySecretRedactor(),
		Options.Create(new CatalogOptions()),
		Options.Create(new PowerShellOptions()),
		NullLogger<CatalogIndexJobHandler>.Instance);

	private static PSObject ArtifactPresence(
		string externalId, string status, object? sizeBytes, string? mismatchReason = null)
	{
		PSObject psObject = new();
		psObject.Properties.Add(new PSNoteProperty("RecordType", "ArtifactPresence"));
		psObject.Properties.Add(new PSNoteProperty("ExternalId", externalId));
		psObject.Properties.Add(new PSNoteProperty("Status", status));
		psObject.Properties.Add(new PSNoteProperty("Sha256", "deadbeef"));
		psObject.Properties.Add(new PSNoteProperty("Product", "VCF"));
		psObject.Properties.Add(new PSNoteProperty("Version", "9.0"));
		psObject.Properties.Add(new PSNoteProperty("SizeBytes", sizeBytes));
		psObject.Properties.Add(new PSNoteProperty("RelativePath", externalId));
		psObject.Properties.Add(new PSNoteProperty("MismatchReason", mismatchReason));
		return psObject;
	}

	/// <summary>
	/// Issue #1615: a custom current culture whose <c>NegativeSign</c> is not the ASCII
	/// hyphen the input string uses -- exactly the shape of culture-sensitivity bug the
	/// simple <c>long.TryParse(string, out long)</c> overload has (it binds
	/// <see cref="CultureInfo.CurrentCulture"/>). Under the pre-fix overload this
	/// culture makes the parse FAIL (the string's ASCII '-' does not match the
	/// culture's own negative-sign token), silently dropping the size; the fix's
	/// explicit <see cref="CultureInfo.InvariantCulture"/> parse is unaffected by
	/// whatever the thread's current culture happens to be.
	/// </summary>
	[Fact]
	public void TryToInt64_StringUnderNonInvariantCulture_ParsesInvariantlyRegardlessOfCurrentCulture()
	{
		CultureInfo originalCulture = Thread.CurrentThread.CurrentCulture;
		try
		{
			NumberFormatInfo customFormat = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
			customFormat.NegativeSign = "XX";
			CultureInfo customCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
			customCulture.NumberFormat = customFormat;
			Thread.CurrentThread.CurrentCulture = customCulture;

			CatalogIndexJobHandler handler = CreateHandler();
			DepotArtifactUpsert? upsert = handler.TryParseArtifact(ArtifactPresence("a", DepotArtifactStatuses.Present, "-42"));

			Assert.NotNull(upsert);
			Assert.Equal(-42, upsert!.SizeBytes);
		}
		finally
		{
			Thread.CurrentThread.CurrentCulture = originalCulture;
		}
	}

	/// <summary>Issue #1615 AC: an ordinary (positive, unsigned) numeric string still parses under any culture.</summary>
	[Fact]
	public void TryToInt64_OrdinaryNumericString_Parses()
	{
		CatalogIndexJobHandler handler = CreateHandler();
		DepotArtifactUpsert? upsert = handler.TryParseArtifact(ArtifactPresence("a", DepotArtifactStatuses.Present, "1048576"));

		Assert.Equal(1048576, upsert!.SizeBytes);
	}

	/// <summary>Issue #1615 AC: a `double` holding an integral value converts rather than falling through to null.</summary>
	[Fact]
	public void TryToInt64_IntegralDouble_Converts()
	{
		CatalogIndexJobHandler handler = CreateHandler();
		DepotArtifactUpsert? upsert = handler.TryParseArtifact(ArtifactPresence("a", DepotArtifactStatuses.Present, 2048.0d));

		Assert.Equal(2048, upsert!.SizeBytes);
	}

	/// <summary>Issue #1615 AC: a `decimal` holding an integral value converts rather than falling through to null.</summary>
	[Fact]
	public void TryToInt64_IntegralDecimal_Converts()
	{
		CatalogIndexJobHandler handler = CreateHandler();
		DepotArtifactUpsert? upsert = handler.TryParseArtifact(ArtifactPresence("a", DepotArtifactStatuses.Present, 4096m));

		Assert.Equal(4096, upsert!.SizeBytes);
	}

	/// <summary>A `double`/`decimal` holding a genuinely fractional value is not an integral byte count -- still drops to null, same "skip, don't halt" posture as any other unparsable value.</summary>
	[Theory]
	[InlineData(1024.5d)]
	public void TryToInt64_NonIntegralDouble_ReturnsNull(double value)
	{
		CatalogIndexJobHandler handler = CreateHandler();
		DepotArtifactUpsert? upsert = handler.TryParseArtifact(ArtifactPresence("a", DepotArtifactStatuses.Present, value));

		Assert.Null(upsert!.SizeBytes);
	}

	/// <summary>Issue #1840: MismatchReason values (size-mismatch, hash-mismatch) are carried through into the persisted metadata JSON.</summary>
	[Theory]
	[InlineData("size-mismatch")]
	[InlineData("hash-mismatch")]
	public void TryParseArtifact_WithMismatchReason_PersistsItInMetadata(string mismatchReason)
	{
		CatalogIndexJobHandler handler = CreateHandler();
		DepotArtifactUpsert? upsert = handler.TryParseArtifact(ArtifactPresence("a", DepotArtifactStatuses.Present, 1024, mismatchReason));

		Assert.NotNull(upsert);
		Assert.Contains($"\"mismatch_reason\":\"{mismatchReason}\"", upsert!.MetadataJson, StringComparison.Ordinal);
	}

	/// <summary>Issue #1840: an absent (`$null`) MismatchReason persists nothing extra -- the metadata JSON carries no `mismatch_reason` key at all.</summary>
	[Fact]
	public void TryParseArtifact_WithoutMismatchReason_PersistsNothingExtra()
	{
		CatalogIndexJobHandler handler = CreateHandler();
		DepotArtifactUpsert? upsert = handler.TryParseArtifact(ArtifactPresence("a", DepotArtifactStatuses.Present, 1024, mismatchReason: null));

		Assert.NotNull(upsert);
		Assert.DoesNotContain("mismatch_reason", upsert!.MetadataJson, StringComparison.Ordinal);
	}
}
