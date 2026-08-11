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

using Microsoft.Extensions.Options;
using Waypoint.Core.PowerShell;
using Waypoint.Core.Scans;
using Waypoint.Core.Secrets;

namespace Waypoint.ComplianceRunner.Readiness;

/// <summary>
/// ADR-0013 §Consequences / ADR-0014 §5: "Runner readiness must include registered
/// capabilities and allocated-resource discovery; it must fail closed when required
/// dependencies or mounts are unavailable." This is the compliance-runner's fail-closed
/// check -- it never throws, it reports.
///
/// What "ready" means for this domain (issue #440 AC4):
/// <list type="bullet">
/// <item><description>every configured PowerShell module preload path
/// (<see cref="PowerShellOptions.ModulePreloadPaths"/>) -- the compliance shim modules
/// this image's Dockerfile provisions (module.common/module.transport.*, and the
/// Waypoint{Discovery,Scan,CredentialTest} shims) -- exists on disk;</description></item>
/// <item><description>the compliance-content roots
/// (<see cref="ScanOptions.ProfilePath"/>, <see cref="ScanOptions.NsxProfilePath"/>,
/// <see cref="ScanOptions.SrgProfilePath"/>) are mounted read-only content the scan
/// handler can read;</description></item>
/// <item><description>the scan artifact store (<see cref="ScanOptions.ArtifactStorePath"/>)
/// is mounted and writable -- HDF/CKL artifacts and STIG Manager upload staging need
/// read-write access (ADR-0014 §7);</description></item>
/// <item><description>a master key is loadable
/// (<see cref="IMasterKeyProvider"/>) -- discover/credential-test/scan all decrypt a
/// service or run-scoped personal credential before doing anything else, so a runner
/// that cannot reach its master key cannot do useful compliance work even though the
/// process itself is alive.</description></item>
/// </list>
///
/// Deliberately excluded from this check: reaching into the InSpec/cinc-auditor or SAF
/// CLI binaries the Dockerfile installs, or actually opening a runspace. Those are
/// exercised by the handlers themselves on first real use; duplicating an exec-and-parse
/// probe here would only add another thing that can drift from the Dockerfile without
/// making the "fails closed" guarantee any stronger for what this issue asks for
/// (missing *mounts/config*, not missing *binaries*).
/// </summary>
public sealed class ComplianceReadinessCheck
{
	private readonly IOptions<PowerShellOptions> _powerShellOptions;
	private readonly IOptions<ScanOptions> _scanOptions;
	private readonly IMasterKeyProvider _masterKeyProvider;

	public ComplianceReadinessCheck(
		IOptions<PowerShellOptions> powerShellOptions,
		IOptions<ScanOptions> scanOptions,
		IMasterKeyProvider masterKeyProvider)
	{
		ArgumentNullException.ThrowIfNull(powerShellOptions);
		ArgumentNullException.ThrowIfNull(scanOptions);
		ArgumentNullException.ThrowIfNull(masterKeyProvider);

		_powerShellOptions = powerShellOptions;
		_scanOptions = scanOptions;
		_masterKeyProvider = masterKeyProvider;
	}

	/// <summary>Runs every check and returns a complete report -- never throws.</summary>
	public ReadinessReport Evaluate()
	{
		List<string> problems = [];

		PowerShellOptions powerShell = _powerShellOptions.Value;
		if (powerShell.ModulePreloadPaths.Count == 0)
		{
			problems.Add("No PowerShell module preload paths are configured (PowerShell:ModulePreloadPaths).");
		}

		foreach (string modulePath in powerShell.ModulePreloadPaths)
		{
			if (!Directory.Exists(modulePath) && !File.Exists(modulePath))
			{
				problems.Add($"PowerShell module preload path is missing: '{modulePath}'.");
			}
		}

		ScanOptions scans = _scanOptions.Value;
		CheckReadableDirectory(scans.ProfilePath, "Scans:ProfilePath", problems);
		CheckReadableDirectory(scans.NsxProfilePath, "Scans:NsxProfilePath", problems);
		CheckReadableDirectory(scans.SrgProfilePath, "Scans:SrgProfilePath", problems);
		CheckWritableDirectory(scans.ArtifactStorePath, "Scans:ArtifactStorePath", problems);

		try
		{
			_masterKeyProvider.GetKey();
		}
		catch (Exception exception)
		{
			problems.Add($"Master key is unavailable: {exception.Message}");
		}

		return new ReadinessReport(problems.Count == 0, problems);
	}

	private static void CheckReadableDirectory(string path, string settingName, List<string> problems)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			problems.Add($"{settingName} is not configured.");
			return;
		}

		if (!Directory.Exists(path))
		{
			problems.Add($"{settingName} ('{path}') does not exist or is not mounted.");
		}
	}

	private static void CheckWritableDirectory(string path, string settingName, List<string> problems)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			problems.Add($"{settingName} is not configured.");
			return;
		}

		if (!Directory.Exists(path))
		{
			problems.Add($"{settingName} ('{path}') does not exist or is not mounted.");
			return;
		}

		// A directory that exists but is not writable fails a real artifact write only
		// at scan-completion time, far from startup. Probe explicitly with a throwaway
		// file so a read-only-mounted volume is caught here instead.
		string probePath = Path.Combine(path, $".waypoint-writable-probe-{Guid.NewGuid():N}");
		try
		{
			File.WriteAllBytes(probePath, [0]);
			File.Delete(probePath);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			problems.Add($"{settingName} ('{path}') is not writable: {exception.Message}");
		}
	}
}

/// <summary>Point-in-time readiness verdict: either healthy, or every reason it is not.</summary>
public sealed record ReadinessReport(bool Ready, IReadOnlyList<string> Problems);
