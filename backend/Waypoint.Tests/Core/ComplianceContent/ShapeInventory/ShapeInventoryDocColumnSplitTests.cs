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

using Xunit;

namespace Waypoint.Tests.Core.ComplianceContent.ShapeInventory;

/// <summary>
/// Issue #1120 AC3: covers an <c>Expected</c> cell containing a literal <c>\|</c>.
///
/// <see cref="ShapeInventoryDoc.LastColumn"/> is the SINGLE row-splitting definition that both
/// <see cref="ShapeInventoryDoc.AssertExpectedVocabulary"/> and
/// <see cref="ShapeInventoryDoc.ClassifyShapes"/> -- and therefore both the suite and
/// <c>scripts/parser-shape-diff.sh</c>, via <c>ShapeVerdictDump</c> -- depend on. Its escape-aware
/// walk-back is what stops a markdown-escaped pipe inside a cell from shifting which column is read
/// as Expected.
///
/// No row of the live inventory carries a <c>\|</c> in its Expected cell (the only one in the table
/// sits in <c>block-scalar-literal-description</c>'s Scenario cell, which is not the last pipe on
/// the line and so does not discriminate the two splits), so PR #1126's round-2 review was able to
/// replace the walk-back with a naive last-pipe search and keep the whole suite green. These tests
/// close that gap: they exercise the walk-back on synthetic row remainders rather than on the doc,
/// which keeps them independent of the inventory's contents -- no row is added, removed or reworded
/// to satisfy the criterion, and the coverage cannot decay when the table changes.
/// </summary>
public sealed class ShapeInventoryDocColumnSplitTests
{
	/// <summary>
	/// The self-contradictory cell the #1120 fail-open was reported with: a `\|` AFTER the verdict
	/// word, whose second half reads as an accept. A naive last-pipe split returns " Accepted only
	/// for entries already normalized." and classifies the row as an ACCEPT, silently disarming the
	/// shape's documented protection; the escape-aware split keeps the whole cell and classifies it
	/// as the REJECT the row actually declares.
	/// </summary>
	[Fact]
	public void EscapedPipeAfterVerdictWordDoesNotShiftTheExpectedColumn()
	{
		const string rowRemainder =
			" An entry name escapes the extraction root. " +
			"| Rejected with an unsafe-path error \\| Accepted only for entries already normalized. ";

		string expectedCell = ShapeInventoryDoc.LastColumn(rowRemainder);

		Assert.Equal(" Rejected with an unsafe-path error \\| Accepted only for entries already normalized. ", expectedCell);
		Assert.Equal("reject", ShapeInventoryDoc.ClassifyExpectedCell(expectedCell));
	}

	/// <summary>
	/// A cell whose LAST character run is an escaped pipe: a naive split returns the empty tail after
	/// it, which classifies as <c>null</c> -- unclassifiable, hence <c>UNVERIFIABLE</c> to the
	/// differential script. The escape-aware split reads the real cell.
	/// </summary>
	[Fact]
	public void ExpectedCellEndingInAnEscapedPipeIsStillReadWhole()
	{
		const string rowRemainder = " A description whose value is a literal pipe. | Rejected; the value may not be a bare \\| ";

		string expectedCell = ShapeInventoryDoc.LastColumn(rowRemainder);

		Assert.Equal(" Rejected; the value may not be a bare \\| ", expectedCell);
		Assert.Equal("reject", ShapeInventoryDoc.ClassifyExpectedCell(expectedCell));
	}

	/// <summary>
	/// Multiple escaped pipes in the Expected cell: every one of them must be walked past, not just
	/// the last.
	/// </summary>
	[Fact]
	public void SeveralEscapedPipesInTheExpectedCellAreAllSkipped()
	{
		const string rowRemainder = " Scenario. | Rejected because \\| and \\| are both reserved \\| here. ";

		string expectedCell = ShapeInventoryDoc.LastColumn(rowRemainder);

		Assert.Equal(" Rejected because \\| and \\| are both reserved \\| here. ", expectedCell);
		Assert.Equal("reject", ShapeInventoryDoc.ClassifyExpectedCell(expectedCell));
	}

	/// <summary>
	/// The shape the live table actually has (<c>block-scalar-literal-description</c>): the escaped
	/// pipe sits in the Scenario cell, before the real column separator. Both splits agree here --
	/// which is exactly why this row cannot stand in for the cases above -- but the escape-aware one
	/// must not over-walk past the genuine separator either.
	/// </summary>
	[Fact]
	public void EscapedPipeInTheScenarioCellStillYieldsTheExpectedCell()
	{
		const string rowRemainder =
			" An entry's `description:` uses a literal block scalar (`\\|`) spanning multiple lines. " +
			"| Accepted; parses without error and the input still resolves by name. ";

		string expectedCell = ShapeInventoryDoc.LastColumn(rowRemainder);

		Assert.Equal(" Accepted; parses without error and the input still resolves by name. ", expectedCell);
		Assert.Equal("accept", ShapeInventoryDoc.ClassifyExpectedCell(expectedCell));
	}

	/// <summary>An ordinary two-column row splits on its single unescaped pipe.</summary>
	[Fact]
	public void UnescapedPipeSplitsNormally()
	{
		Assert.Equal(" Accepted; resolves the input by name. ", ShapeInventoryDoc.LastColumn(" A plain scenario. | Accepted; resolves the input by name. "));
	}

	/// <summary>A remainder with no pipe at all is the Expected cell in its entirety.</summary>
	[Fact]
	public void RemainderWithNoPipeIsReturnedWhole()
	{
		Assert.Equal(" Accepted; nothing to split. ", ShapeInventoryDoc.LastColumn(" Accepted; nothing to split. "));
	}

	/// <summary>
	/// A leading escaped pipe at index 0 has no preceding character to inspect; the walk-back must
	/// not read out of bounds, and must treat it as escaped-by-nothing, i.e. a real separator.
	/// </summary>
	[Fact]
	public void PipeAtIndexZeroIsTreatedAsASeparator()
	{
		Assert.Equal(" Accepted; leading separator. ", ShapeInventoryDoc.LastColumn("| Accepted; leading separator. "));
	}
}
