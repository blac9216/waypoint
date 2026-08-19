# compliance-runner: imported project-owned PowerShell

These three files are imported unmodified (except for the added Apache-2.0 header and
one doc-comment sanitization, both noted below and in `NOTICE`) from the sibling
[`vmware-stig-docker`](https://github.com/blac9216/vmware-stig-docker) repository,
per [ADR-0013](../../../docs/adr/0013-control-plane-and-runners.md) and
[ADR-0015](../../../docs/adr/0015-source-build-and-operator-export.md).

Only the files the M2 discover/credential-test/scan call graph needs are imported --
this is not a full port of the sibling repo. The call graph was inventoried from the
Waypoint-owned shim modules that dot-source these files
(`backend/Waypoint.Infrastructure/PowerShell/Modules/Waypoint{Discovery,Scan,
CredentialTest}/*.psm1`) and the compose env vars that point at them
(`deploy/docker-compose.yml`, `WAYPOINT_VMWARE_STIG_DOCKER_*`, issue #395/#427):

| File | Functions the shims dot-source | Used by |
| ---- | ------------------------------- | ------- |
| `module.transport.vmware.ps1` | `Connect-StigVIServer`, `Disconnect-VIServer` | `WaypointDiscovery.Invoke-WaypointDiscovery`, `WaypointCredentialTest.Invoke-WaypointVCenterCredentialTest` |
| `module.transport.nsxapi.ps1` | `Get-NsxSessionToken` | `WaypointScan.Invoke-WaypointNsxScan`, `WaypointCredentialTest.Invoke-WaypointNsxCredentialTest` |
| `module.common.ps1` | `Invoke-ExternalCommand`, `New-InspecSecretConfigFile`, `Test-TargetReachable` | `WaypointScan.Invoke-WaypointScan` / `-WaypointAttest` / `-WaypointConvert` / `-WaypointSrgScan`, `WaypointCredentialTest.Invoke-WaypointSshCredentialTest` |

Not imported because the current runner call graph does not require them: `module.remediation.ps1`,
`module.orchestrator.ps1`, `module.parallelism.ps1`, `module.catalog.ps1`,
`module.config.ps1`, `module.logging.ps1`, `module.menu.ps1`, `module.reporting.ps1`,
`module.watcher.ps1`, `module.sshaccess*.ps1`, `module.transport.ssh.ps1`,
`module.transport.vcfapi.ps1`, `module.benchmarks.ps1`, `module.attestation.ps1`,
`shell_init.ps1`, `stig-runner.ps1`. The Waypoint shims provide their own generic
`Get-LogSplat`/`Write-Log` stand-ins rather than importing `module.logging.ps1`'s
parallel-engine logging stack (see the shim modules' own doc comments) -- that
pattern is preserved here; no sibling-repository logging module is imported.

## Provenance and sanitization

Both `vmware-stig-docker` and this repository share the same copyright holder
(Justin Black); the sibling repo carries no `LICENSE` file, so these files are
self-authored code being relicensed under Apache-2.0 at import time, not
third-party code accepted under an existing license grant. See the root `NOTICE`
file for the formal entry.

One doc comment in `module.common.ps1` (`Get-TargetShortName`) originally illustrated
FQDN/IP-collision behavior with a real lab hostname and private IP addresses; those
were replaced with fictional `example.internal` / RFC 5737 (`192.0.2.0/24`) values
per this repo's mandatory sanitization policy (`CLAUDE.md`). No other change was made
to file content beyond the added license header.

## Runtime wiring

The compliance-runner image copies these project-owned scripts into `/powershell`.
`deploy/docker-compose.yml` points the `WAYPOINT_VMWARE_STIG_DOCKER_*_PATH`
variables at those image paths, replacing the former host bind mount completed by
#440/#442.
