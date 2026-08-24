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
using Waypoint.Core.Downloads;
using Xunit;

namespace Waypoint.Tests.Core.Downloads;

/// <summary>
/// Issue #691: <see cref="DepotActivationCodeCodec"/> decodes a base64-encoded JSON
/// envelope and extracts <c>asset_id</c> for pairing validation, treating every
/// malformed shape as one uniform "structurally invalid" outcome (null), never
/// throwing -- the controller relies on that to report a clean 400 without leaking
/// the raw code into an exception message.
/// </summary>
public sealed class DepotActivationCodeCodecTests
{
	private static string EncodeJson(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

	[Fact]
	public void TryExtractAssetId_ValidEnvelope_ReturnsAssetId()
	{
		string code = EncodeJson("""{"asset_id":"WPT-0001-DEPOT-ID","issued_at":"2026-08-01"}""");
		Assert.Equal("WPT-0001-DEPOT-ID", DepotActivationCodeCodec.TryExtractAssetId(code));
	}

	[Fact]
	public void TryExtractAssetId_TrimsSurroundingWhitespace()
	{
		string code = "  " + EncodeJson("""{"asset_id":"WPT-0002"}""") + "\n";
		Assert.Equal("WPT-0002", DepotActivationCodeCodec.TryExtractAssetId(code));
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData(null)]
	public void TryExtractAssetId_NullOrBlankInput_ReturnsNull(string? input)
	{
		Assert.Null(DepotActivationCodeCodec.TryExtractAssetId(input!));
	}

	[Fact]
	public void TryExtractAssetId_NotValidBase64_ReturnsNullWithoutThrowing()
	{
		Assert.Null(DepotActivationCodeCodec.TryExtractAssetId("not-base64-!!!"));
	}

	[Fact]
	public void TryExtractAssetId_DecodedBytesAreNotJson_ReturnsNull()
	{
		string code = Convert.ToBase64String(Encoding.UTF8.GetBytes("just some plain text, not json"));
		Assert.Null(DepotActivationCodeCodec.TryExtractAssetId(code));
	}

	[Fact]
	public void TryExtractAssetId_DecodedJsonIsAnArrayNotAnObject_ReturnsNull()
	{
		string code = EncodeJson("""["asset_id","WPT-0001"]""");
		Assert.Null(DepotActivationCodeCodec.TryExtractAssetId(code));
	}

	[Fact]
	public void TryExtractAssetId_MissingAssetIdField_ReturnsNull()
	{
		string code = EncodeJson("""{"other_field":"value"}""");
		Assert.Null(DepotActivationCodeCodec.TryExtractAssetId(code));
	}

	[Fact]
	public void TryExtractAssetId_AssetIdIsNotAString_ReturnsNull()
	{
		string code = EncodeJson("""{"asset_id":12345}""");
		Assert.Null(DepotActivationCodeCodec.TryExtractAssetId(code));
	}

	[Fact]
	public void TryExtractAssetId_EmptyAssetIdString_ReturnsNull()
	{
		string code = EncodeJson("""{"asset_id":""}""");
		Assert.Null(DepotActivationCodeCodec.TryExtractAssetId(code));
	}
}
