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

using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Waypoint.Core.Downloads;

namespace Waypoint.Infrastructure.Downloads;

/// <inheritdoc cref="IManagedToolMetadataPuller"/>
/// <remarks>
/// Mirrors <see cref="DepotIdentityTool"/>'s bounded noninteractive process pattern
/// (stdin closed immediately, linked timeout/cancellation, hard kill on timeout) and
/// its HOME/XDG_DATA_HOME identity isolation, so a catalog pull authenticates as the
/// same stable machine identity issue #691's enrollment flow already validated.
/// </remarks>
public sealed class ManagedToolMetadataPuller : IManagedToolMetadataPuller
{
	private readonly IOptions<ManagedToolOptions> _options;
	private readonly IManagedToolPresenceChecker _presenceChecker;

	public ManagedToolMetadataPuller(IOptions<ManagedToolOptions> options, IManagedToolPresenceChecker presenceChecker)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(presenceChecker);
		_options = options;
		_presenceChecker = presenceChecker;
	}

	public async Task<CatalogPullResult> PullAsync(string depotPath, string activationCodePath, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(depotPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(activationCodePath);

		if (!_presenceChecker.IsPresent())
		{
			return CatalogPullResult.Failed(
				$"vcf-download-tool is not installed (expected at '{_presenceChecker.DescribeExpectedLocation()}'). Install the managed tool before pulling the vendor catalog.");
		}

		ManagedToolOptions options = _options.Value;
		string identityHome = PrepareIdentityHome(options);
		string arguments = $"metadata download -d \"{depotPath}\" \"--depot-download-activation-code-file={activationCodePath}\" --ceip=DISABLE";

		(bool succeeded, int exitCode, string stdout, string stderr) = await RunAsync(
			ExecutablePath(options), arguments, identityHome, options, cancellationToken).ConfigureAwait(false);

		if (!succeeded)
		{
			return CatalogPullResult.Failed($"metadata download could not be started or timed out: {stderr}");
		}

		if (exitCode == 0)
		{
			return CatalogPullResult.Ok();
		}

		// Same convention DepotIdentityTool.ValidateActivationCodeAsync uses: once the
		// tool actually ran, a nonzero exit here is Broadcom's own auth/authorization
		// rejection of the Activation Code (bad/expired/revoked/insufficient portal
		// role), not a runner problem -- issue #687 AC: "auth rejection ... [is]
		// visible and fail closed," classified distinctly from a runner-side failure.
		return CatalogPullResult.AuthFailed(Truncate(stdout.Length > 0 ? stdout : stderr));
	}

	private static string PrepareIdentityHome(ManagedToolOptions options)
	{
		string identityHome = Path.Combine(options.ToolStatePath, options.IdentityStatePath);
		Directory.CreateDirectory(identityHome);
		return identityHome;
	}

	private static string ExecutablePath(ManagedToolOptions options) =>
		Path.Combine(options.ToolStatePath, options.ActiveDirectoryName, options.ExecutableRelativePath);

	private static async Task<(bool Succeeded, int ExitCode, string Stdout, string Stderr)> RunAsync(
		string executablePath, string arguments, string identityHome, ManagedToolOptions options, CancellationToken cancellationToken)
	{
		string libraryPath = Path.Combine(options.ToolStatePath, options.ActiveDirectoryName, options.LibraryRelativePath);

		ProcessStartInfo startInfo = new(executablePath, arguments)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		string existingLibraryPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty;
		startInfo.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existingLibraryPath)
			? libraryPath
			: libraryPath + Path.PathSeparator + existingLibraryPath;

		startInfo.Environment["HOME"] = identityHome;
		startInfo.Environment["XDG_DATA_HOME"] = Path.Combine(identityHome, ".local", "share");

		using CancellationTokenSource timeoutSource = new(options.CatalogPullTimeout);
		using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

		Process process;
		try
		{
			process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null.");
		}
		catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
		{
			return (false, -1, string.Empty, exception.Message);
		}

		using (process)
		{
			process.StandardInput.Close();

			try
			{
				await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				TryKill(process);
				bool timedOut = timeoutSource.IsCancellationRequested;
				return (false, -1, string.Empty,
					timedOut ? $"did not complete within {options.CatalogPullTimeout}" : "cancelled");
			}

			string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
			string stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
			return (true, process.ExitCode, stdout, stderr);
		}
	}

	private static void TryKill(Process process)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch (InvalidOperationException)
		{
			// Already exited between the check and the kill -- not a failure.
		}
	}

	private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "...";
}
