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

using System.Text.Json;

namespace Waypoint.Api.Diagnostics;

/// <summary>
/// The container health probe, implemented *inside the application* rather than as a
/// shell command in the orchestrator.
///
/// <para>
/// Convention (see <c>backend/README.md</c> "Container health"): <b>the image owns how it
/// reports health; the orchestrator merely invokes it.</b> The .NET runtime images
/// (<c>mcr.microsoft.com/dotnet/aspnet</c>) are deliberately slim and ship neither
/// <c>curl</c> nor <c>wget</c>, so a compose-side <c>wget --spider</c> healthcheck can
/// never run — it fails every probe and any service gated on
/// <c>condition: service_healthy</c> never starts. Installing a HTTP client into the
/// runtime image would fix the symptom at the cost of image size and attack surface on
/// every future .NET service we ship.
/// </para>
/// <para>
/// Instead the app answers its own probe: <c>dotnet Waypoint.Api.dll --health-check</c>
/// performs a loopback GET of <c>/api/v1/health</c> and exits 0 (healthy) or 1
/// (unhealthy). It needs nothing that is not already in the image, so the same
/// <c>HEALTHCHECK</c> works in plain <c>docker run</c>, compose, and any future
/// orchestrator.
/// </para>
/// </summary>
public static class HealthCheckProbe
{
	/// <summary>The command-line argument that switches the entry point into probe mode.</summary>
	public const string Argument = "--health-check";

	/// <summary>Optional full override of the URL probed (host, port and path).</summary>
	public const string UrlEnvironmentVariable = "WAYPOINT_HEALTHCHECK_URL";

	/// <summary>Path of the health endpoint appended to a URL that carries no path of its own.</summary>
	private const string HealthPath = "/api/v1/health";

	/// <summary>Used when neither the override nor <c>ASPNETCORE_URLS</c> says anything usable.</summary>
	private const string FallbackUrl = "http://127.0.0.1:8080" + HealthPath;

	private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

	/// <summary>True when the process was started to probe health rather than to serve traffic.</summary>
	/// <param name="args">The entry point's raw arguments.</param>
	public static bool IsHealthCheckInvocation(string[] args)
	{
		return args.Any(argument => string.Equals(argument, Argument, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Probes the running instance over loopback and returns the process exit code Docker
	/// reads: <c>0</c> healthy, <c>1</c> unhealthy. Never throws — any failure (connection
	/// refused, timeout, non-200, unexpected payload) is an unhealthy verdict.
	/// </summary>
	public static int Run()
	{
		string url = ResolveUrl(
			Environment.GetEnvironmentVariable(UrlEnvironmentVariable),
			Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));

		// Synchronous by design: this is a short-lived probe process, not a server.
		return RunAsync(url).ConfigureAwait(false).GetAwaiter().GetResult();
	}

	/// <summary>Probes <paramref name="url"/> and reports the verdict as an exit code.</summary>
	/// <param name="url">Absolute URL of the health endpoint.</param>
	internal static async Task<int> RunAsync(string url)
	{
		try
		{
			using HttpClient client = new() { Timeout = ProbeTimeout };
			using HttpResponseMessage response = await client.GetAsync(url).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode)
			{
				await Console.Error.WriteLineAsync(
					$"health-check: {url} returned {(int)response.StatusCode}").ConfigureAwait(false);
				return 1;
			}

			string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
			if (!IsHealthyPayload(body))
			{
				await Console.Error.WriteLineAsync(
					$"health-check: {url} returned 200 but the payload did not report status \"ok\"").ConfigureAwait(false);
				return 1;
			}

			return 0;
		}
		catch (Exception exception)
		{
			// Connection refused while the host is still starting is the common case, so
			// keep this quiet-but-informative rather than a stack trace in the container log.
			await Console.Error.WriteLineAsync($"health-check: {url} unreachable — {exception.Message}").ConfigureAwait(false);
			return 1;
		}
	}

	/// <summary>
	/// Works out what to probe. Precedence: the explicit override, then the first plain-HTTP
	/// entry of <c>ASPNETCORE_URLS</c> (the binding the container actually listens on), then
	/// the documented default port. Wildcard bind hosts (<c>+</c>, <c>*</c>, <c>0.0.0.0</c>)
	/// are rewritten to loopback because the probe runs inside the same container.
	/// </summary>
	/// <param name="explicitUrl">Value of <see cref="UrlEnvironmentVariable"/>, if set.</param>
	/// <param name="aspNetCoreUrls">Value of <c>ASPNETCORE_URLS</c>, if set.</param>
	internal static string ResolveUrl(string? explicitUrl, string? aspNetCoreUrls)
	{
		if (!string.IsNullOrWhiteSpace(explicitUrl))
		{
			return WithHealthPath(explicitUrl.Trim());
		}

		string? binding = (aspNetCoreUrls ?? string.Empty)
			.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.FirstOrDefault(candidate => candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

		if (binding is null)
		{
			return FallbackUrl;
		}

		// A wildcard bind is not a dialable host; the probe always talks to loopback.
		string loopback = binding
			.Replace("://+", "://127.0.0.1", StringComparison.Ordinal)
			.Replace("://*", "://127.0.0.1", StringComparison.Ordinal)
			.Replace("://0.0.0.0", "://127.0.0.1", StringComparison.Ordinal)
			.Replace("://[::]", "://127.0.0.1", StringComparison.Ordinal);

		return WithHealthPath(loopback);
	}

	/// <summary>Appends the health path to a bare origin, leaving an explicit path untouched.</summary>
	private static string WithHealthPath(string url)
	{
		string trimmed = url.TrimEnd('/');

		// Anything after the authority counts as a caller-supplied path — respect it.
		int authorityStart = trimmed.IndexOf("://", StringComparison.Ordinal);
		if (authorityStart >= 0 && trimmed.IndexOf('/', authorityStart + 3) >= 0)
		{
			return trimmed;
		}

		return trimmed + HealthPath;
	}

	/// <summary>True when the health payload's <c>status</c> field reads <c>ok</c>.</summary>
	private static bool IsHealthyPayload(string body)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(body);
			return document.RootElement.TryGetProperty("status", out JsonElement status)
				&& string.Equals(status.GetString(), "ok", StringComparison.Ordinal);
		}
		catch (JsonException)
		{
			return false;
		}
	}
}
