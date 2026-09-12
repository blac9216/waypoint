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

namespace Waypoint.Infrastructure.Downloads;

/// <summary>
/// Issue #1041 (Epic #16 decision Q20): real download progress via file-growth
/// sampling, uniform across every acquisition lane (<c>DownloadJobHandler</c>'s direct
/// HTTP fetch, <c>BinariesDownloadJobHandler</c>'s tool-driven fetch, and any future
/// lane) -- the vcf-download-tool buffers stdout without a TTY, so parsing tool output
/// for progress is unreliable (issue #719); this samples the destination path's size
/// on disk instead, which is truthful regardless of what the acquiring process does
/// with its own stdout.
///
/// A caller starts sampling via <see cref="RunAsync{T}"/>, which runs
/// <paramref name="operation"/> (the actual PowerShell/tool invocation) to completion
/// while a background loop measures <paramref name="measureBytes"/> on
/// <paramref name="interval"/> and reports each sample to <paramref name="onSample"/>.
/// The loop is always stopped (and awaited, so a caller never races its own next step
/// against a still-running sampler) before this method returns or throws, satisfying
/// this issue's "sampling overhead bounded... stops on completion/cancel" acceptance
/// criterion -- there is no scenario in which the loop outlives the operation it
/// samples.
///
/// Rate is the average since sampling started (bytes grown / elapsed), not an
/// instantaneous delta between the last two samples -- this avoids a divide-by-zero
/// or spurious spike from a single slow tick (e.g. filesystem metadata caching) and
/// settles to the real throughput over a multi-GB transfer. ETA is null whenever
/// <paramref name="bytesTotal"/> is unknown (an artifact indexed without a known size)
/// or the rate is not yet positive -- never a divide-by-zero, never a blown-up value.
/// </summary>
public static class FileGrowthProgressSampler
{
	/// <summary>
	/// Default sampling cadence: frequent enough that a multi-GB transfer shows live
	/// movement, infrequent enough that stat-ing the destination path never becomes
	/// meaningful overhead next to the transfer itself.
	/// </summary>
	public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

	public static async Task<T> RunAsync<T>(
		Func<long> measureBytes,
		long? bytesTotal,
		Func<FileGrowthProgressSample, CancellationToken, Task> onSample,
		Func<CancellationToken, Task<T>> operation,
		CancellationToken cancellationToken,
		TimeSpan? interval = null)
	{
		ArgumentNullException.ThrowIfNull(measureBytes);
		ArgumentNullException.ThrowIfNull(onSample);
		ArgumentNullException.ThrowIfNull(operation);

		using CancellationTokenSource loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		Task loop = SampleLoopAsync(measureBytes, bytesTotal, interval ?? DefaultInterval, onSample, loopCts.Token);

		try
		{
			return await operation(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			// Always stop and await the loop before returning/throwing -- the AC is
			// "stops on completion/cancel", not "eventually stops": a caller's very
			// next step (e.g. reading the final file size for verification) must never
			// race a still-ticking sampler over the same path.
			loopCts.Cancel();
			try
			{
				await loop.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				// Expected: the loop's own Task.Delay observes loopCts and unwinds here.
			}
		}
	}

	/// <summary>Sums a file's length, or a directory's files recursively (issue #1041: "file or dir" sizing). Zero for a path that does not exist yet -- a destination not yet created is not a measurement error.</summary>
	public static long MeasurePathSize(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				return new FileInfo(path).Length;
			}

			if (!Directory.Exists(path))
			{
				return 0;
			}

			long total = 0;
			foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
			{
				try
				{
					total += new FileInfo(file).Length;
				}
				catch (IOException)
				{
					// A file mid-write/mid-rename between EnumerateFiles listing it and
					// stat-ing it -- best-effort sizing, never fails the sample over it.
				}
				catch (UnauthorizedAccessException)
				{
				}
			}

			return total;
		}
		catch (IOException)
		{
			return 0;
		}
		catch (UnauthorizedAccessException)
		{
			return 0;
		}
	}

	private static async Task SampleLoopAsync(
		Func<long> measureBytes,
		long? bytesTotal,
		TimeSpan interval,
		Func<FileGrowthProgressSample, CancellationToken, Task> onSample,
		CancellationToken cancellationToken)
	{
		long startBytes = SafeMeasure(measureBytes);
		long startTimestamp = Environment.TickCount64;

		while (true)
		{
			await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

			long currentBytes = SafeMeasure(measureBytes);
			double elapsedSeconds = (Environment.TickCount64 - startTimestamp) / 1000.0;

			double? rateBytesPerSecond = elapsedSeconds > 0 && currentBytes > startBytes
				? (currentBytes - startBytes) / elapsedSeconds
				: null;

			double? etaSeconds = rateBytesPerSecond is > 0 && bytesTotal is long total && total > currentBytes
				? (total - currentBytes) / rateBytesPerSecond
				: null;

			FileGrowthProgressSample sample = new(currentBytes, bytesTotal, rateBytesPerSecond, etaSeconds);

			try
			{
				await onSample(sample, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception) when (!cancellationToken.IsCancellationRequested)
			{
				// Best-effort reporting: a transient failure emitting/persisting one
				// sample (e.g. a momentary DB hiccup) must never abort the sampling
				// loop or the download it is observing.
			}
		}
	}

	private static long SafeMeasure(Func<long> measureBytes)
	{
		try
		{
			return measureBytes();
		}
		catch (IOException)
		{
			return 0;
		}
		catch (UnauthorizedAccessException)
		{
			return 0;
		}
	}
}

/// <summary>One file-growth sample: bytes observed on disk against the catalog-known total (if any), with the average rate and ETA computed against it.</summary>
public sealed record FileGrowthProgressSample(long BytesDownloaded, long? BytesTotal, double? DownloadRateBps, double? EtaSeconds);
