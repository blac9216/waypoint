using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Waypoint.Api.Middleware;
using Waypoint.Core.Errors;

namespace Waypoint.Api.Validation;

/// <summary>
/// Turns MVC's model-state failures into the documented error envelope
/// (<c>docs/api-contract.md</c> Conventions: <c>{ "error": { code, message, detail? } }</c>,
/// snake_case).
///
/// <para>
/// Without this, <c>[ApiController]</c>'s automatic 400 short-circuits the whole error
/// pipeline and emits RFC 7807 <c>ProblemDetails</c> — a different envelope
/// (<c>type</c>/<c>title</c>/<c>status</c>/<c>errors</c>/<c>traceId</c>) in camelCase — on
/// the most common client-error path there is. <c>Program.cs</c> installs
/// <see cref="Create"/> as <c>ApiBehaviorOptions.InvalidModelStateResponseFactory</c> so
/// missing fields, malformed JSON bodies and mistyped query parameters all answer with
/// <c>validation_error</c> in the same shape as every other error.
/// </para>
/// </summary>
public static class ValidationErrorFactory
{
	private const string Message = "The request is not valid.";

	/// <summary>Cap on the rendered detail so a pathological model state cannot bloat a response.</summary>
	private const int MaxDetailEntries = 10;

	/// <summary>Builds the 400 response for a failed model state.</summary>
	/// <param name="context">The action context MVC hands the invalid-model-state factory.</param>
	public static IActionResult Create(ActionContext context)
	{
		return new ErrorEnvelopeResult(
			ApiException.Validation(Message, FormatDetail(context.ModelState)));
	}

	/// <summary>
	/// Renders model-state errors as a single <c>field: reason</c> line list for the
	/// envelope's optional <c>detail</c>. Only framework/validation-supplied messages are
	/// used — never <c>ModelError.Exception</c>, which can carry internal type and stack
	/// information the client has no business seeing.
	/// </summary>
	/// <param name="modelState">The failed model state.</param>
	/// <returns>The detail string, or <c>null</c> when nothing useful can be said.</returns>
	internal static string? FormatDetail(ModelStateDictionary modelState)
	{
		StringBuilder detail = new();
		int rendered = 0;

		foreach (KeyValuePair<string, ModelStateEntry> entry in modelState)
		{
			foreach (ModelError error in entry.Value.Errors)
			{
				if (rendered >= MaxDetailEntries)
				{
					break;
				}

				string reason = string.IsNullOrWhiteSpace(error.ErrorMessage)
					? "The value provided is not valid."
					: error.ErrorMessage;

				if (detail.Length > 0)
				{
					detail.Append("; ");
				}

				string field = NormalizeFieldName(entry.Key);
				if (field.Length > 0)
				{
					detail.Append(field).Append(": ");
				}

				detail.Append(reason);
				rendered++;
			}
		}

		return detail.Length > 0 ? detail.ToString() : null;
	}

	/// <summary>
	/// Normalizes a model-state key to the snake_case field name the client sent. Body
	/// binding errors arrive as JSON paths (<c>$.password</c>); validation errors arrive as
	/// CLR property names (<c>Password</c>); a whole-body failure arrives as an empty key.
	/// </summary>
	/// <param name="key">The raw model-state key.</param>
	private static string NormalizeFieldName(string key)
	{
		if (string.IsNullOrWhiteSpace(key))
		{
			return string.Empty;
		}

		string name = key.StartsWith("$.", StringComparison.Ordinal) ? key[2..] : key;
		if (name == "$")
		{
			return string.Empty;
		}

		return JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
	}
}
