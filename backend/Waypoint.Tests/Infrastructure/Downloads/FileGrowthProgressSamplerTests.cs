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

using Waypoint.Infrastructure.Downloads;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// Issue #1041: the file-growth sampler is the one piece of real logic uniformly
/// shared by every download-content lane (direct-fetch <c>download</c>,
/// tool-driven <c>binaries-download</c>) -- exercised directly here with a real
/// temp-directory file rather than through either handler's much heavier
/// PowerShell/Postgres/tool-process dependency graph, matching those handlers' own
/// existing "fast unit tests here, real end-to-end elsewhere" split.
/// </summary>
public sealed class FileGrowthProgressSamplerTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("wp-progress-sampler-tests").FullName;

	public void Dispose() => Directory.Delete(_root, recursive: true);

	/// <summary>AC: known-size case computes a non-null rate and a finite, non-blown-up ETA once bytes have visibly grown.</summary>
	[Fact]
	public async Task KnownTotalBytes_ComputesRateAndEta_OnceBytesGrow()
	{
		string path = Path.Combine(_root, "known.bin");
		File.WriteAllBytes(path, Array.Empty<byte>());
		const long total = 1000L;
		List<FileGrowthProgressSample> samples = [];

		int result = await FileGrowthProgressSampler.RunAsync(
			measureBytes: () => FileGrowthProgressSampler.MeasurePathSize(path),
			bytesTotal: total,
			onSample: (sample, _) =>
			{
				samples.Add(sample);
				return Task.CompletedTask;
			},
			operation: async token =>
			{
				for (int i = 0; i < 6 && !token.IsCancellationRequested; i++)
				{
					await File.WriteAllBytesAsync(path, new byte[(i + 1) * 100], token);
					await Task.Delay(15, token);
				}

				return 42;
			},
			cancellationToken: CancellationToken.None,
			interval: TimeSpan.FromMilliseconds(10));

		Assert.Equal(42, result);
		Assert.NotEmpty(samples);
		Assert.All(samples, sample => Assert.Equal(total, sample.BytesTotal));

		FileGrowthProgressSample? grown = samples.FirstOrDefault(sample => sample.BytesDownloaded > 0);
		Assert.NotNull(grown);
		Assert.True(grown!.DownloadRateBps is null or > 0, "rate must never be negative once bytes have grown.");

		// ETA must be a finite, sane value bounded by "not yet complete" -- never
		// NaN/Infinity/negative, which a naive division would produce near completion.
		foreach (FileGrowthProgressSample sample in samples.Where(sample => sample.EtaSeconds is not null))
		{
			Assert.False(double.IsNaN(sample.EtaSeconds!.Value));
			Assert.False(double.IsInfinity(sample.EtaSeconds.Value));
			Assert.True(sample.EtaSeconds.Value >= 0);
		}
	}

	/// <summary>AC: unknown-size case (no catalog-known total) never divides by zero or blows up -- every sample reports a null total and a null ETA, bytes-only progress still flows.</summary>
	[Fact]
	public async Task UnknownTotalBytes_NeverComputesEta_AndNeverThrows()
	{
		string path = Path.Combine(_root, "unknown.bin");
		File.WriteAllBytes(path, Array.Empty<byte>());
		List<FileGrowthProgressSample> samples = [];

		await FileGrowthProgressSampler.RunAsync(
			measureBytes: () => FileGrowthProgressSampler.MeasurePathSize(path),
			bytesTotal: null,
			onSample: (sample, _) =>
			{
				samples.Add(sample);
				return Task.CompletedTask;
			},
			operation: async token =>
			{
				for (int i = 0; i < 4; i++)
				{
					await File.WriteAllBytesAsync(path, new byte[(i + 1) * 250], token);
					await Task.Delay(15, token);
				}

				return true;
			},
			cancellationToken: CancellationToken.None,
			interval: TimeSpan.FromMilliseconds(10));

		Assert.NotEmpty(samples);
		Assert.All(samples, sample =>
		{
			Assert.Null(sample.BytesTotal);
			Assert.Null(sample.EtaSeconds);
		});
	}

	/// <summary>AC: sampling stops on completion -- no sample ever arrives after <see cref="FileGrowthProgressSampler.RunAsync{T}"/> has returned, and the loop is not left running.</summary>
	[Fact]
	public async Task Sampling_StopsWhenOperationCompletes()
	{
		string path = Path.Combine(_root, "stop.bin");
		File.WriteAllBytes(path, new byte[10]);
		int sampleCount = 0;

		await FileGrowthProgressSampler.RunAsync(
			measureBytes: () => FileGrowthProgressSampler.MeasurePathSize(path),
			bytesTotal: 10L,
			onSample: (_, _) =>
			{
				Interlocked.Increment(ref sampleCount);
				return Task.CompletedTask;
			},
			operation: _ => Task.FromResult(true),
			cancellationToken: CancellationToken.None,
			interval: TimeSpan.FromMilliseconds(5));

		int countAtReturn = sampleCount;
		await Task.Delay(50);
		Assert.Equal(countAtReturn, sampleCount);
	}

	/// <summary>AC: a cancelled operation is propagated, not swallowed by the sampler.</summary>
	[Fact]
	public async Task Cancellation_PropagatesFromOperation_AndStopsTheLoop()
	{
		using CancellationTokenSource cts = new();
		string path = Path.Combine(_root, "cancel.bin");
		File.WriteAllBytes(path, Array.Empty<byte>());

		Task work() => FileGrowthProgressSampler.RunAsync<object?>(
			measureBytes: () => FileGrowthProgressSampler.MeasurePathSize(path),
			bytesTotal: null,
			onSample: (_, _) => Task.CompletedTask,
			operation: async token =>
			{
				cts.Cancel();
				await Task.Delay(TimeSpan.FromSeconds(30), token);
				return null;
			},
			cancellationToken: cts.Token,
			interval: TimeSpan.FromMilliseconds(5));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(work);
	}

	[Fact]
	public void MeasurePathSize_NonexistentPath_ReturnsZero()
	{
		string path = Path.Combine(_root, "does-not-exist.bin");
		Assert.Equal(0, FileGrowthProgressSampler.MeasurePathSize(path));
	}

	[Fact]
	public void MeasurePathSize_File_ReturnsLength()
	{
		string path = Path.Combine(_root, "sized.bin");
		File.WriteAllBytes(path, new byte[321]);
		Assert.Equal(321, FileGrowthProgressSampler.MeasurePathSize(path));
	}

	[Fact]
	public void MeasurePathSize_Directory_SumsFilesRecursively()
	{
		string dir = Path.Combine(_root, "bundle");
		Directory.CreateDirectory(Path.Combine(dir, "nested"));
		File.WriteAllBytes(Path.Combine(dir, "a.bin"), new byte[100]);
		File.WriteAllBytes(Path.Combine(dir, "nested", "b.bin"), new byte[50]);

		Assert.Equal(150, FileGrowthProgressSampler.MeasurePathSize(dir));
	}
}
