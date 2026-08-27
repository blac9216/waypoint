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
/// What makes <see cref="ReadinessReport.Ready"/> false (issue #440 AC4, narrowed by
/// issue #905):
/// <list type="bullet">
/// <item><description>every configured PowerShell module preload path
/// (<see cref="PowerShellOptions.ModulePreloadPaths"/>) -- the compliance shim modules
/// this image's Dockerfile provisions (module.common/module.transport.*, the shared
/// WaypointLogging adapter (issue #579), and the Waypoint{Discovery,Scan,CredentialTest}
/// shims) -- exists on disk;</description></item>
/// <item><description>the scan artifact store (<see cref="ScanOptions.ArtifactStorePath"/>)
/// is mounted and writable -- HDF/CKL artifacts and STIG Manager upload staging need
/// read-write access (ADR-0014 §7).</description></item>
/// </list>
///
/// Reported but deliberately NOT hard-failing (issue #905, mirroring the download-runner's
/// "tool absent is reported, not fatal" precedent -- see
/// <c>Waypoint.DownloadRunner.ReadinessSnapshot</c>'s <c>ToolPresent</c> doc comment):
/// <list type="bullet">
/// <item><description>the deprecated compliance-content fallback roots
/// (<see cref="ScanOptions.ProfilePath"/>, <see cref="ScanOptions.NsxProfilePath"/>,
/// <see cref="ScanOptions.SrgProfilePath"/>) -- issue #639 already retired these as the
/// primary profile source in favor of the compliance-content volume resolved per-run via
/// <c>scope.profile_id</c>; the mount stays only as <c>ScanJobHandler</c>'s transitional
/// fallback for a payload with no <c>profile_key</c>, and production ships it as an empty
/// named volume with nothing to populate it (only the dev override's <c>dev-bootstrap</c>
/// creates the three subdirectories). Hard-failing container health on an empty,
/// deprecated, read-only fallback mount blocked every by-the-book production bring-up
/// (issue #905) for a capability gap that only ever affects the one job whose payload
/// omits <c>profile_key</c>.</description></item>
/// <item><description>a master key is loadable
/// (<see cref="IMasterKeyProvider"/>) -- discover/credential-test/scan all decrypt a
/// service or run-scoped personal credential before doing anything else, so a runner
/// that cannot reach its master key cannot do useful compliance work, but production
/// deliberately ships without one until an operator supplies it (<c>deploy/README.md</c>
/// "Production only: secrets master key", <c>deploy/compose.yaml</c>'s master-key mount
/// comment on this service): "a runner that cannot reach this key still starts and
/// reports healthy, but every job it claims fails until the key is mounted." Folding
/// key absence into <see cref="ReadinessReport.Ready"/> contradicted that documented,
/// supported state and disagreed with the download-runner, which never gates health on
/// key presence either.</description></item>
/// </list>
///
/// Every one of the above still appears in <see cref="ReadinessReport.Problems"/> --
/// this check never hides a gap, it only stops the *deprecated-fallback* and
/// *operator-not-configured-yet* gaps from failing the container healthcheck. An
/// operator (or <c>GET /api/v1/system</c>) can still see exactly what is missing; only
/// the two genuinely load-bearing dependencies (module shims, artifact store) fail
/// closed on the health surface.
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
		// Hard failures: a genuinely load-bearing dependency is missing, and this
		// runner cannot do useful work at all without it. These fail Ready.
		List<string> problems = [];

		// Degraded: reported for observability (still appended to the combined
		// Problems list below) but never fails Ready -- issue #905. See this type's
		// doc comment for why the deprecated profile-fallback mounts and master-key
		// absence belong here rather than above.
		List<string> degraded = [];

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
		CheckReadableDirectory(scans.ProfilePath, "Scans:ProfilePath", degraded);
		CheckReadableDirectory(scans.NsxProfilePath, "Scans:NsxProfilePath", degraded);
		CheckReadableDirectory(scans.SrgProfilePath, "Scans:SrgProfilePath", degraded);
		CheckWritableDirectory(scans.ArtifactStorePath, "Scans:ArtifactStorePath", problems);

		try
		{
			_masterKeyProvider.GetKey();
		}
		catch (Exception exception)
		{
			degraded.Add($"Master key is unavailable: {exception.Message}");
		}

		List<string> allProblems = [.. problems, .. degraded];
		return new ReadinessReport(problems.Count == 0, allProblems);
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

/// <summary>
/// Point-in-time readiness verdict. <see cref="Problems"/> lists every gap this check
/// found, including ones that do NOT affect <see cref="Ready"/> -- see
/// <see cref="ComplianceReadinessCheck"/>'s doc comment for exactly which entries are
/// hard failures (fail <see cref="Ready"/>) versus reported-but-degraded (issue #905).
/// </summary>
public sealed record ReadinessReport(bool Ready, IReadOnlyList<string> Problems);
