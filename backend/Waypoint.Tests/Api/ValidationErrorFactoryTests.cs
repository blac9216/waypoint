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
