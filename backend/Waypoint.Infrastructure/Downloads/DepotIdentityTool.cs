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

/// <inheritdoc cref="IDepotIdentityTool"/>
/// <remarks>
/// Mirrors <c>ManagedToolDistributionInstaller.SmokeTestAsync</c>'s bounded
/// noninteractive process pattern: stdin closed immediately, a linked
/// timeout/cancellation token, and a hard kill on timeout. The Broadcom-documented
/// command is <c>vcf-download-tool configuration get --software-depot-id</c> (issue
/// #691's cited guidance); validation reuses the sibling
/// <c>--depot-download-activation-code-file</c> convention against the tool's own
/// <c>configuration get --software-depot-id</c> call once the code file is staged --
/// the tool itself refuses a mismatched/invalid code, so a nonzero exit or its stderr
/// classifying as an auth failure is the validation signal.
/// </remarks>
public sealed class DepotIdentityTool : IDepotIdentityTool
{
	private const string SoftwareDepotIdArgument = "configuration get --software-depot-id";
	private readonly IOptions<ManagedToolOptions> _options;
	private readonly IManagedToolPresenceChecker _presenceChecker;

	public DepotIdentityTool(IOptions<ManagedToolOptions> options, IManagedToolPresenceChecker presenceChecker)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(presenceChecker);
		_options = options;
		_presenceChecker = presenceChecker;
	}

	public async Task<DepotIdentityResult> GetDepotIdAsync(CancellationToken cancellationToken)
	{
		if (!_presenceChecker.IsPresent())
		{
			return DepotIdentityResult.Failed(
				$"vcf-download-tool is not installed (expected at '{_presenceChecker.DescribeExpectedLocation()}'). Install the managed tool before generating a Software Depot ID.");
		}

		ManagedToolOptions options = _options.Value;
		string identityHome = PrepareIdentityHome(options);

		(bool succeeded, int exitCode, string stdout, string stderr) = await RunAsync(
			ExecutablePath(options), SoftwareDepotIdArgument, identityHome, options, cancellationToken).ConfigureAwait(false);

		if (!succeeded)
		{
			return DepotIdentityResult.Failed($"Depot ID query could not be started or timed out: {stderr}");
		}

		if (exitCode != 0)
		{
			return DepotIdentityResult.Failed($"Depot ID query exited with code {exitCode}: {Truncate(stderr)}");
		}

		string depotId = stdout.Trim();
		return string.IsNullOrEmpty(depotId)
			? DepotIdentityResult.Failed("Depot ID query succeeded but produced no output.")
			: DepotIdentityResult.Ok(depotId);
	}

	public async Task<DepotValidationResult> ValidateActivationCodeAsync(string activationCodePath, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(activationCodePath);

		if (!_presenceChecker.IsPresent())
		{
			return DepotValidationResult.Failed(
				$"vcf-download-tool is not installed (expected at '{_presenceChecker.DescribeExpectedLocation()}').");
		}

		ManagedToolOptions options = _options.Value;
		string identityHome = PrepareIdentityHome(options);
		string argument = $"configuration get --software-depot-id --depot-download-activation-code-file \"{activationCodePath}\"";

		(bool succeeded, int exitCode, string stdout, string stderr) = await RunAsync(
			ExecutablePath(options), argument, identityHome, options, cancellationToken).ConfigureAwait(false);

		if (!succeeded)
		{
			// The invocation itself never completed (missing binary, timeout) -- never
			// classified as a code rejection.
			return DepotValidationResult.Failed($"Activation Code validation could not be completed: {stderr}");
		}

		if (exitCode == 0)
		{
			return DepotValidationResult.Ok();
		}

		// A nonzero exit with the tool actually running IS the auth-failure signal
		// (bad/expired/revoked code, or a portal-role problem) -- issue #691 AC: "Missing
		// Broadcom portal roles are surfaced as external enrollment guidance, not
		// retried as a runner failure," so this is classified as an auth failure, not an
		// ordinary job failure that a retry policy might act on.
		return DepotValidationResult.AuthFailed(Truncate(stdout.Length > 0 ? stdout : stderr));
	}

	/// <summary>
	/// Creates (if absent) and returns the isolated, persistent identity directory the
	/// tool's <c>HOME</c>/<c>XDG_DATA_HOME</c> is pointed at -- same-volume as the
	/// managed tool itself (<see cref="ManagedToolOptions.ToolStatePath"/>), so the
	/// Depot ID the tool derives/persists there is stable across container rebuilds
	/// without ever being a container-global root home.
	/// </summary>
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

		// Isolated persistent app-state home (issue #691 AC), never the process's own
		// container-global HOME -- matches the sibling reference's
		// ~/.local/share/vmware/vdt/machine_id convention, but rooted at the managed
		// volume instead of /root.
		startInfo.Environment["HOME"] = identityHome;
		startInfo.Environment["XDG_DATA_HOME"] = Path.Combine(identityHome, ".local", "share");

		using CancellationTokenSource timeoutSource = new(options.EnrollmentCommandTimeout);
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
					timedOut ? $"did not complete within {options.EnrollmentCommandTimeout}" : "cancelled");
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
