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

using Microsoft.AspNetCore.Mvc.ModelBinding;
using Waypoint.Api.Validation;

namespace Waypoint.Tests.Api;

/// <summary>
/// Unit-level cover for how model-state failures are rendered into the envelope's optional
/// <c>detail</c> — field-name normalization, and the rule that internal exception text
/// never reaches the client.
/// </summary>
public sealed class ValidationErrorFactoryTests
{
	[Fact]
	public void FormatDetail_NormalizesClrPropertyNamesToSnakeCase()
	{
		ModelStateDictionary modelState = new();
		modelState.AddModelError("AdminPasswordHash", "The AdminPasswordHash field is required.");

		string? detail = ValidationErrorFactory.FormatDetail(modelState);

		Assert.NotNull(detail);
		Assert.StartsWith("admin_password_hash: ", detail!, StringComparison.Ordinal);
	}

	[Fact]
	public void FormatDetail_StripsTheJsonPathPrefixFromBodyBindingKeys()
	{
		ModelStateDictionary modelState = new();
		modelState.AddModelError("$.password", "The password field is required.");

		string? detail = ValidationErrorFactory.FormatDetail(modelState);

		Assert.NotNull(detail);
		Assert.StartsWith("password: ", detail!, StringComparison.Ordinal);
		Assert.DoesNotContain("$", detail!, StringComparison.Ordinal);
	}

	[Fact]
	public void FormatDetail_OmitsTheFieldPrefixForWholeBodyFailures()
	{
		ModelStateDictionary modelState = new();
		modelState.AddModelError(string.Empty, "A non-empty request body is required.");

		string? detail = ValidationErrorFactory.FormatDetail(modelState);

		Assert.Equal("A non-empty request body is required.", detail);
	}

	[Fact]
	public void FormatDetail_NeverLeaksTheUnderlyingExceptionText()
	{
		ModelStateDictionary modelState = new();
		modelState.TryAddModelException("payload", new InvalidOperationException("internal detail: /srv/secret/path"));

		string? detail = ValidationErrorFactory.FormatDetail(modelState);

		Assert.NotNull(detail);
		Assert.DoesNotContain("internal detail", detail!, StringComparison.Ordinal);
		Assert.DoesNotContain("/srv/secret/path", detail!, StringComparison.Ordinal);
		Assert.Equal("payload: The value provided is not valid.", detail);
	}

	[Fact]
	public void FormatDetail_WithNoErrors_ReturnsNullSoTheFieldIsOmitted()
	{
		Assert.Null(ValidationErrorFactory.FormatDetail(new ModelStateDictionary()));
	}

	[Fact]
	public void FormatDetail_CapsTheNumberOfRenderedEntries()
	{
		ModelStateDictionary modelState = new();
		for (int index = 0; index < 25; index++)
		{
			modelState.AddModelError($"field{index}", $"Message {index}.");
		}

		string? detail = ValidationErrorFactory.FormatDetail(modelState);

		Assert.NotNull(detail);
		Assert.Equal(10, detail!.Split("; ").Length);
	}
}
