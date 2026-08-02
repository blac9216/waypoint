using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Waypoint.Tests.Support;

namespace Waypoint.Tests.Api;

/// <summary>
/// The 400 path of the error contract (<c>docs/api-contract.md</c> Conventions). These go
/// over real HTTP through the full MVC pipeline on purpose: the defect they cover was
/// <c>[ApiController]</c>'s automatic model-state response short-circuiting the envelope
/// with RFC 7807 <c>ProblemDetails</c> in camelCase — something no unit test of the error
/// types could ever have caught.
/// </summary>
public sealed class ValidationErrorTests : IClassFixture<WaypointApiFactory>, IClassFixture<RoleGuardedApiFactory>
{
	private readonly WaypointApiFactory _factory;
	private readonly RoleGuardedApiFactory _roleGuardedFactory;

	public ValidationErrorTests(WaypointApiFactory factory, RoleGuardedApiFactory roleGuardedFactory)
	{
		_factory = factory;
		_roleGuardedFactory = roleGuardedFactory;
	}

	[Fact]
	public async Task Login_WithMissingRequiredField_Returns400WithErrorEnvelope()
	{
		HttpClient client = _factory.CreateClient();

		// "password" omitted entirely — a non-nullable member of LoginRequest.
		HttpResponseMessage response = await client.PostAsync(
			"/api/v1/auth/login",
			JsonBody("{\"username\":\"admin\"}"));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		ErrorEnvelopeAssertions.AssertEnvelope(body, "validation_error");
		Assert.Contains("password", DetailOf(body), StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Login_WithMalformedJsonBody_Returns400WithErrorEnvelope()
	{
		HttpClient client = _factory.CreateClient();

		HttpResponseMessage response = await client.PostAsync(
			"/api/v1/auth/login",
			JsonBody("{\"username\": \"admin\", \"password\":"));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		ErrorEnvelopeAssertions.AssertEnvelope(await response.Content.ReadAsStringAsync(), "validation_error");
	}

	[Fact]
	public async Task ListEndpoint_WithBadQueryParameterType_Returns400WithErrorEnvelope()
	{
		// Authenticated: authorization runs before model binding, so an anonymous request
		// would 401 long before the query parameter is ever parsed.
		HttpClient client = _roleGuardedFactory.CreateClient();
		client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeaderName, "Admin");

		// "limit" binds to an int on PageRequest; "abc" cannot convert.
		HttpResponseMessage response = await client.GetAsync("/api/v1/_stub/items?limit=abc");

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		ErrorEnvelopeAssertions.AssertEnvelope(body, "validation_error");
		Assert.Contains("limit", DetailOf(body), StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Guards the specific regression: no RFC 7807 field may reappear at the top level, and
	/// nothing in the envelope may be camelCase.
	/// </summary>
	[Fact]
	public async Task ValidationError_DoesNotEmitProblemDetailsOrCamelCase()
	{
		HttpClient client = _factory.CreateClient();

		HttpResponseMessage response = await client.PostAsync("/api/v1/auth/login", JsonBody("{}"));
		string body = await response.Content.ReadAsStringAsync();

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

		using JsonDocument document = JsonDocument.Parse(body);
		foreach (string problemDetailsField in new[] { "type", "title", "status", "errors", "traceId" })
		{
			Assert.False(
				document.RootElement.TryGetProperty(problemDetailsField, out _),
				$"Response must not carry the ProblemDetails field \"{problemDetailsField}\".");
		}

		// snake_case check: every key in the envelope is lower-case with underscores.
		foreach (JsonProperty property in document.RootElement.GetProperty("error").EnumerateObject())
		{
			Assert.Equal(property.Name.ToLowerInvariant(), property.Name);
		}
	}

	private static string DetailOf(string body)
	{
		using JsonDocument document = JsonDocument.Parse(body);
		return document.RootElement.GetProperty("error").TryGetProperty("detail", out JsonElement detail)
			? detail.GetString() ?? string.Empty
			: string.Empty;
	}

	private static StringContent JsonBody(string json)
	{
		StringContent content = new(json, Encoding.UTF8);
		content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
		return content;
	}
}
