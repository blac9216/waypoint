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

using System.Text;
using Waypoint.Api.Contracts;
using Waypoint.Core.Jobs;

namespace Waypoint.Tests.Api;

/// <summary>
/// Issue #757: the opaque three-leg <c>(priority, created_at, id)</c> wire cursor for
/// <c>GET /runs/{id}/component-jobs</c>. <see cref="ComponentJobCursor.TryDecode"/>
/// must return <c>false</c> -- never throw -- on every malformed shape a client can
/// send (the controller maps a decode failure to 400 <c>validation_error</c>, never a
/// 500), mirroring the <c>JobEventCursor</c>/<c>RunHistoryCursor</c> contract.
/// </summary>
public sealed class ComponentJobCursorTests
{
	[Fact]
	public void EncodeThenDecode_RoundTripsEveryLeg()
	{
		ComponentJobCursorPosition position = new(
			Priority: 6,
			CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(1_772_123_456_789),
			Id: Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301"));

		string cursor = ComponentJobCursor.Encode(position);
		Assert.True(ComponentJobCursor.TryDecode(cursor, out ComponentJobCursorPosition? decoded));
		Assert.Equal(position, decoded);
	}

	[Fact]
	public void Encode_ProducesOpaqueBase64_NotThePlainKeyset()
	{
		ComponentJobCursorPosition position = new(1, DateTimeOffset.UnixEpoch, Guid.Empty);
		string cursor = ComponentJobCursor.Encode(position);

		// Not readable as-is (no colon-delimited plaintext on the wire) but valid base64.
		Assert.DoesNotContain(":", cursor, StringComparison.Ordinal);
		Assert.NotEmpty(Convert.FromBase64String(cursor));
	}

	public static TheoryData<string> MalformedCursors() => new()
	{
		"", // empty
		"   ", // whitespace
		"not-base64!!!", // invalid base64
		Convert.ToBase64String(Encoding.ASCII.GetBytes("v2:1:2:00000000-0000-0000-0000-000000000000")), // wrong version prefix
		Convert.ToBase64String(Encoding.ASCII.GetBytes("no-prefix")), // no prefix at all
		Convert.ToBase64String(Encoding.ASCII.GetBytes("v1:1:2")), // only two legs
		Convert.ToBase64String(Encoding.ASCII.GetBytes("v1:abc:2:00000000-0000-0000-0000-000000000000")), // non-numeric priority
		Convert.ToBase64String(Encoding.ASCII.GetBytes("v1:-1:2:00000000-0000-0000-0000-000000000000")), // negative priority (NumberStyles.None)
		Convert.ToBase64String(Encoding.ASCII.GetBytes("v1:1:xyz:00000000-0000-0000-0000-000000000000")), // non-numeric timestamp
		Convert.ToBase64String(Encoding.ASCII.GetBytes("v1:1:2:not-a-guid")), // malformed guid
	};

	[Theory]
	[MemberData(nameof(MalformedCursors))]
	public void TryDecode_MalformedInput_ReturnsFalseNeverThrows(string cursor)
	{
		Exception? thrown = Record.Exception(() =>
			Assert.False(ComponentJobCursor.TryDecode(cursor, out _)));
		Assert.Null(thrown);
	}

	[Theory]
	[InlineData("")]
	[InlineData("not-base64!!!")]
	public void TryDecode_ObviousGarbage_ReturnsFalseWithNullPosition(string cursor)
	{
		Assert.False(ComponentJobCursor.TryDecode(cursor, out ComponentJobCursorPosition? position));
		Assert.Null(position);
	}

	[Fact]
	public void TryDecode_PriorityBeyondInt16_ReturnsFalse()
	{
		string cursor = Convert.ToBase64String(Encoding.ASCII.GetBytes("v1:40000:2:00000000-0000-0000-0000-000000000000"));
		Assert.False(ComponentJobCursor.TryDecode(cursor, out _));
	}
}
