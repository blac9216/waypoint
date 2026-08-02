using System.Net;
using System.Text;
using Waypoint.Api.Diagnostics;
using Waypoint.Tests.Support;

namespace Waypoint.Tests.Api;

/// <summary>
/// The probe's verdict logic, exercised in-process against a stub HTTP server so every
/// branch (non-200, wrong payload, unparseable payload, unreachable) is asserted without
/// spawning a process per case. <see cref="HealthCheckProbeTests"/> covers the same
/// mechanism end-to-end through the real binary's exit code.
/// </summary>
public sealed class HealthCheckProbeVerdictTests
{
	[Fact]
	public async Task Healthy200_ReturnsZero()
	{
		int verdict = await ProbeAsync(HttpStatusCode.OK, "{\"status\":\"ok\",\"version\":\"0.0.0-dev\"}");

		Assert.Equal(0, verdict);
	}

	[Fact]
	public async Task NonSuccessStatus_ReturnsOne()
	{
		int verdict = await ProbeAsync(HttpStatusCode.ServiceUnavailable, "{\"status\":\"ok\"}");

		Assert.Equal(1, verdict);
	}

	[Fact]
	public async Task Status200ButNotOk_ReturnsOne()
	{
		// A 200 is not enough — the payload has to actually say the app is healthy.
		int verdict = await ProbeAsync(HttpStatusCode.OK, "{\"status\":\"degraded\"}");

		Assert.Equal(1, verdict);
	}

	[Fact]
	public async Task Status200WithUnparseableBody_ReturnsOne()
	{
		int verdict = await ProbeAsync(HttpStatusCode.OK, "not json at all");

		Assert.Equal(1, verdict);
	}

	[Fact]
	public async Task Status200WithoutAStatusField_ReturnsOne()
	{
		int verdict = await ProbeAsync(HttpStatusCode.OK, "{\"version\":\"0.0.0-dev\"}");

		Assert.Equal(1, verdict);
	}

	[Fact]
	public async Task NothingListening_ReturnsOne()
	{
		int verdict = await HealthCheckProbe.RunAsync(
			$"http://127.0.0.1:{ApiProcess.GetFreePort()}/api/v1/health");

		Assert.Equal(1, verdict);
	}

	/// <summary>Serves one canned response and returns the probe's verdict for it.</summary>
	/// <param name="statusCode">Status the stub server replies with.</param>
	/// <param name="body">Body the stub server replies with.</param>
	private static async Task<int> ProbeAsync(HttpStatusCode statusCode, string body)
	{
		int port = ApiProcess.GetFreePort();
		string prefix = $"http://127.0.0.1:{port}/";

		using HttpListener listener = new();
		listener.Prefixes.Add(prefix);
		listener.Start();

		Task serve = Task.Run(async () =>
		{
			HttpListenerContext context = await listener.GetContextAsync();
			byte[] payload = Encoding.UTF8.GetBytes(body);
			context.Response.StatusCode = (int)statusCode;
			context.Response.ContentType = "application/json; charset=utf-8";
			context.Response.ContentLength64 = payload.Length;
			await context.Response.OutputStream.WriteAsync(payload);
			context.Response.Close();
		});

		try
		{
			return await HealthCheckProbe.RunAsync($"{prefix}api/v1/health");
		}
		finally
		{
			listener.Stop();
			await serve.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
		}
	}
}
