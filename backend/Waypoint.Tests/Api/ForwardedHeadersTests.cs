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

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Api;

/// <summary>
/// Issue #61: the appliance lives behind nginx (ADR-0003), so the backend must
/// honour <c>X-Forwarded-Proto</c>/<c>X-Forwarded-For</c> (the audit trail's
/// initiating-identity record depends on the real client IP, security.md control 4)
/// and must never 307-redirect a proxied request no matter what HTTPS port
/// configuration appears.
/// </summary>
public sealed class ForwardedHeadersTests
	: IClassFixture<ForwardedHeadersTests.EchoApiFactory>, IClassFixture<ForwardedHeadersTests.GatedApiFactory>
{
	/// <summary>Adds a test-only echo route reflecting what the pipeline believes about the request, and sets the https_port that would have armed the removed redirect middleware.</summary>
	public sealed class EchoApiFactory : WaypointApiFactory
	{
		public const string EchoPath = "/api/v1/_test/forwarded-echo";

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			base.ConfigureWebHost(builder);

			// Arms UseHttpsRedirection IF it existed: with this set, the old pipeline
			// would 307 every plain-HTTP request. The regression below proves it does not.
			builder.UseSetting("https_port", "8443");

			builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new EchoStartupFilter()));
		}

		private sealed class EchoStartupFilter : IStartupFilter
		{
			public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
			{
				return app =>
				{
					next(app);

					// Appended after every production middleware (the ThrowingApiFactory
					// pattern), so what it reflects is what forwarded-headers processing
					// produced for the request.
					app.Use(async (context, nextMiddleware) =>
					{
						if (string.Equals(context.Request.Path, EchoPath, StringComparison.Ordinal))
						{
							context.Response.ContentType = "application/json";
							await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
							{
								scheme = context.Request.Scheme,
								remote_ip = context.Connection.RemoteIpAddress?.ToString()
							}));
							return;
						}

						await nextMiddleware(context);
					});
				};
			}
		}
	}

	/// <summary>The PRODUCTION trust path (PR #190 round 1, finding 2): TrustAnyProxy
	/// absent, so the KnownNetworks CIDR gate itself decides. A prepended middleware
	/// (registered BEFORE the production pipeline) forges the connection's remote
	/// address so both sides of the gate are exercisable under TestServer.</summary>
	public sealed class GatedApiFactory : WaypointApiFactory
	{
		public const string EchoPath = EchoApiFactory.EchoPath;

		/// <summary>Set per request via this header by the test, applied to the connection before forwarded-headers processing.</summary>
		public const string ForgedRemoteHeader = "X-Test-Forged-Remote";

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			base.ConfigureWebHost(builder);
			builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
			{
				// Explicitly false: exercises the else-branch even though the Testing
				// environment would be allowed to trust any proxy.
				["ForwardedHeaders:TrustAnyProxy"] = "false"
			}));
			builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new ForgingStartupFilter()));
		}

		private sealed class ForgingStartupFilter : IStartupFilter
		{
			public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
			{
				return app =>
				{
					// BEFORE next(app): runs ahead of UseForwardedHeaders, so the forged
					// remote address is what the known-networks gate evaluates.
					app.Use(async (context, nextMiddleware) =>
					{
						if (context.Request.Headers.TryGetValue(ForgedRemoteHeader, out Microsoft.Extensions.Primitives.StringValues forged))
						{
							context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(forged.ToString());
						}

						await nextMiddleware(context);
					});

					next(app);

					app.Use(async (context, nextMiddleware) =>
					{
						if (string.Equals(context.Request.Path, EchoPath, StringComparison.Ordinal))
						{
							context.Response.ContentType = "application/json";
							await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
							{
								scheme = context.Request.Scheme,
								remote_ip = context.Connection.RemoteIpAddress?.ToString()
							}));
							return;
						}

						await nextMiddleware(context);
					});
				};
			}
		}
	}

	private readonly EchoApiFactory _factory;

	private readonly GatedApiFactory _gatedFactory;

	public ForwardedHeadersTests(EchoApiFactory factory, GatedApiFactory gatedFactory)
	{
		_factory = factory;
		_gatedFactory = gatedFactory;
	}

	[Fact]
	public async Task AProxyInsideKnownNetworks_IsTrusted()
	{
		HttpClient client = _gatedFactory.CreateClient();
		using HttpRequestMessage request = new(HttpMethod.Get, GatedApiFactory.EchoPath);
		request.Headers.Add(GatedApiFactory.ForgedRemoteHeader, "172.16.0.5");
		request.Headers.Add("X-Forwarded-Proto", "https");
		request.Headers.Add("X-Forwarded-For", "198.51.100.7");

		using HttpResponseMessage response = await client.SendAsync(request);
		using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		Assert.Equal("https", document.RootElement.GetProperty("scheme").GetString());
		Assert.Equal("198.51.100.7", document.RootElement.GetProperty("remote_ip").GetString());
	}

	[Fact]
	public async Task AConnectionOutsideKnownNetworks_CannotSpoofViaForwardedHeaders()
	{
		HttpClient client = _gatedFactory.CreateClient();
		using HttpRequestMessage request = new(HttpMethod.Get, GatedApiFactory.EchoPath);
		request.Headers.Add(GatedApiFactory.ForgedRemoteHeader, "198.51.100.9");
		request.Headers.Add("X-Forwarded-Proto", "https");
		request.Headers.Add("X-Forwarded-For", "198.51.100.7");

		using HttpResponseMessage response = await client.SendAsync(request);
		using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		// The gate rejected the headers: the request keeps describing itself as the
		// direct plain-HTTP connection it actually is.
		Assert.Equal("http", document.RootElement.GetProperty("scheme").GetString());
		Assert.Equal("198.51.100.9", document.RootElement.GetProperty("remote_ip").GetString());
	}

	[Fact]
	public async Task AProxiedRequest_IsNeverRedirected_EvenWithAnHttpsPortConfigured()
	{
		HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false
		});

		using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/health/live");
		using HttpResponseMessage response = await client.SendAsync(request);

		Assert.NotEqual(HttpStatusCode.TemporaryRedirect, response.StatusCode);
		Assert.NotEqual(HttpStatusCode.PermanentRedirect, response.StatusCode);
	}

	[Fact]
	public async Task ForwardedProtoAndFor_AreReflectedIntoTheRequest()
	{
		// The Testing host trusts any proxy (TestServer connections carry no remote
		// address); production restricts to ForwardedHeaders:KnownNetworks.
		HttpClient client = _factory.CreateClient();
		using HttpRequestMessage request = new(HttpMethod.Get, EchoApiFactory.EchoPath);
		request.Headers.Add("X-Forwarded-Proto", "https");
		request.Headers.Add("X-Forwarded-For", "198.51.100.7");

		using HttpResponseMessage response = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		Assert.Equal("https", document.RootElement.GetProperty("scheme").GetString());
		Assert.Equal("198.51.100.7", document.RootElement.GetProperty("remote_ip").GetString());
	}
}
