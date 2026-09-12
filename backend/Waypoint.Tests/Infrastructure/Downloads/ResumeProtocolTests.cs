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

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Core.Pagination;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.PowerShell;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// Issue #1411: pins the resume-protocol contract PR #1743 rewrote in
/// <c>vcf-download-manager.common.ps1</c>'s <c>Save-WebFile</c> -- the status code and
/// <c>Content-Range</c> header drive the append-vs-restart decision, not the body
/// size -- against a real in-process <see cref="HttpListener"/>, never a mocked
/// <c>Invoke-WebRequest</c>. Matrix rows: docs/reference/download-parity-matrix.md's
/// "Buildable-today subset: the resume protocol" section, RP-01..RP-06 (added by this
/// PR).
///
/// Two invocation shapes, both through the REAL, unmodified module + sibling script:
/// <see cref="DownloadHandlerEndToEnd_FreshDownload_VerifiesAndMarksPresent"/> drives
/// the full <see cref="DownloadJobHandler"/> (real handler -&gt; real
/// <c>WaypointDownload.psm1</c> -&gt; real script) for one fresh, non-resumed download,
/// proving the handler wiring (checksum verification, download/artifact state).
/// Every byte-shape/response-shape assertion (merge, 206, 200-restart, oversize,
/// Content-Range mismatch, 401/403) drives <c>Invoke-WaypointDownload</c> directly at
/// the SAME command/parameter seam <see cref="DownloadJobHandler"/> issues, passing
/// <c>ExpectedSize</c> explicitly. This is a deliberate, documented departure from
/// <see cref="DownloadJobHandler"/>'s own real parameter list (which never sets
/// <c>ExpectedSize</c>, relying on <c>Save-WebFile</c>'s own internal
/// <c>Invoke-WebRequest -Method Head</c> lookup): that internal HEAD lookup was found,
/// empirically and repeatedly, to be unreliable specifically when reached through
/// <c>Save-WebFile</c> in this repository's SDK-hosted (in-process,
/// <c>Microsoft.PowerShell.SDK</c>) runspace -- a raw <c>Invoke-WebRequest -Method
/// Head</c> script probe against the same listener, in the same runspace, succeeds
/// every time, but the identical call reached via <c>Save-WebFile</c> intermittently
/// returns a Content-Length the <c>[long]</c> cast cannot parse, silently swallowed by
/// <c>Save-WebFile</c>'s own <c>catch</c>. The auto-HEAD-based size lookup predates PR
/// #1743 and is not part of the 206-vs-200/Content-Range contract this issue pins;
/// bypassing it with an explicit <c>ExpectedSize</c> keeps these tests deterministic
/// without depending on an unreliable, orthogonal code path. Filed as deferred issue
/// #1800 rather than root-caused here -- #1411 is a test-only issue.
///
/// No Postgres: <see cref="IDownloadRepository"/>/<see cref="IDepotArtifactRepository"/>
/// are in-memory fakes (both are already-Postgres-free interfaces -- see
/// <c>ToolGatedDownloadJobHandlerTests</c> for the same no-Postgres convention on this
/// handler family), and the job-runner repository the <see cref="JobExecutionContext"/>
/// constructor requires is never called: <see cref="DownloadJobHandler"/> is a
/// <see cref="JobShape.Simple"/> handler, which never calls
/// <see cref="JobExecutionContext.AdvanceAsync"/> (see that class's own doc comment).
/// </summary>
#pragma warning disable CA1001 // pool/executor lifecycle is scoped per test method via `using`, not a field.
public sealed class ResumeProtocolTests
#pragma warning restore CA1001
{
	private static readonly string LoggingModulePath = Path.Combine(
		AppContext.BaseDirectory, "..", "..", "..", "..",
		"Waypoint.Infrastructure.Execution", "PowerShell", "Modules", "WaypointLogging", "WaypointLogging.psm1");

	private static readonly string DownloadModulePath = Path.Combine(
		AppContext.BaseDirectory, "..", "..", "..", "..",
		"Waypoint.Infrastructure.Execution", "PowerShell", "Modules", "WaypointDownload", "WaypointDownload.psm1");

	/// <summary>
	/// The REAL, unmodified sibling-repository script (License & Borrowing Policy: not
	/// vendored, owner-authored) -- deliberately not a fake/stub, so a regression in the
	/// PR #1743 contract fails this suite instead of a hand-written stand-in that
	/// encodes the same wrong assumption the production code has (PR #1629/#1638
	/// review lesson).
	/// </summary>
	private static readonly string RealCommonScriptPath = Path.Combine(
		AppContext.BaseDirectory, "..", "..", "..", "..", "..",
		"runners", "download-runner", "powershell", "project", "vcf-download-manager.common.ps1");

	private static readonly byte[] FullBytes = Encoding.ASCII.GetBytes("ABCDEFGHIJKL"); // invented 12-byte fixture body
	private static readonly string FullSha256 = Convert.ToHexString(SHA256.HashData(FullBytes));

	public ResumeProtocolTests()
	{
		Assert.True(File.Exists(Path.GetFullPath(LoggingModulePath)), $"expected the adapter at '{Path.GetFullPath(LoggingModulePath)}'");
		Assert.True(File.Exists(Path.GetFullPath(DownloadModulePath)), $"expected WaypointDownload.psm1 at '{Path.GetFullPath(DownloadModulePath)}'");
		Assert.True(File.Exists(Path.GetFullPath(RealCommonScriptPath)), $"expected the real sibling script at '{Path.GetFullPath(RealCommonScriptPath)}'");
	}

	// ---- fakes: no Postgres for this handler-level suite (mirrors ToolGatedDownloadJobHandlerTests) ----

	private sealed class RecordingLogBuffer : IJobLogBuffer
	{
		public ConcurrentQueue<(string EventType, Guid? JobId, Guid? RunId, string Payload)> Events { get; } = new();

		public bool TryEnqueue(string eventType, Guid? jobId, Guid? runId, string payloadJson)
		{
			Events.Enqueue((eventType, jobId, runId, payloadJson));
			return true;
		}
	}

	private sealed class FakeEventPublisher : IJobEventPublisher
	{
		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken) => Task.CompletedTask;
	}

	private sealed class FakeDownloadRepository : IDownloadRepository
	{
		public Download Row = null!;
		public string? LastState;

		public Task<Guid> CreateAsync(Guid depotArtifactId, Guid? jobId, string? requestedBy, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task SetJobAsync(Guid downloadId, Guid jobId, Guid runId, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<Download?> GetAsync(Guid downloadId, CancellationToken cancellationToken) => Task.FromResult<Download?>(Row);

		public Task<Download?> GetByJobIdAsync(Guid jobId, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task UpdateProgressAsync(
			Guid downloadId, string state, long? bytesTotal, long? bytesDownloaded, long? downloadRateBps, int? etaSeconds,
			string? failureReason, CancellationToken cancellationToken)
		{
			LastState = state;
			return Task.CompletedTask;
		}

		public Task<int> IncrementRetryCountAsync(Guid downloadId, CancellationToken cancellationToken) => Task.FromResult(1);

		public Task<(IReadOnlyList<Download> Items, long TotalCount)> ListAsync(PageRequest page, CancellationToken cancellationToken) =>
			throw new NotSupportedException();
	}

	private sealed class FakeDepotArtifactRepository : IDepotArtifactRepository
	{
		public DepotArtifact Row = null!;
		public string? LastUpsertStatus;

		public Task<Guid> UpsertAsync(DepotArtifactUpsert artifact, CancellationToken cancellationToken)
		{
			LastUpsertStatus = artifact.Status;
			return Task.FromResult(Row.Id);
		}

		public Task<DepotArtifact?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<DepotArtifact?>(Row);

		public Task<(IReadOnlyList<DepotArtifact> Items, long TotalCount)> ListAsync(
			DepotArtifactFilter filter, PageRequest page, CancellationToken cancellationToken) =>
			Task.FromResult<(IReadOnlyList<DepotArtifact>, long)>(([Row], 1));

		public Task<int> RekeyManyAsync(IReadOnlyDictionary<string, string> renames, CancellationToken cancellationToken) => throw new NotSupportedException();
	}

	/// <summary>
	/// A single-request-at-a-time in-process HTTP server: real sockets, real status
	/// codes/headers -- no <c>Invoke-WebRequest</c> mock anywhere on this path.
	/// </summary>
	private sealed class ScenarioHttpServer : IDisposable
	{
		private readonly HttpListener _listener;
		private readonly Func<HttpListenerRequest, HttpListenerResponse, Task> _handle;
		private readonly CancellationTokenSource _cts = new();
		private readonly Task _acceptLoop;

		public int Port { get; }

		public ConcurrentQueue<(string Method, string? RangeHeader)> Requests { get; } = new();

		public ScenarioHttpServer(Func<HttpListenerRequest, HttpListenerResponse, Task> handle)
		{
			_handle = handle;
			Port = GetFreeTcpPort();
			_listener = new HttpListener();
			_listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
			_listener.Start();
			_acceptLoop = Task.Run(AcceptLoopAsync);
		}

		public string ArtifactUrl => $"http://127.0.0.1:{Port}/artifact.bin";

		/// <summary>Probe-then-bind: a port-0 <see cref="TcpListener"/> reserves a free
		/// ephemeral port and releases it before <see cref="HttpListener"/> binds the
		/// same number, so a busy host sharing that range can rarely take it first.</summary>
		private static int GetFreeTcpPort()
		{
			TcpListener probe = new(IPAddress.Loopback, 0);
			probe.Start();
			int port = ((IPEndPoint)probe.LocalEndpoint).Port;
			probe.Stop();
			return port;
		}

		private async Task AcceptLoopAsync()
		{
			while (!_cts.IsCancellationRequested)
			{
				HttpListenerContext context;
				try
				{
					context = await _listener.GetContextAsync().ConfigureAwait(false);
				}
				catch
				{
					return;
				}

				Requests.Enqueue((context.Request.HttpMethod, context.Request.Headers["Range"]));
				try
				{
					await _handle(context.Request, context.Response).ConfigureAwait(false);
				}
				finally
				{
					context.Response.OutputStream.Close();
				}
			}
		}

		public void Dispose()
		{
			_cts.Cancel();
			try
			{
				_listener.Stop();
				_listener.Close();
			}
			catch (HttpListenerException)
			{
				// Cleanup only -- the test's own assertions have already run by this
				// point. Under heavy same-host contention (several full test-process
				// runs sharing the ephemeral port range, see GetFreeTcpPort's own
				// probe-then-bind comment) a stale prefix registration can make
				// Close() throw "Address already in use"; that must never turn an
				// otherwise-passing test's teardown into a reported failure.
			}
		}
	}

	/// <summary>
	/// <c>WaypointDownload.psm1</c> reads <c>WAYPOINT_VCF_DOWNLOAD_MANAGER_COMMON_PATH</c>
	/// at import time into a module-scoped variable (mirrors
	/// <c>CatalogIndexJobHandlerRealModuleEndToEndTests</c>' own set/restore around
	/// runspace-pool creation) -- <see cref="DownloadJobHandler"/>'s real
	/// <c>Invoke-WaypointDownload</c> call never passes the path parameter explicitly
	/// (production relies on the env var deploy/compose.yaml sets), so the module must
	/// see it before this preload happens.
	/// </summary>
	private static async Task<(PowerShellExecutor Executor, WaypointRunspacePool Pool)> CreateExecutorAsync(RecordingLogBuffer buffer)
	{
		string? previous = Environment.GetEnvironmentVariable("WAYPOINT_VCF_DOWNLOAD_MANAGER_COMMON_PATH");
		Environment.SetEnvironmentVariable("WAYPOINT_VCF_DOWNLOAD_MANAGER_COMMON_PATH", Path.GetFullPath(RealCommonScriptPath));
		try
		{
			PowerShellOptions options = new() { MaxRunspaces = 1, DefaultInvocationTimeout = TimeSpan.FromSeconds(30) };
			options.ModulePreloadPaths.Add(Path.GetFullPath(LoggingModulePath));
			options.ModulePreloadPaths.Add(Path.GetFullPath(DownloadModulePath));
			IOptions<PowerShellOptions> wrapped = Options.Create(options);
			WaypointRunspacePool pool = new(wrapped, NullLogger<WaypointRunspacePool>.Instance);
			PowerShellExecutor executor = new(pool, buffer, wrapped, NullLogger<PowerShellExecutor>.Instance);

			// Module preload is lazy (the pool imports on the FIRST runspace open, not
			// in its constructor) -- force that first open now, with a trivial no-op
			// command, while the env var above is still in scope. Without this warmup,
			// the env var would already be restored to its previous value (below) by
			// the time DownloadJobHandler's real invocation lazily triggers the import.
			await executor.ExecuteAsync(
				new PowerShellRequest("Get-Command", Parameters: new Dictionary<string, object?> { ["Name"] = "Invoke-WaypointDownload" }),
				CancellationToken.None);

			return (executor, pool);
		}
		finally
		{
			Environment.SetEnvironmentVariable("WAYPOINT_VCF_DOWNLOAD_MANAGER_COMMON_PATH", previous);
		}
	}

	private static JobExecutionContext BuildContext(Guid jobId, string payloadJson)
	{
		ClaimedJob job = new(
			Id: jobId, RunId: null, JobType: "download", TargetId: null, TargetName: null,
			CredentialId: null, Priority: 1, Payload: payloadJson, AttemptCount: 1, MaxAttempts: 3);
		return new JobExecutionContext(
			job, "worker-test", new FakeEventPublisher(),
			new JobQueueRepository("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x", NullLogger<JobQueueRepository>.Instance),
			JobShape.Simple);
	}

	private static (FakeDownloadRepository Downloads, FakeDepotArtifactRepository Artifacts, DownloadJobHandler Handler, string StoreDir)
		CreateHandler(PowerShellExecutor executor, string? sha256)
	{
		string storeDir = Directory.CreateTempSubdirectory("wp-resume-protocol").FullName;
		Guid downloadId = Guid.NewGuid();
		Guid artifactId = Guid.NewGuid();

		FakeDownloadRepository downloads = new()
		{
			Row = new Download(
				downloadId, artifactId, JobId: null, RunId: null, State: DownloadStates.Queued,
				BytesTotal: null, BytesDownloaded: 0, DownloadRateBps: null, EtaSeconds: null,
				RetryCount: 0, MaxRetries: 3, FailureReason: null, RequestedBy: "tester",
				CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow, CompletedAt: null),
		};
		FakeDepotArtifactRepository artifacts = new()
		{
			Row = new DepotArtifact(
				artifactId, ExternalId: "artifact.bin", Sha256: sha256, Status: DepotArtifactStatuses.Indexed,
				Product: null, Version: null, MetadataJson: "{}", IndexedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow),
		};

		DownloadOptions options = new() { ArtifactStorePath = storeDir };
		DownloadJobHandler handler = new(executor, downloads, artifacts, Options.Create(options));
		return (downloads, artifacts, handler, storeDir);
	}

	private static string PayloadFor(FakeDownloadRepository downloads, string sourceUrl) =>
		JsonSerializer.Serialize(new { download_id = downloads.Row.Id, depot_artifact_id = downloads.Row.DepotArtifactId, source_url = sourceUrl });

	/// <summary>
	/// Drives <c>Invoke-WaypointDownload</c> directly -- the same command
	/// <see cref="DownloadJobHandler"/> issues -- with <c>ExpectedSize</c> passed
	/// explicitly (see this class's own header comment for why).
	/// </summary>
	private static Task<PowerShellExecutionResult> InvokeDownloadAsync(
		PowerShellExecutor executor, string url, string outFile, long expectedSize, int retryCount = 3) =>
		executor.ExecuteAsync(
			new PowerShellRequest(
				"Invoke-WaypointDownload",
				Parameters: new Dictionary<string, object?>
				{
					["Url"] = url,
					["OutFile"] = outFile,
					["ExpectedSize"] = expectedSize,
					["RetryCount"] = retryCount,
					["Source"] = "depot",
					["VcfDownloadManagerCommonPath"] = Path.GetFullPath(RealCommonScriptPath),
				}),
			CancellationToken.None);

	/// <summary>
	/// A real <see cref="HttpListener"/> requires the promised <c>Content-Length</c>
	/// byte count to actually be written before the response closes -- setting
	/// <see cref="HttpListenerResponse.ContentLength64"/> without writing a matching
	/// body causes the client to see a truncated response. <c>HEAD</c> is otherwise
	/// body-less over the wire (.NET's <c>HttpClient</c> discards it for a HEAD
	/// request), so this filler content is never actually read by the caller.
	/// </summary>
	private static async Task WriteHeadAsync(HttpListenerResponse response, long contentLength)
	{
		response.StatusCode = 200;
		response.ContentLength64 = contentLength;
		await response.OutputStream.WriteAsync(new byte[contentLength]);
	}

	/// <summary>
	/// Handler-wiring proof: the REAL <see cref="DownloadJobHandler"/> drives the REAL
	/// <c>WaypointDownload.psm1</c> -&gt; the real sibling script for one fresh
	/// (non-resumed) download, verifying its OWN independent sha256 check and marking
	/// the artifact <c>present</c>. This is deliberately the one scenario that does
	/// NOT depend on <c>Save-WebFile</c>'s resume/range branch (no partial file
	/// pre-exists, so <see cref="DownloadJobHandler"/> never needing to pass
	/// <c>ExpectedSize</c> costs nothing here) -- every resume-specific byte/response
	/// assertion lives in the direct-invocation tests below (see class header).
	/// </summary>
	[Fact]
	public async Task DownloadHandlerEndToEnd_FreshDownload_VerifiesAndMarksPresent()
	{
		RecordingLogBuffer logBuffer = new();
		(PowerShellExecutor executor, WaypointRunspacePool pool) = await CreateExecutorAsync(logBuffer);
		using IDisposable _ = pool;

		using ScenarioHttpServer server = new(async (request, response) =>
		{
			if (request.HttpMethod == "HEAD")
			{
				await WriteHeadAsync(response, FullBytes.Length);
				return;
			}

			response.StatusCode = 200;
			response.ContentLength64 = FullBytes.Length;
			await response.OutputStream.WriteAsync(FullBytes);
		});

		(FakeDownloadRepository downloads, FakeDepotArtifactRepository artifacts, DownloadJobHandler handler, string storeDir) =
			CreateHandler(executor, FullSha256);
		string destinationPath = Path.Combine(storeDir, artifacts.Row.ExternalId);

		JobExecutionContext context = BuildContext(Guid.NewGuid(), PayloadFor(downloads, server.ArtifactUrl));
		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.True(outcome.Kind == JobOutcomeKind.Succeeded, outcome.Note);
		Assert.Equal(FullBytes, await File.ReadAllBytesAsync(destinationPath));
		Assert.Equal(DepotArtifactStatuses.Present, artifacts.LastUpsertStatus);

		Directory.Delete(storeDir, recursive: true);
	}

	/// <summary>
	/// RP-01 (matrix): a leftover <c>.resume.tmp</c> from a previously interrupted
	/// ranged download is merged into the partial <c>OutFile</c> BEFORE the retry loop
	/// runs at all (vcf-download-manager.common.ps1 lines ~672-685) -- when the merge
	/// alone reaches the expected size, <c>Save-WebFile</c> returns success without
	/// ever issuing a GET.
	/// </summary>
	[Fact]
	public async Task TmpFileMerge_LeftoverResumeTmp_ProducesFullBytes_NoGetRequest_AndCleansUpTempFile()
	{
		RecordingLogBuffer logBuffer = new();
		(PowerShellExecutor executor, WaypointRunspacePool pool) = await CreateExecutorAsync(logBuffer);
		using IDisposable _ = pool;

		using ScenarioHttpServer server = new((_, _) => Task.CompletedTask); // no request should ever reach the listener

		string storeDir = Directory.CreateTempSubdirectory("wp-resume-protocol-merge").FullName;
		string destinationPath = Path.Combine(storeDir, "artifact.bin");
		await File.WriteAllBytesAsync(destinationPath, FullBytes[..5]);
		string tempPath = destinationPath + ".resume.tmp";
		await File.WriteAllBytesAsync(tempPath, FullBytes[5..]);

		PowerShellExecutionResult result = await InvokeDownloadAsync(executor, server.ArtifactUrl, destinationPath, expectedSize: FullBytes.Length);

		Assert.True(result.Succeeded, result.FailureReason);
		Assert.Equal(FullBytes, await File.ReadAllBytesAsync(destinationPath));
		Assert.False(File.Exists(tempPath), "the leftover .resume.tmp must be cleaned up after merge");
		Assert.Empty(server.Requests);

		Directory.Delete(storeDir, recursive: true);
	}

	/// <summary>
	/// RP-02 (matrix): a genuine 206 response, Content-Range start confirmed against
	/// the partial size, appends the remainder to produce the exact full byte
	/// sequence (PR #1743's Content-Range confirmation, not a body-size heuristic).
	/// </summary>
	[Fact]
	public async Task Resume206_AppendsFromConfirmedOffset_ByteForByte()
	{
		RecordingLogBuffer logBuffer = new();
		(PowerShellExecutor executor, WaypointRunspacePool pool) = await CreateExecutorAsync(logBuffer);
		using IDisposable _ = pool;

		using ScenarioHttpServer server = new(async (request, response) =>
		{
			Assert.Equal("bytes=5-", request.Headers["Range"]);
			response.StatusCode = 206;
			response.Headers.Add("Content-Range", "bytes 5-11/12");
			byte[] remainder = FullBytes[5..];
			response.ContentLength64 = remainder.Length;
			await response.OutputStream.WriteAsync(remainder);
		});

		string storeDir = Directory.CreateTempSubdirectory("wp-resume-protocol-206").FullName;
		string destinationPath = Path.Combine(storeDir, "artifact.bin");
		await File.WriteAllBytesAsync(destinationPath, FullBytes[..5]);

		PowerShellExecutionResult result = await InvokeDownloadAsync(executor, server.ArtifactUrl, destinationPath, expectedSize: FullBytes.Length);

		Assert.True(result.Succeeded, result.FailureReason);
		Assert.Equal(FullBytes, await File.ReadAllBytesAsync(destinationPath));
		Assert.False(File.Exists(destinationPath + ".resume.tmp"));
		Assert.Equal(1, server.Requests.Count(r => r.Method == "GET"));

		Directory.Delete(storeDir, recursive: true);
	}

	/// <summary>
	/// RP-03 (matrix): a real edge-cache-style 200 answer to a ranged request restarts
	/// the file from zero -- never appends -- and logs a Warning (issue #1169/PR #1743).
	/// </summary>
	[Fact]
	public async Task RangedRequestAnsweredWith200_RestartsFromZero_AndLogsWarning()
	{
		RecordingLogBuffer logBuffer = new();
		(PowerShellExecutor executor, WaypointRunspacePool pool) = await CreateExecutorAsync(logBuffer);
		using IDisposable _ = pool;

		using ScenarioHttpServer server = new(async (request, response) =>
		{
			// Server ignores Range entirely and answers with the full body -- observed
			// live from an edge cache (research #1030); never merge/append this.
			response.StatusCode = 200;
			response.ContentLength64 = FullBytes.Length;
			await response.OutputStream.WriteAsync(FullBytes);
		});

		string storeDir = Directory.CreateTempSubdirectory("wp-resume-protocol-200restart").FullName;
		string destinationPath = Path.Combine(storeDir, "artifact.bin");
		await File.WriteAllBytesAsync(destinationPath, Encoding.ASCII.GetBytes("ZZZZZ")); // wrong prefix -- must not survive

		PowerShellExecutionResult result = await InvokeDownloadAsync(executor, server.ArtifactUrl, destinationPath, expectedSize: FullBytes.Length);

		Assert.True(result.Succeeded, result.FailureReason);
		Assert.Equal(FullBytes, await File.ReadAllBytesAsync(destinationPath));
		Assert.Contains(
			logBuffer.Events,
			e => e.Payload.Contains("answered with 200 instead of 206", StringComparison.Ordinal) && Severity(e.Payload) == "warning");

		Directory.Delete(storeDir, recursive: true);
	}

	/// <summary>
	/// RP-04 (matrix): an oversize response (longer than the declared/expected size)
	/// is rejected outright -- the file is removed, never truncated or merged as a
	/// "success".
	/// </summary>
	[Fact]
	public async Task OversizeResponse_IsRejected_NotTruncatedNorMerged()
	{
		RecordingLogBuffer logBuffer = new();
		(PowerShellExecutor executor, WaypointRunspacePool pool) = await CreateExecutorAsync(logBuffer);
		using IDisposable _ = pool;

		byte[] oversizeBody = Encoding.ASCII.GetBytes("012345678901234"); // 15 bytes; declared/expected size is 10
		using ScenarioHttpServer server = new(async (request, response) =>
		{
			response.StatusCode = 200;
			response.ContentLength64 = oversizeBody.Length;
			await response.OutputStream.WriteAsync(oversizeBody);
		});

		string storeDir = Directory.CreateTempSubdirectory("wp-resume-protocol-oversize").FullName;
		string destinationPath = Path.Combine(storeDir, "artifact.bin");

		PowerShellExecutionResult result = await InvokeDownloadAsync(executor, server.ArtifactUrl, destinationPath, expectedSize: 10, retryCount: 1);

		Assert.False(result.Succeeded);
		Assert.Contains("Size mismatch", result.FailureReason, StringComparison.Ordinal);
		Assert.False(File.Exists(destinationPath), "an oversize response must never be left on disk as if it succeeded");

		Directory.Delete(storeDir, recursive: true);
	}

	/// <summary>
	/// RP-06 (matrix): a 206 response whose <c>Content-Range</c> start disagrees with
	/// the requested/partial offset is rejected outright (PR #1743's mismatch guard),
	/// never silently appended at the wrong offset. <c>RetryCount=1</c> -- the
	/// mismatch is NOT recognized as an auth/disk error by <c>Save-WebFile</c>'s catch
	/// block, so it falls into the generic "retry with backoff" bucket; RetryCount=1
	/// keeps this deterministic and fast (single attempt, no sleep) while still
	/// proving the throw.
	/// </summary>
	[Fact]
	public async Task RangedResponseContentRangeStartMismatch_Throws()
	{
		RecordingLogBuffer logBuffer = new();
		(PowerShellExecutor executor, WaypointRunspacePool pool) = await CreateExecutorAsync(logBuffer);
		using IDisposable _ = pool;

		using ScenarioHttpServer server = new(async (request, response) =>
		{
			response.StatusCode = 206;
			response.Headers.Add("Content-Range", "bytes 2-11/12"); // wrong start; requested offset is 5
			byte[] body = Encoding.ASCII.GetBytes("0123456789");
			response.ContentLength64 = body.Length;
			await response.OutputStream.WriteAsync(body);
		});

		string storeDir = Directory.CreateTempSubdirectory("wp-resume-protocol-mismatch").FullName;
		string destinationPath = Path.Combine(storeDir, "artifact.bin");
		await File.WriteAllBytesAsync(destinationPath, FullBytes[..5]);

		PowerShellExecutionResult result = await InvokeDownloadAsync(executor, server.ArtifactUrl, destinationPath, expectedSize: FullBytes.Length, retryCount: 1);

		Assert.False(result.Succeeded);
		Assert.Contains("Content-Range starting at 2", result.FailureReason, StringComparison.Ordinal);

		Directory.Delete(storeDir, recursive: true);
	}

	/// <summary>
	/// RP-05 (matrix): a 401/403 response is documented (docs/reference/download-parity-matrix.md,
	/// <c>vcf-download-manager.common.ps1</c>'s own "Auth errors - don't retry" comment)
	/// as non-retryable. Driven with a REAL listener rather than the Pester suite's
	/// mocks, this now passes: issue #1799 taught <c>Save-WebFile</c>'s auth-error
	/// branch to also recognize <c>Microsoft.PowerShell.Commands.HttpResponseException</c>
	/// -- the type pwsh7's <c>Invoke-WebRequest</c> actually throws for a non-2xx
	/// response, never the Windows PowerShell 5.1 <c>System.Net.WebException</c> shape
	/// the branch matched exclusively before. A real 401/403 now fails on the first
	/// attempt, with "Authentication error" in the failure reason, instead of falling
	/// into the generic "transient - retry with backoff" bucket.
	/// </summary>
	[Theory]
	[InlineData(401)]
	[InlineData(403)]
	public async Task AuthFailureStatusCode_FailsImmediately_UnderRealHttpResponseException(int statusCode)
	{
		RecordingLogBuffer logBuffer = new();
		(PowerShellExecutor executor, WaypointRunspacePool pool) = await CreateExecutorAsync(logBuffer);
		using IDisposable _ = pool;

		using ScenarioHttpServer server = new(async (request, response) =>
		{
			response.StatusCode = statusCode;
			byte[] body = Encoding.ASCII.GetBytes("nope");
			response.ContentLength64 = body.Length;
			await response.OutputStream.WriteAsync(body);
		});

		string storeDir = Directory.CreateTempSubdirectory("wp-resume-protocol-auth").FullName;
		string destinationPath = Path.Combine(storeDir, "artifact.bin");

		PowerShellExecutionResult result = await InvokeDownloadAsync(executor, server.ArtifactUrl, destinationPath, expectedSize: 10, retryCount: 2);

		Assert.False(result.Succeeded);
		Assert.Contains("Authentication error", result.FailureReason, StringComparison.Ordinal);
		Assert.Equal(1, server.Requests.Count(r => r.Method == "GET"));

		Directory.Delete(storeDir, recursive: true);
	}

	private static string Severity(string payload)
	{
		using JsonDocument document = JsonDocument.Parse(payload);
		return document.RootElement.GetProperty("severity").GetString()!;
	}
}
