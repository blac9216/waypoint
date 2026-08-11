# Copyright 2026 Justin Black
#
# Licensed under the Apache License, Version 2.0 (the "License").
# You may not use this file except in compliance with the License.
# You may obtain a copy of the License at
#
#     http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.
#
# Imported from the sibling vmware-stig-docker repository (issue #438, ADR-0013,
# ADR-0015): project-owned PowerShell authored by the same copyright holder as this
# repository, relicensed under Apache-2.0 at import time and copied unmodified except
# for this header and the sanitization noted in NOTICE. See NOTICE for provenance.

<#
.MODULE
	module.common.ps1

.SYNOPSIS
	Common utilities for VMware STIG Runner.

.DESCRIPTION
	Provides shared utilities for configuration loading, external command execution,
	environment validation, and directory management across all workflow modules.
	New-ScanDirectoryStructure's report subdirectories are catalog-driven (issue #134),
	so this module also depends on module.catalog.ps1 -- Import-StigCatalog must have run
	before that function is called.

.DEPENDENCIES
	module.parallelism.ps1
	module.catalog.ps1 (Get-CatalogReportGroupMap, used by New-ScanDirectoryStructure)
#>

#region Shared Runtime Config Defaults

# Single source of truth for every runtime-config default literal (issue #216 slice 1 of
# epic #137): Initialize-EngineConfig (module.config.ps1) falls back to these exact
# values whenever engine.json/site.json doesn't supply one, so a default like '/reports'
# is no longer independently hardcoded a second time over there. Resolved from
# engine.json/site.json by Initialize-EngineConfig at runtime; these are the bare
# defaults so utility functions are usable before/without a config load (e.g. in unit
# tests).
$Script:RuntimeConfigDefaults = [PSCustomObject]@{
	ReportsBasePath  = '/reports'
	ProfilesBasePath = '/profiles'
	ConfigBasePath   = '/config'
	DefaultThrottle  = 25
	DefaultTimeout   = 3600
	VSphereVersion   = '8.0'
	WatcherCklPath   = $null
}

# $Script:RuntimeConfig holds all seven runtime-config values in one object (issue #216
# slice 1 of epic #137 introduced it as a dual-write mirror; issue #217 slice 2 migrated
# ReportsBasePath/ProfilesBasePath/ConfigBasePath/WatcherCklPath to be sole-sourced here;
# issue #218 slice 3 finished DefaultThrottle/DefaultTimeout/VSphereVersion the same way).
# None of the seven have standalone globals any more -- every read/write site goes
# through this object, which is the sole source of truth. Initialize-EngineConfig
# (module.config.ps1) updates the resolved properties on this object once it runs; the
# WatcherCklPath computation in New-ScanDirectoryStructure below writes it directly.
# Pre-seeded here, identical to the bare defaults above, so it is never $null for a
# consumer that only dot-sources this module without ever calling Initialize-EngineConfig.
$Script:RuntimeConfig = [PSCustomObject]@{
	ReportsBasePath  = $Script:RuntimeConfigDefaults.ReportsBasePath
	ProfilesBasePath = $Script:RuntimeConfigDefaults.ProfilesBasePath
	ConfigBasePath   = $Script:RuntimeConfigDefaults.ConfigBasePath
	DefaultThrottle  = $Script:RuntimeConfigDefaults.DefaultThrottle
	DefaultTimeout   = $Script:RuntimeConfigDefaults.DefaultTimeout
	VSphereVersion   = $Script:RuntimeConfigDefaults.VSphereVersion
	WatcherCklPath   = $Script:RuntimeConfigDefaults.WatcherCklPath
}

#endregion


#region Scan Skip Tracking

# $Script:ScanSkips holds "target family discovered-but-skipped this run" records
# (issue #246): a generic, process-wide list any discovery/transport code can append to
# via Add-ScanSkip without a direct dependency on the orchestrator or reporting modules.
# Invoke-StigScan (module.orchestrator.ps1) calls Clear-ScanSkips once at the start of
# every run, so skips recorded by a previous scan never leak into the next run's
# [Summary] block, AllTargetsSummary.xml, or exit code -- relevant in-process across
# repeated Invoke-StigScan calls (interactive/menu mode) or repeated Pester runs.
#
# Add-ScanSkip is called from Get-StigTargets' VCSA-no-credential branch
# (module.transport.vmware.ps1, issue #246/#264): when a mixed '-Scan all' request has
# no VCSA credential resolved, that branch logs a Warning and records the vcsa family
# here instead of silently letting the run read as a full success once vCenter/ESXi/VM
# discovery completes. Whatever this list holds at that point feeds Invoke-StigScan's
# final [Summary] line, AllTargetsSummary.xml, and the process exit code (issue #243's
# contract, via Get-ScanExitCode).
$Script:ScanSkips = [System.Collections.Generic.List[PSObject]]::new()

<#
.SYNOPSIS
	Record that a target family was discovered-but-skipped this run.

.DESCRIPTION
	Appends a skip record (family name + reason) to the process-wide $Script:ScanSkips
	list (issue #246). Discovery/transport code can call this instead of only logging a
	mid-run Warning, so Invoke-StigScan can surface the skip in the final [Summary]
	block, AllTargetsSummary.xml, and the process exit code (issue #243) -- see
	Get-ScanSkips and Clear-ScanSkips.

.PARAMETER Family
	Short name of the skipped target family (e.g. 'vcsa').

.PARAMETER Reason
	Human-readable reason the family was skipped.
#>
function Add-ScanSkip {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[string]$Family,

		[Parameter(Mandatory)]
		[string]$Reason
	)

	$Script:ScanSkips.Add([PSCustomObject]@{
		Family = $Family
		Reason = $Reason
	})
}

<#
.SYNOPSIS
	Return every family skip recorded so far this run.

.DESCRIPTION
	Returns a snapshot array of the $Script:ScanSkips list populated by Add-ScanSkip
	(issue #246). Always returns an array -- empty when nothing was skipped, never
	$null -- so callers can unconditionally measure .Count without a null check.

.OUTPUTS
	[PSObject[]] Skip records with Family/Reason properties.
#>
function Get-ScanSkips {
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Returns the full set of family skips recorded so far this run; plural by design')]
	[CmdletBinding()]
	param()

	return ,@($Script:ScanSkips)
}

<#
.SYNOPSIS
	Reset the skip-tracking list.

.DESCRIPTION
	Clears $Script:ScanSkips back to empty. Invoke-StigScan calls this once at the start
	of every run so skips recorded by a previous scan (interactive/menu mode reuses the
	same process) never leak into the next run's summary or exit code.
#>
function Clear-ScanSkips {
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Resets the full set of family skips recorded so far this run; plural by design, matching Get-ScanSkips')]
	[CmdletBinding()]
	param()

	$Script:ScanSkips = [System.Collections.Generic.List[PSObject]]::new()
}

#endregion


#region Get-PSObjectMember

<#
.SYNOPSIS
	Safe property access on a PSCustomObject (returns the value or $null).

.DESCRIPTION
	Returns the named NoteProperty's value, or $null when the object is null or the
	member is absent, without tripping Set-StrictMode on missing members. Used for
	variable-named keys such as version "8-0" that ConvertFrom-Json turns into
	NoteProperties. Generic helper shared across the catalog, config, transport,
	attestation, benchmark, and remediation modules.

.PARAMETER InputObject
	The object to read from. May be $null.

.PARAMETER Name
	The property name to resolve.

.OUTPUTS
	The property value, or $null when absent.
#>
function Get-PSObjectMember {
	[CmdletBinding()]
	param(
		[Parameter()]
		[AllowNull()]
		[object]$InputObject,

		[Parameter(Mandatory)]
		[string]$Name
	)

	if ($null -eq $InputObject) { return $null }
	$Prop = $InputObject.PSObject.Properties[$Name]
	if ($null -eq $Prop) { return $null }
	return $Prop.Value
}

#endregion


#region Resolve-HostIpViaDns

<#
.SYNOPSIS
	Resolve a hostname to its first IPv4 address, or '' on failure.

.DESCRIPTION
	Wraps the repeated `[System.Net.Dns]::GetHostAddresses(...)` IPv4 lookup used as a
	fallback when guest tools / the API don't provide an IP. Never throws; logs a Debug
	line and returns '' when resolution fails.

.PARAMETER HostName
	The hostname or FQDN to resolve.

.PARAMETER Source
	Component identifier for the Debug log on failure.

.OUTPUTS
	[string] the first IPv4 address, or '' when none resolves.
#>
function Resolve-HostIpViaDns {
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Dns is an acronym, not a plural noun')]
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[string]$HostName,

		[Parameter()]
		[string]$Source = 'DNS'
	)

	$WriteLogParams = Get-LogSplat $Source
	try {
		$DnsResult = [System.Net.Dns]::GetHostAddresses($HostName) | Where-Object AddressFamily -eq 'InterNetwork' | Select-Object -First 1
		if ($null -ne $DnsResult) { return $DnsResult.IPAddressToString }
	} catch {
		Write-Log "DNS resolution failed for ${HostName}: $($_.Exception.Message)" -Severity 'Debug' @WriteLogParams
	}
	return ''
}

#endregion


#region Test-TargetReachable

<#
.SYNOPSIS
	Cheap TCP-connect reachability probe, bounded by a short timeout distinct from the
	scan timeout.

.DESCRIPTION
	Opens a TCP connection to TargetHost:Port -- a connectivity check only (the TCP
	handshake), no auth and no protocol traffic -- so an ssh transport target build
	(SRG product, VCSA component) can tell an unreachable/hung host apart from a
	reachable one in a few seconds instead of enqueuing it for the full InSpec run
	(issue #261: unreachable ssh targets previously ran unchecked and burned the full
	$Script:RuntimeConfig.DefaultTimeout, 3600s by default, before failing with no
	useful result).
	Runs during single-threaded target discovery, before targets are enqueued for the
	parallel worker engine -- never inside a worker runspace -- so it does not need to
	be added to Get-ExportedFunctions (module.scan.ps1).
	Uses System.Net.Sockets.TcpClient.ConnectAsync bounded by Task.Wait(TimeoutSeconds)
	rather than relying on the OS/library's own connect timeout, which can run far
	longer than TimeoutSeconds against a host that is not responding.

.PARAMETER TargetHost
	Hostname or IP address to probe.

.PARAMETER Port
	TCP port to connect to.

.PARAMETER TimeoutSeconds
	Maximum time to wait for the TCP handshake to complete before giving up and
	returning $false. Deliberately short and independent of the scan timeout; defaults
	to 5.

.PARAMETER Source
	Component identifier for the Debug log on failure.

.OUTPUTS
	[bool] $true when a TCP connection was established within the timeout; $false on
	timeout, refusal, DNS failure, or any other connect error.
#>
function Test-TargetReachable {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[string]$TargetHost,

		[Parameter(Mandatory)]
		[int]$Port,

		[Parameter()]
		[int]$TimeoutSeconds = 5,

		[Parameter()]
		[string]$Source = 'Precheck'
	)

	if ([string]::IsNullOrWhiteSpace($TargetHost)) { return $false }

	$WriteLogParams = Get-LogSplat $Source
	$TcpClient = [System.Net.Sockets.TcpClient]::new()
	try {
		$ConnectTask = $TcpClient.ConnectAsync($TargetHost, $Port)
		if (-not $ConnectTask.Wait([TimeSpan]::FromSeconds($TimeoutSeconds))) {
			Write-Log "Reachability probe timed out for ${TargetHost}:${Port} after ${TimeoutSeconds}s" -Severity 'Debug' @WriteLogParams
			return $false
		}
		return $TcpClient.Connected
	} catch {
		Write-Log "Reachability probe failed for ${TargetHost}:${Port}: $($_.Exception.Message)" -Severity 'Debug' @WriteLogParams
		return $false
	} finally {
		$TcpClient.Dispose()
	}
}

#endregion


#region New-AttestStep

<#
.SYNOPSIS
	Compute the attest -> convert wiring for a CKL-producing scan target.

.DESCRIPTION
	Encapsulates the "attest the HDF first, then convert the attested report" indirection
	(issue #97) that every CKL-producing transport repeats. When an attestation template is
	set, SAF attests $ReportFile into $AttestReportFile and the CKL conversion must read the
	attested report; when not, conversion reads the raw HDF and no attest step runs.

.PARAMETER ReportFile
	The InSpec HDF report path.

.PARAMETER AttestTemplate
	The resolved attestation template path, or null/empty for no attestation.

.PARAMETER AttestReportFile
	The path the attested HDF should be written to (used only when a template is set).

.OUTPUTS
	[PSCustomObject] @{ AttestRequired; ConvertInputFile; AttestReportFile; SafAttestArgs }.
#>
function New-AttestStep {
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Pure function: builds an attest/convert descriptor, no system state change')]
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[string]$ReportFile,

		[Parameter()]
		[AllowNull()]
		[AllowEmptyString()]
		[string]$AttestTemplate,

		[Parameter()]
		[string]$AttestReportFile = ''
	)

	if ([string]::IsNullOrWhiteSpace($AttestTemplate)) {
		return [PSCustomObject]@{
			AttestRequired   = $false
			ConvertInputFile = $ReportFile
			AttestReportFile = ''
			SafAttestArgs    = ''
		}
	}

	return [PSCustomObject]@{
		AttestRequired   = $true
		ConvertInputFile = $AttestReportFile
		AttestReportFile = $AttestReportFile
		SafAttestArgs    = "attest apply -i `"$ReportFile`" `"$AttestTemplate`" -o `"$AttestReportFile`""
	}
}

#endregion


#region New-CklConvertArgs

<#
.SYNOPSIS
	Build the `saf convert hdf2ckl` argument string with optional host metadata.

.DESCRIPTION
	Replaces the per-transport copies of the hdf2ckl arg-builder. Appends each of
	--hostname/--fqdn/--ip/--mac only when non-empty (NSX, for example, passes -Mac '').

.PARAMETER ConvertInputFile
	The HDF to convert (the attested report when attesting, else the raw HDF).

.PARAMETER CklStagingPath
	The CKL output path (staging dir; corrected then published).

.PARAMETER Hostname
	Optional hostname stamped into the CKL.

.PARAMETER Fqdn
	Optional FQDN stamped into the CKL.

.PARAMETER Ip
	Optional IP stamped into the CKL.

.PARAMETER Mac
	Optional MAC stamped into the CKL.

.OUTPUTS
	[string] the `convert hdf2ckl ...` argument string.
#>
function New-CklConvertArgs {
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Pure function: builds an argument string, no system state change')]
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Args = the argument string it returns')]
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[string]$ConvertInputFile,

		[Parameter(Mandatory)]
		[string]$CklStagingPath,

		[Parameter()]
		[string]$Hostname = '',

		[Parameter()]
		[string]$Fqdn = '',

		[Parameter()]
		[string]$Ip = '',

		[Parameter()]
		[string]$Mac = ''
	)

	$CklArgs = "convert hdf2ckl -i `"$ConvertInputFile`" -o `"$CklStagingPath`""
	if ($Hostname) { $CklArgs += " --hostname `"$Hostname`"" }
	if ($Fqdn) { $CklArgs += " --fqdn `"$Fqdn`"" }
	if ($Ip) { $CklArgs += " --ip `"$Ip`"" }
	if ($Mac) { $CklArgs += " --mac `"$Mac`"" }
	return $CklArgs
}

#endregion

#region New-StigTarget

<#
.SYNOPSIS
	Build a scan-target object with the worker's Config state machine defaulted.

.DESCRIPTION
	The single definition of the scan-target contract the parallel worker consumes.
	Replaces the seven hand-built `[PSCustomObject]@{ TargetType; VCenterSession;
	Config; TargetInfo; Paths; Commands }` literals in the transport builders (the
	four vSphere builders + NSX + SSH) so the object shape has one source of truth.

	The Config state machine (InspecSuccess/AttestSuccess/ConvertSuccess/CklCorrected/
	Success + error fields) is always initialized to its starting state; only Kind and
	AttestRequired vary at build time. Genuine per-transport variance is opt-in:
	  - VCenterSession  -- the vmware transport's reused PowerCLI session (else $null).
	  - Credential      -- carried on the target for the worker. SSH sets it at build
	                       time; vmware attaches it after discovery; NSX needs none.
	                       The property is added only when -Credential is supplied so
	                       the object matches today's shape exactly.
	  - Kind 'srg'      -- adds the SSH-only Config fields (Sudo, SudoRequiresPassword,
	                       SummaryGenerated) and Paths.SummaryFile.

.PARAMETER TargetType
	The worker's dispatch/partition key (e.g. 'vcenter', 'esxi', 'vm',
	'vcsa-component-<name>', 'nsx-component-<name>', 'srg-<product>').

.PARAMETER Kind
	Post-processing kind: 'stig' (CKL + STIG Manager) or 'srg' (HDF + summary only).

.PARAMETER AttestRequired
	Whether the worker attests the HDF before convert/summary.

.PARAMETER Name
	Host display name (TargetInfo.Name).

.PARAMETER Fqdn
	Host FQDN stamped into the CKL (TargetInfo.FQDN). Optional; default ''.

.PARAMETER Ip
	Host IP address stamped into the CKL (TargetInfo.IP). Optional; default ''.

.PARAMETER Mac
	Host MAC address stamped into the CKL (TargetInfo.Mac). Optional; default ''.

.PARAMETER ReportFile
	The InSpec JSON (HDF) output path.

.PARAMETER AttestReportFile
	The attested-HDF output path (when attesting). Optional; default ''.

.PARAMETER CklPath
	The watched CKL output path. Empty for SRG (HDF-only).

.PARAMETER CklStagingPath
	The CKL's non-watched staging path. Empty for SRG (HDF-only).

.PARAMETER AttestTemplate
	The attestation template path/object, or $null.

.PARAMETER InspecProfile
	The InSpec profile directory.

.PARAMETER InspecArgs
	The pre-built InSpec command argument string the worker runs.

.PARAMETER SafAttestArgs
	The pre-built `saf attest` command argument string the worker runs. Optional; default ''.

.PARAMETER SafConvertArgs
	The pre-built `saf convert` command argument string the worker runs. Optional; default ''.

.PARAMETER VCenterSession
	The vmware transport's PowerCLI session, or $null (NSX/SSH).

.PARAMETER Credential
	PSCredential carried on the target. Added only when supplied.

.PARAMETER SummaryFile
	SRG-only: the `saf view summary` artifact path (Paths.SummaryFile).

.PARAMETER Sudo
	SRG-only: the worker's sudo state machine (Config.Sudo).

.PARAMETER SudoRequiresPassword
	SRG-only: the worker's sudo state machine (Config.SudoRequiresPassword).

.OUTPUTS
	[PSCustomObject] the scan target the parallel worker consumes.
#>
function New-StigTarget {
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Pure factory: builds an in-memory object, no system state change')]
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[string]$TargetType,

		[Parameter(Mandatory)]
		[ValidateSet('stig', 'srg')]
		[string]$Kind,

		[Parameter(Mandatory)]
		[bool]$AttestRequired,

		[Parameter(Mandatory)]
		[string]$Name,

		[Parameter()]
		[string]$Fqdn = '',

		[Parameter()]
		[string]$Ip = '',

		[Parameter()]
		[string]$Mac = '',

		[Parameter(Mandatory)]
		[string]$ReportFile,

		[Parameter()]
		[string]$AttestReportFile = '',

		[Parameter()]
		[string]$CklPath = '',

		[Parameter()]
		[string]$CklStagingPath = '',

		[Parameter()]
		[AllowNull()]
		[object]$AttestTemplate = $null,

		[Parameter(Mandatory)]
		[string]$InspecProfile,

		[Parameter(Mandatory)]
		[string]$InspecArgs,

		[Parameter()]
		[string]$SafAttestArgs = '',

		[Parameter()]
		[string]$SafConvertArgs = '',

		[Parameter()]
		[AllowNull()]
		[object]$VCenterSession = $null,

		[Parameter()]
		[pscredential]$Credential,

		[Parameter()]
		[string]$SummaryFile = '',

		[Parameter()]
		[bool]$Sudo,

		[Parameter()]
		[bool]$SudoRequiresPassword
	)

	# The worker's Config state machine, always initialized to its starting state.
	$Config = [ordered]@{
		Kind           = $Kind
		AttestRequired = $AttestRequired
		InspecSuccess  = $false
		AttestSuccess  = $false
		ConvertSuccess = $false
		CklCorrected   = $false
		Success        = $false
		ErrorMessage   = ''
		ErrorDetails   = ''
	}
	$Paths = [ordered]@{
		ReportFile       = $ReportFile
		AttestReportFile = $AttestReportFile
		CklPath          = $CklPath
		CklStagingPath   = $CklStagingPath
		AttestTemplate   = $AttestTemplate
		InspecProfile    = $InspecProfile
	}

	# SRG (ssh transport) extras: the sudo state machine + HDF-summary generation.
	if ($Kind -eq 'srg') {
		$Config.Sudo = $Sudo
		$Config.SudoRequiresPassword = $SudoRequiresPassword
		$Config.SummaryGenerated = $false
		$Paths.SummaryFile = $SummaryFile
	}

	$Target = [PSCustomObject]@{
		TargetType     = $TargetType
		VCenterSession = $VCenterSession
		Config         = [PSCustomObject]$Config
		TargetInfo     = [PSCustomObject]@{ Name = $Name; FQDN = $Fqdn; IP = $Ip; Mac = $Mac }
		Paths          = [PSCustomObject]$Paths
		Commands       = [PSCustomObject]@{
			InspecArgs     = $InspecArgs
			SafAttestArgs  = $SafAttestArgs
			SafConvertArgs = $SafConvertArgs
		}
	}

	# Credential travels ON THE TARGET (a runspace-injected global PSCredential does not
	# reliably reach the worker). SSH supplies it here; vmware attaches it after
	# discovery; NSX needs none. Only add the property when supplied, matching the
	# pre-factory object shape.
	if ($PSBoundParameters.ContainsKey('Credential')) {
		$Target | Add-Member -NotePropertyName 'Credential' -NotePropertyValue $Credential -Force
	}

	return $Target
}

#endregion


#region Get-RedactedCommandString

<#
.SYNOPSIS
	Mask secret-shaped values (CLI-flag, JSON, or env-assignment forms) in a string before logging.

.DESCRIPTION
	Replaces the VALUE half of any secret-shaped `name=value`/`name: value` pairing with a
	fixed marker, across three independent forms, applied in sequence. Used to build the
	copy of a command string or captured subprocess output that is safe to write to the
	log/console; the real, unredacted string is still what actually gets executed/captured
	-- this only touches the logged copy.

	1. CLI option-flag form (issue #142): `--option="value"` / `--option='value'` (quoted,
	   equals-separated, the form every command builder in this repo uses) and
	   `--option value` (space-separated, unquoted). The option name -- --password,
	   --sudo-password, --session-token, --api-token, --client-secret, etc. -- matches
	   case-insensitively whenever it contains "password", "token", or "secret".

	2. JSON key/value form (issue #236): a double-quoted key containing "password",
	   "secret", or "token" (case-insensitive -- covers "password", "sudo_password",
	   "secret", "client_secret", "token", etc., matching the underscored key names
	   New-InspecSecretConfigFile writes) followed by `:` and a double-quoted string value.
	   The replacement re-wraps the marker in double quotes so the result stays valid JSON.

	3. Env/assignment form (issue #236): an ALL-CAPS `NAME=value` pair where NAME (letters,
	   digits, underscores only) contains PASSWORD, SECRET, TOKEN, or SSHPASS -- e.g.
	   `SSHPASS=x`, `WATCHER_CLIENT_SECRET=x`. Deliberately restricted to an all-uppercase
	   name (real env-var convention) so it does NOT fire on a lowercase `name=value` token
	   that merely contains one of those words as a substring, such as an InSpec input
	   `--input password_policy=foo` -- that benign form is preserved verbatim. A leading
	   `--` is also excluded (already handled by form 1, case-insensitively).

	KNOWN LIMITATION: a bare secret value with no recognizable key/flag/assignment prefix
	(e.g. a password echoed on its own line with nothing identifying it as one) cannot be
	distinguished from ordinary text by pattern alone and is NOT redacted by this function.
	Closing that gap would require threading the actual secret value(s) into the logging
	path for a redact-by-known-value pass, which issue #236 explicitly declines to add (see
	that issue's discussion) -- the config-file design (issue #142) deliberately keeps
	secrets out of argv/logging call sites to avoid exactly that plumbing. Callers that log
	captured subprocess output should treat this function's output as best-effort, not a
	guarantee no secret can ever appear in the log.

.PARAMETER CommandString
	The full command/arguments string (or captured subprocess output) to redact.

.OUTPUTS
	[string] The string with secret-shaped values replaced by '***REDACTED***'.
#>
function Get-RedactedCommandString {
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Pure function: transforms a string for logging, no system state change')]
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[AllowEmptyString()]
		[string]$CommandString
	)

	# Form 1 -- CLI option-flag. Option name contains password/token/secret
	# (case-insensitive); value is a quoted string (single or double) or a single
	# non-whitespace token, separated by '=' or whitespace -- covers
	# `--password="x"` / `--sudo-password="x"` (this repo's form) plus the space-separated
	# form InSpec itself also accepts.
	$FlagPattern = '(?i)(--[a-z0-9-]*(?:password|token|secret)[a-z0-9-]*)(=|\s+)("(?:[^"\\]|\\.)*"|''(?:[^''\\]|\\.)*''|\S+)'
	$Redacted = [regex]::Replace($CommandString, $FlagPattern, '$1$2***REDACTED***')

	# Form 2 -- JSON key/value (issue #236: New-InspecSecretConfigFile's --config file
	# shape). Double-quoted key containing password/secret/token (case-insensitive),
	# `:` (with optional surrounding whitespace, matching both compact and
	# ConvertTo-Json -Compress-free output), then a double-quoted string value. The
	# replacement stays double-quoted so the result is still valid JSON.
	$JsonPattern = '(?i)("[a-z0-9_]*(?:password|secret|token)[a-z0-9_]*")(\s*:\s*)("(?:[^"\\]|\\.)*")'
	$Redacted = [regex]::Replace($Redacted, $JsonPattern, '$1$2"***REDACTED***"')

	# Form 3 -- env/assignment (issue #236). NAME is restricted to all-uppercase
	# letters/digits/underscores (the real env-var convention: PASSWORD, SSHPASS,
	# WATCHER_CLIENT_SECRET, ...) so this deliberately does NOT match a lowercase
	# `name=value` token that merely contains "password"/"secret"/"token"/"sshpass" as a
	# substring -- e.g. an InSpec input `password_policy=foo` is left untouched. A NAME
	# immediately preceded by `--` is excluded here since form 1 (case-insensitive)
	# already redacts that shape.
	$EnvPattern = '(?<!-)\b([A-Z0-9_]*(?:PASSWORD|SECRET|TOKEN|SSHPASS)[A-Z0-9_]*)=("(?:[^"\\]|\\.)*"|''(?:[^''\\]|\\.)*''|\S+)'
	$Redacted = [regex]::Replace($Redacted, $EnvPattern, '$1=***REDACTED***')

	return $Redacted
}

#endregion


#region New-InspecSecretConfigFile

<#
.SYNOPSIS
	Write a per-invocation InSpec JSON config file carrying secret CLI options.

.DESCRIPTION
	InSpec's `--config <file>` flag reads a JSON file whose top-level keys (in the
	legacy/unversioned format, i.e. no "version" key) are treated exactly like CLI
	options -- confirmed against the bundled cinc-auditor/inspec 7.1.7 by inspecting
	inspec/config.rb and empirically running `inspec exec --config ... --diagnose`:
	the merged configuration included the file's "password"/"sudo_password" keys and
	the ssh transport genuinely used them to authenticate. Note the key names are
	underscored ("password", "sudo_password"), matching InSpec's internal Thor option
	names -- NOT the hyphenated CLI flag spelling ("sudo-password").

	Moving --password/--sudo-password into this file instead of argv keeps them out of
	the process table (`ps`, `/proc/<pid>/cmdline`) for the lifetime of the scan
	(issue #142). The file is created with owner-only (0600) permissions before the
	secret is written, so it is never briefly world-readable, mirroring the ansible
	vars-file pattern in module.remediation.ps1's Invoke-AnsibleRemediation. The caller
	owns the file's lifetime and must delete it (typically in a `finally` block) once
	the InSpec invocation completes, success or failure.

.PARAMETER Password
	The ssh/target login password (train-ssh `password`).

.PARAMETER SudoPassword
	Optional sudo password (train-ssh `sudo_password`). Omitted from the file when not
	supplied (passwordless sudo, or sudo not in use).

.PARAMETER Directory
	Directory the config file is written into. Defaults to the system temp directory.

.OUTPUTS
	[string] Full path to the written JSON config file.
#>
function New-InspecSecretConfigFile {
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Writes a short-lived, per-invocation secrets file; ShouldProcess/-WhatIf ceremony has no operator-facing meaning for an internal ephemeral artifact (same rationale as the PasswordVarsFile helper in module.remediation.ps1''s Invoke-AnsibleRemediation)')]
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'Must be plaintext: written verbatim into the InSpec --config JSON file (train-ssh has no SecureString-native config path); the caller already extracts it via GetNetworkCredential().Password, matching Invoke-AnsibleRemediation''s -SshPassword')]
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[AllowEmptyString()]
		[string]$Password,

		[Parameter()]
		[AllowNull()]
		[AllowEmptyString()]
		[string]$SudoPassword,

		[Parameter()]
		[string]$Directory = [System.IO.Path]::GetTempPath()
	)

	$ConfigPath = Join-Path $Directory "inspec-secret-$([System.Guid]::NewGuid().ToString('N')).json"

	$ConfigData = [ordered]@{ password = $Password }
	if (-not [string]::IsNullOrEmpty($SudoPassword)) {
		$ConfigData['sudo_password'] = $SudoPassword
	}

	# Create the file with owner-only permissions before writing the secret so the JSON
	# is never briefly world-readable.
	New-Item -ItemType File -Path $ConfigPath -Force -ErrorAction Stop | Out-Null
	if ($IsLinux -or $IsMacOS) {
		& chmod 600 $ConfigPath
	}

	($ConfigData | ConvertTo-Json -Compress) | Set-Content -Path $ConfigPath -Encoding utf8 -NoNewline

	return $ConfigPath
}

#endregion


#region Write-CapturedOutputWindow

<#
.SYNOPSIS
	Log a redacted, head+tail-windowed dump of captured process output at Error severity.

.DESCRIPTION
	Shared by both of Invoke-ExternalCommand's -SurfaceOutputOnFailure paths -- the
	non-zero-exit branch (issue #227/#237) and the timeout-kill branch (issue #260) --
	so the decision of what a failing/killed process's captured stdout/stderr looks
	like in the log has exactly one definition instead of two copies drifting apart.

	Blank lines are dropped from both streams before windowing (AppendLine always
	terminates a captured line with a newline, so splitting leaves a trailing empty
	element anyway, and a blank line carries no diagnostic value in a head/tail dump).
	Up to 20 non-blank lines are logged in full; beyond that, only the first 10 and
	last 10 are logged, separated by a marker line stating how many lines in between
	were suppressed -- a tail-only dump previously hid the actual root cause behind
	stack-trace filler, since Ruby/InSpec print the error message first and the
	backtrace after it (issue #237, confirmed against a real lab failure). Every
	window is redacted via Get-RedactedCommandString (issue #142/#236) before being
	logged, since captured stdout/stderr can echo a secret value back (e.g. ssh
	verbose output), not just the already-redacted command line logged at process
	start. Logs nothing when there are zero non-blank captured lines.

.PARAMETER StdOut
	The process's captured stdout.

.PARAMETER StdErr
	The process's captured stderr.

.PARAMETER ContextPrefix
	Text prefixed to the "captured output (...)" log message -- names the process
	and, for the timeout-kill caller, states this was a kill rather than a normal exit.

.PARAMETER WriteLogParams
	The -Source splat hashtable (from Get-LogSplat) forwarded to Write-Log.
#>
function Write-CapturedOutputWindow {
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Logs a diagnostic message only; no system state change')]
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[AllowEmptyString()]
		[string]$StdOut,

		[Parameter(Mandatory)]
		[AllowEmptyString()]
		[string]$StdErr,

		[Parameter(Mandatory)]
		[string]$ContextPrefix,

		[Parameter(Mandatory)]
		[hashtable]$WriteLogParams
	)

	# Blank lines carry no diagnostic value in a head/tail dump (and one is always
	# present anyway: AppendLine terminates every captured line with a newline, so
	# splitting leaves a trailing empty element) -- drop them from both streams rather
	# than special-casing just that one.
	$CapturedLines = @(@($StdOut, $StdErr) | Where-Object { $_ } | ForEach-Object {
		$_ -split '\r?\n' | Where-Object { $_ -ne '' }
	})

	if ($CapturedLines.Count -eq 0) { return }

	$WindowLineCount = 20
	$HalfWindowLineCount = 10

	if ($CapturedLines.Count -le $WindowLineCount) {
		# Redact before logging (issue #142's helper): stdout/stderr can echo a
		# --password/--sudo-password value back (e.g. ssh verbose/debug output), not
		# just the already-redacted command line logged at process start.
		$RedactedWindow = Get-RedactedCommandString -CommandString ($CapturedLines -join "`n")

		Write-Log "$ContextPrefix captured output ($($CapturedLines.Count) line(s)):`n$RedactedWindow" -Severity 'Error' @WriteLogParams
	} else {
		# Ruby/InSpec (and most stack-trace-producing tools) print the error message
		# first and the backtrace after it, so a tail-only dump (the original #227
		# behavior) showed nothing but "from ..." stack frames and hid the actual
		# cause -- an ssh password prompt / Errno::ENOTTY -- in the suppressed head
		# (issue #237, confirmed against a real lab failure). Surface both ends
		# instead: the first 10 lines, where the error message lives, and the last
		# 10, which keep the innermost frames / any trailing summary, around a
		# marker noting how many lines were dropped from the middle.
		$SuppressedCount = $CapturedLines.Count - ($HalfWindowLineCount * 2)
		$Head = $CapturedLines[0..($HalfWindowLineCount - 1)]
		$Tail = $CapturedLines[-$HalfWindowLineCount..-1]
		$Window = (@($Head) + @("... $SuppressedCount line(s) suppressed ...") + @($Tail)) -join "`n"
		$RedactedWindow = Get-RedactedCommandString -CommandString $Window

		Write-Log "$ContextPrefix captured output (first $HalfWindowLineCount and last $HalfWindowLineCount of $($CapturedLines.Count) line(s), $SuppressedCount line(s) suppressed):`n$RedactedWindow" -Severity 'Error' @WriteLogParams
	}
}

#endregion


#region Invoke-ExternalCommand

<#
.SYNOPSIS
	Execute external processes with timeout and output capture.

.DESCRIPTION
	Runs an external executable with configurable timeout, captures stdout/stderr,
	and returns exit code and output. Adapted from vcf-docker-download with InSpec
	exit code handling (100/101 treated as success).

	When -Background is specified, starts the process and returns a control object
	immediately without waiting for completion. Use Stop-ExternalCommand to stop
	and clean up the background process.

	On the foreground path, once the process exits, output is flushed with a
	bounded wait (FlushTimeoutMs) rather than an unbounded one: if a grandchild
	process inherited the redirected stdout/stderr pipes and is still holding
	them open, captured output may be truncated and a warning is logged instead
	of the call hanging indefinitely (see issue #208).

.PARAMETER Executable
	Path to the executable to run.

.PARAMETER Arguments
	Command-line arguments to pass to the executable.

.PARAMETER TimeoutMilliseconds
	Maximum time to wait for process completion. Default is 1 hour.

.PARAMETER FlushTimeoutMs
	Maximum time to wait, after the process has exited, for the async
	stdout/stderr handlers to finish draining before giving up and logging a
	truncation warning. Bounds the post-exit Process.WaitForExit() flush
	against a grandchild process that inherited the redirected pipes and is
	still holding them open (see issue #208); on the normal path, with no
	such grandchild, the flush still completes in full well inside this
	window. Default is 2000 (2 seconds).

.PARAMETER ProcessName
	Friendly name for logging. Default is "External Command".

.PARAMETER EnvironmentVars
	Hashtable of environment variables to set for the process.

.PARAMETER PassThru
	If set, returns output string instead of exit code.

.PARAMETER SuppressOutput
	If set, does not log stdout/stderr.

.PARAMETER Source
	Component identifier for logging.

.PARAMETER AllowedExitCodes
	Array of exit codes to treat as success. Default is @(0).

.PARAMETER Background
	If set, starts the process and returns a control object immediately
	without waiting for completion.

.PARAMETER SurfaceOutputOnFailure
	If set, and the process exits with a code outside AllowedExitCodes, logs the
	captured stdout/stderr (redacted via Get-RedactedCommandString, issue #142) at
	Error severity, with a header naming ProcessName. Up to 20 non-blank lines are
	logged in full; beyond that, only the first 10 and last 10 lines are logged,
	separated by a marker line stating how many lines in between were suppressed.
	The head/tail split (rather than a tail-only dump) exists because Ruby/InSpec
	print the error message first and the backtrace after it, so a tail-only dump
	surfaced nothing but stack frames and hid the actual root cause in the
	suppressed head (issue #237). Without this switch the captured output on a
	failing exit is only ever logged at Debug severity (or not at all, when
	-SuppressOutput is also set), which previously hid the real root cause of a
	scan failure behind a bare exit code (issue #227). The Debug-severity dump
	itself is also redacted via Get-RedactedCommandString (issue #236 -- it logs
	the FULL raw capture, not just a head/tail window, so it was the larger of the
	two exposures before this fix). Ignored when -SuppressOutput is set: a caller
	that opted out of output logging altogether should not have it reappear just
	because the process failed (e.g. the ansible-vault decrypt call in
	module.config.ps1, whose plaintext output must never reach the log). Has no
	effect on the success path.

	Also covers the timeout-kill path (issue #260): a timeout is a runner-side
	kill, not a non-zero exit, so it is a different branch than the one above --
	previously the only failure path -SurfaceOutputOnFailure did not cover at all,
	leaving an operator with nothing but "timed out after N seconds" and no clue
	where the process was stuck. On a timeout kill this logs the same
	Get-RedactedCommandString-redacted, head+tail-windowed dump (via the shared
	Write-CapturedOutputWindow helper) with a header stating this was a TIMEOUT
	KILL rather than a normal exit, before the timeout error is thrown -- the
	throw itself, and the caller's handling of it, are unchanged.

.OUTPUTS
	[int] Exit code, or [string] output if -PassThru is specified.
	[PSCustomObject] Control object if -Background is specified.

.EXAMPLE
	Invoke-ExternalCommand -Executable 'inspec' -Arguments 'exec profile' -AllowedExitCodes @(0, 100, 101) -Source 'ESXi'

.EXAMPLE
	Invoke-ExternalCommand -Executable 'inspec' -Arguments 'exec profile' -AllowedExitCodes @(0, 100, 101) -Source 'ESXi' -SurfaceOutputOnFailure
	On a non-allowed exit code, also logs the captured output at Error severity: all of it
	up to 20 lines, or the first 10 and last 10 lines (with a suppressed-count marker)
	beyond that.
#>
function Invoke-ExternalCommand {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$Executable,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$Arguments,

		[Parameter()]
		[int]$TimeoutMilliseconds = 3600000,

		[Parameter()]
		[int]$FlushTimeoutMs = 2000,

		[Parameter()]
		[string]$ProcessName = "External Command",

		[Parameter()]
		[hashtable]$EnvironmentVars = @{},

		[Parameter()]
		[switch]$PassThru,

		[Parameter()]
		[switch]$SuppressOutput,

		[Parameter()]
		[string]$Source,

		[Parameter()]
		[int[]]$AllowedExitCodes = @(0),

		[Parameter()]
		[switch]$Background,

		[Parameter()]
		[switch]$SurfaceOutputOnFailure
	)

	$WriteLogParams = Get-LogSplat $Source

	# Resolve executable from PATH if the specified path doesn't exist
	$ResolvedExecutable = $Executable
	if (-not (Test-Path -Path $Executable -PathType Leaf)) {
		$Found = Get-Command -Name (Split-Path $Executable -Leaf) -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
		if ($Found) {
			$ResolvedExecutable = $Found.Source
		}
	}

	Write-Log "Starting $ProcessName`: $ResolvedExecutable $(Get-RedactedCommandString -CommandString $Arguments)" -Severity 'Info' @WriteLogParams

	$ProcessInfo = [System.Diagnostics.ProcessStartInfo]::new()
	$ProcessInfo.FileName = $ResolvedExecutable
	$ProcessInfo.Arguments = $Arguments
	$ProcessInfo.UseShellExecute = $false
	$ProcessInfo.RedirectStandardOutput = $true
	$ProcessInfo.RedirectStandardError = $true
	$ProcessInfo.CreateNoWindow = $true

	foreach ($Key in $EnvironmentVars.Keys) {
		$ProcessInfo.EnvironmentVariables[$Key] = $EnvironmentVars[$Key]
	}

	# Compile C# helpers: thread-safe output collection, and a bounded flush
	# wrapper (see #208 -- both types are compiled together so Stop-ExternalCommand,
	# which only ever runs against a control object this function already produced,
	# can rely on ProcessFlushHelper being loaded without its own guard/Add-Type).
	if (-not ([System.Management.Automation.PSTypeName]'ProcessOutputCollector').Type) {
		Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public class ProcessOutputCollector {
	public readonly StringBuilder StdOut = new StringBuilder();
	public readonly StringBuilder StdErr = new StringBuilder();
	private int _lineCount;
	public int LineCount => _lineCount;

	public void OnStdOut(object sender, DataReceivedEventArgs e) {
		if (e.Data != null) {
			StdOut.AppendLine(e.Data);
			Interlocked.Increment(ref _lineCount);
		}
	}

	public void OnStdErr(object sender, DataReceivedEventArgs e) {
		if (e.Data != null) {
			StdErr.AppendLine(e.Data);
			Interlocked.Increment(ref _lineCount);
		}
	}
}

// Bounds the wait for the post-exit async output flush. Process.WaitForExit()
// (parameterless) is the only overload that guarantees the redirected
// stdout/stderr OutputDataReceived/ErrorDataReceived handlers have finished
// running, but it blocks until the pipes reach EOF -- which requires every
// process holding the write end (including grandchildren that inherited it)
// to close it, not just the direct child. Running that call on a background
// thread and bounding *our* wait for it with Task.Wait(timeoutMs) preserves
// the full guaranteed drain on the happy path (the task completes in low
// milliseconds once the direct child exits and nothing else holds the pipes)
// while capping the worst case when a grandchild is still holding them open.
// This has to be plain C#/.NET (no PowerShell scriptblock/delegate) because a
// PowerShell scriptblock invoked on a raw ThreadPool thread has no runspace to
// run in and throws immediately.
public static class ProcessFlushHelper {
	public static bool TryFlush(Process process, int timeoutMs) {
		var task = Task.Run(() => {
			try { process.WaitForExit(); } catch { }
		});
		return task.Wait(timeoutMs);
	}
}
'@
	}

	$Process = $null
	$Collector = [ProcessOutputCollector]::new()
	$StdOutDelegate = $null
	$StdErrDelegate = $null
	$IsBackground = $false

	try {
		$Process = [System.Diagnostics.Process]::new()
		$Process.StartInfo = $ProcessInfo
		$Process.EnableRaisingEvents = $true

		$StdOutDelegate = [System.Delegate]::CreateDelegate(
			[System.Diagnostics.DataReceivedEventHandler],
			$Collector, 'OnStdOut')
		$StdErrDelegate = [System.Delegate]::CreateDelegate(
			[System.Diagnostics.DataReceivedEventHandler],
			$Collector, 'OnStdErr')

		$Process.add_OutputDataReceived($StdOutDelegate)
		$Process.add_ErrorDataReceived($StdErrDelegate)

		$Process.Start() | Out-Null
		$Process.BeginOutputReadLine()
		$Process.BeginErrorReadLine()

		Write-Log "$ProcessName is running (PID $($Process.Id))" -Severity 'Info' @WriteLogParams

		if ($Background) {
			$IsBackground = $true
			return [PSCustomObject]@{
				Process        = $Process
				Collector      = $Collector
				StdOutDelegate = $StdOutDelegate
				StdErrDelegate = $StdErrDelegate
				ProcessName    = $ProcessName
				Source         = $Source
				StartTime      = [datetime]::UtcNow
			}
		}

		$PollMs = 1000
		$Elapsed = [System.Diagnostics.Stopwatch]::StartNew()

		while (-not $Process.HasExited) {
			if (($TimeoutMilliseconds - $Elapsed.ElapsedMilliseconds) -le 0) {
				try {
					$Process.Kill()
					$Process.WaitForExit(5000)
				} catch {
					Write-Log "Failed to kill timed-out process: $_" -Severity 'Warning' @WriteLogParams
				}

				# Surface whatever partial output was captured before the kill, the same
				# way the non-zero-exit path below does (issue #260): a timeout is a
				# runner-side kill, a different branch than that one, which
				# -SurfaceOutputOnFailure previously did not cover at all -- the operator
				# saw only "timed out after N seconds" with no clue where the process was
				# stuck. Honors -SuppressOutput the same way the non-zero-exit branch
				# does: a caller that opted out of output logging altogether should not
				# have it reappear just because the process also timed out. The header
				# names the process and the timeout, and states this was a kill (not a
				# normal exit), before the timeout error below is thrown unchanged.
				if ($SurfaceOutputOnFailure -and -not $SuppressOutput) {
					Write-CapturedOutputWindow -StdOut $Collector.StdOut.ToString() -StdErr $Collector.StdErr.ToString() -ContextPrefix "$ProcessName TIMEOUT KILL after $($TimeoutMilliseconds / 1000)s --" -WriteLogParams $WriteLogParams
				}

				throw "Process '$ProcessName' timed out after $($TimeoutMilliseconds / 1000) seconds"
			}

			$Process.WaitForExit($PollMs) | Out-Null
		}

		# Flush async output handlers with a bounded wait (see #208 and the
		# ProcessFlushHelper comment above): guarantees full drain on the happy
		# path while capping the wait if a grandchild process is still holding
		# the redirected pipes open.
		if (-not [ProcessFlushHelper]::TryFlush($Process, $FlushTimeoutMs)) {
			Write-Log "$ProcessName output flush did not complete within ${FlushTimeoutMs}ms; stdout/stderr may be truncated (a grandchild process may still be holding the output pipes open)" -Severity 'Warning' @WriteLogParams
		}
		$Elapsed.Stop()

		$StdOut = $Collector.StdOut.ToString()
		$StdErr = $Collector.StdErr.ToString()
		$ExitCode = $Process.ExitCode

		$ElapsedSec = [Math]::Round($Elapsed.Elapsed.TotalSeconds)

		if (-not $SuppressOutput) {
			# Redacted (issue #236): this Debug-severity dump is the FULL raw captured
			# output, unlike the head/tail window SurfaceOutputOnFailure logs at Error
			# severity below -- previously unredacted, so any secret in stdout/stderr (a
			# --password echoed by ssh -v, an InSpec --config JSON body, an SSHPASS=...
			# env dump, etc.) reached the log verbatim whenever Debug logging was enabled.
			if ($StdOut) {
				Write-Log "$ProcessName stdout: $(Get-RedactedCommandString -CommandString $StdOut)" -Severity 'Debug' @WriteLogParams
			}
			if ($StdErr) {
				Write-Log "$ProcessName stderr: $(Get-RedactedCommandString -CommandString $StdErr)" -Severity 'Debug' @WriteLogParams
			}
		}

		if ($ExitCode -in $AllowedExitCodes) {
			Write-Log "$ProcessName completed successfully (${ElapsedSec}s, $($Collector.LineCount) lines)" -Severity 'Success' @WriteLogParams
		} else {
			Write-Log "$ProcessName exited with code $ExitCode (${ElapsedSec}s, $($Collector.LineCount) lines)" -Severity 'Warning' @WriteLogParams

			# Surface the captured output that -SuppressOutput/the Debug-only logging above
			# would otherwise hide from the operator at the default Info log level (issue
			# #227): a failing InSpec run's actual error (e.g. an SSH auth failure) was
			# previously visible only at Debug severity, so the operator saw just the bare
			# exit code and had to reproduce the failure by hand to find the real cause.
			# Opt-in (only the InSpec scan call in module.scan.ps1 sets it) so every other
			# Invoke-ExternalCommand caller's failure-path behavior is unchanged.
			if ($SurfaceOutputOnFailure -and -not $SuppressOutput) {
				Write-CapturedOutputWindow -StdOut $StdOut -StdErr $StdErr -ContextPrefix $ProcessName -WriteLogParams $WriteLogParams
			}
		}

		if ($PassThru) {
			return $StdOut
		}
		return $ExitCode
	} finally {
		if (-not $IsBackground) {
			if ($StdOutDelegate -and $Process) { $Process.remove_OutputDataReceived($StdOutDelegate) }
			if ($StdErrDelegate -and $Process) { $Process.remove_ErrorDataReceived($StdErrDelegate) }
			if ($Process) {
				if (-not $Process.HasExited) {
					try {
						Write-Log "Killing $ProcessName (PID $($Process.Id)) during cleanup" -Severity 'Warning' @WriteLogParams
						$Process.Kill()
						$Process.WaitForExit(5000)
					} catch { }
				}
				$Process.Dispose()
			}
		}
	}
}

#endregion

#region Stop-ExternalCommand

<#
.SYNOPSIS
	Stop a background process started by Invoke-ExternalCommand -Background.

.DESCRIPTION
	Stops the process in two tiers, then drains collected stdout/stderr, logs
	the output (redacted via Get-RedactedCommandString, issue #236), and
	disposes the process.

	The post-exit output flush uses a bounded wait (FlushTimeoutMs): if a
	grandchild process inherited the redirected stdout/stderr pipes and is
	still holding them open past the direct child's own exit, captured output
	may be truncated and a warning is logged instead of this function hanging
	indefinitely (see issue #208).

	Tier 1 (graceful, non-Windows only): sends SIGTERM to the process via a
	direct libc kill() P/Invoke -- the same style of native call used by
	Set-StigTlsTrust's libc setenv() in module.config.ps1, and cheaper/more
	reliable than shelling out to /bin/kill (no fork/exec, no PATH lookup,
	the signal delivery result is a checkable return code). Waits up to
	GracePeriodMs for the process to exit on its own.

	Tier 2 (hard kill): if the process is still running after the graceful
	tier -- or on Windows, where the graceful tier is skipped entirely and
	this is the only tier -- calls $Process.Kill() (SIGKILL on Linux) and
	waits up to GracePeriodMs again for exit confirmation, logging a warning
	if it still hasn't exited.

	The watcher's stigman-watcher child process is the motivating consumer:
	it honors SIGTERM and shuts down cleanly (see module.watcher.Tests.ps1's
	"received shutdown event with code 0, exiting" fixture), so the graceful
	tier gives an in-flight CKL upload a chance to finish or flush before the
	connection is severed, rather than being cut mid-request by an immediate
	SIGKILL.

.PARAMETER ControlObject
	Control object returned by Invoke-ExternalCommand -Background.

.PARAMETER GracePeriodMs
	Milliseconds to wait for the process to exit after each tier (SIGTERM,
	then Kill()) before moving on / giving up. Default 10000.

.PARAMETER FlushTimeoutMs
	Maximum time to wait, after the process has exited, for the async
	stdout/stderr handlers to finish draining before giving up and logging a
	truncation warning. Bounds the post-exit Process.WaitForExit() flush
	against a grandchild process that inherited the redirected pipes and is
	still holding them open (see issue #208); on the normal path, with no
	such grandchild, the flush still completes in full well inside this
	window. Default is 2000 (2 seconds).

.PARAMETER Source
	Component identifier for logging.
#>
function Stop-ExternalCommand {
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Terminates a background process started by Invoke-ExternalCommand -Background (graceful SIGTERM tier, then a Kill() fallback) and drains its output; called only by the orchestrator''s own cleanup path, with no interactive operator and no externally-visible destructive state for -WhatIf to preview')]
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[PSCustomObject]$ControlObject,

		[Parameter()]
		[int]$GracePeriodMs = 10000,

		[Parameter()]
		[int]$FlushTimeoutMs = 2000,

		[Parameter()]
		[string]$Source
	)

	$WriteLogParams = Get-LogSplat $Source

	$Process = $ControlObject.Process
	$Collector = $ControlObject.Collector
	$ProcessName = $ControlObject.ProcessName

	if ($null -eq $Process) {
		Write-Log "No process to stop" -Severity 'Debug' @WriteLogParams
		return
	}

	if ($Process.HasExited) {
		Write-Log "$ProcessName already exited (exit code $($Process.ExitCode))" -Severity 'Info' @WriteLogParams
	} else {
		try {
			Write-Log "Stopping $ProcessName (PID $($Process.Id))" -Severity 'Info' @WriteLogParams

			$Exited = $false
			if (-not $IsWindows) {
				# Graceful tier: SIGTERM via a direct libc kill() P/Invoke rather than
				# shelling out to /bin/kill -- no fork/exec, no PATH resolution, and the
				# signal delivery itself is a checkable return code. Same style of native
				# call as Set-StigTlsTrust's libc setenv() P/Invoke in module.config.ps1.
				if (-not ('StigProcessNative' -as [type])) {
					Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class StigProcessNative {
	[DllImport("libc", SetLastError = true)]
	public static extern int kill(int pid, int sig);
}
'@
				}

				# SIGTERM = 15
				if ([StigProcessNative]::kill($Process.Id, 15) -eq 0) {
					$Exited = $Process.WaitForExit($GracePeriodMs)
				} else {
					Write-Log "SIGTERM to $ProcessName (PID $($Process.Id)) failed; falling back to an immediate kill" -Severity 'Debug' @WriteLogParams
				}
			}

			if (-not $Exited -and -not $Process.HasExited) {
				if (-not $IsWindows) {
					Write-Log "$ProcessName (PID $($Process.Id)) did not exit within the graceful shutdown window; killing" -Severity 'Warning' @WriteLogParams
				}
				$Process.Kill()
				$Exited = $Process.WaitForExit($GracePeriodMs)
			} elseif (-not $Exited) {
				# HasExited flipped true between the SIGTERM WaitForExit timeout and this
				# check (race) -- the process is gone, so treat it as a graceful exit
				# rather than calling Kill() on an already-exited process.
				$Exited = $true
			}

			if ($Exited) {
				Write-Log "$ProcessName stopped (PID $($Process.Id))" -Severity 'Success' @WriteLogParams
			} else {
				Write-Log "$ProcessName did not exit within grace period" -Severity 'Warning' @WriteLogParams
			}
		} catch {
			Write-Log "Error stopping $ProcessName`: $($_.Exception.Message)" -Severity 'Warning' @WriteLogParams
		}
	}

	# Flush async event handlers with a bounded wait (see the ProcessFlushHelper
	# comment in Invoke-ExternalCommand and issue #208): a grandchild process
	# that inherited the redirected pipes and outlived the direct child (e.g. a
	# killed child that had backgrounded a task) can hold them open well past
	# this cleanup path's own kill tiers above, so an unbounded flush here could
	# stall the caller indefinitely on an otherwise-successful stop.
	if ($Process.HasExited) {
		try {
			if (-not [ProcessFlushHelper]::TryFlush($Process, $FlushTimeoutMs)) {
				Write-Log "$ProcessName output flush did not complete within ${FlushTimeoutMs}ms; stdout/stderr may be truncated (a grandchild process may still be holding the output pipes open)" -Severity 'Warning' @WriteLogParams
			}
		} catch { }
	}

	# Log collected output. Redacted (issue #236): this background-process drain is the
	# same full raw-capture shape as Invoke-ExternalCommand's foreground Debug dump above,
	# and was equally unredacted -- the stigman-watcher child this function stops runs for
	# the life of a scan, so its captured stdout/stderr is exactly the kind of long-lived
	# stream that could echo a secret (e.g. a connection-string password on a retry log
	# line) before this cleanup path ever runs.
	try {
		$StdOut = $Collector.StdOut.ToString()
		$StdErr = $Collector.StdErr.ToString()

		if ($StdOut) {
			Write-Log "$ProcessName stdout: $(Get-RedactedCommandString -CommandString $StdOut)" -Severity 'Debug' @WriteLogParams
		}
		if ($StdErr) {
			$LogSeverity = if ($Process.HasExited -and $Process.ExitCode -ne 0) { 'Warning' } else { 'Debug' }
			Write-Log "$ProcessName stderr: $(Get-RedactedCommandString -CommandString $StdErr)" -Severity $LogSeverity @WriteLogParams
		}
	} catch {
		Write-Log "Could not read $ProcessName output: $($_.Exception.Message)" -Severity 'Debug' @WriteLogParams
	}

	# Cleanup delegates and dispose
	if ($ControlObject.StdOutDelegate -and $Process) { $Process.remove_OutputDataReceived($ControlObject.StdOutDelegate) }
	if ($ControlObject.StdErrDelegate -and $Process) { $Process.remove_ErrorDataReceived($ControlObject.StdErrDelegate) }
	$Process.Dispose()
}

#endregion

#region Test-EnvironmentDependencies

<#
.SYNOPSIS
	Validate that required tools and modules are available.

.DESCRIPTION
	Checks for inspec, saf CLI, and VMware PowerCLI modules. Throws if any
	required dependency is missing.

	Also validates that the VMware.PowerCLI META-MODULE (not just its
	VMware.VimAutomation.Core submodule) is genuinely loaded (issue #307):
	cmdlet AUTOLOAD only pulls in the handful of submodules containing the
	first invoked cmdlet (~5 of 83), and under that partial load live guest
	hydration for powered-on VMs fails silently ($vm.Guest /
	$vm.ExtensionData.Guest come back null). The image's AllUsersAllHosts
	profile (/opt/microsoft/powershell/7/profile.ps1) imports the full
	meta-module for every profile-bearing pwsh process (entrypoint, -Shell
	child), so this should already be satisfied here; this check self-heals
	a -NoProfile invocation (or any process that skipped the profile) by
	importing it here with a timed log, and fails loud if the import throws
	or the loaded module count still falls short of a sanity floor -- a
	partial/broken PowerCLI installation. The remediation child pwsh also
	runs -NoProfile and is not reached by this parent-side check; it
	prepends its own explicit -ErrorAction Stop import to its command
	string (module.remediation.ps1).

.PARAMETER Source
	Component identifier for logging.
#>
function Test-EnvironmentDependencies {
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Validates the whole set of required environment dependencies (inspec, saf, PowerCLI) in one pass')]
	[CmdletBinding()]
	param(
		[Parameter()]
		[string]$Source = 'Environment'
	)

	$WriteLogParams = Get-LogSplat $Source

	Write-Log "Performing environment checks" -Severity 'Info' @WriteLogParams

	Write-Log "Checking for required tools: inspec and saf cli" -Severity 'Verbose' @WriteLogParams
	if ($null -eq (Get-Command saf -ErrorAction SilentlyContinue)) {
		throw "SAF CLI not found. Please ensure 'saf' is installed and in your system's PATH."
	}
	if ($null -eq (Get-Command inspec -ErrorAction SilentlyContinue)) {
		throw "InSpec not found. Please ensure 'inspec' (cinc-auditor) is installed and in your system's PATH."
	}

	Write-Log "Loading required PowerShell modules" -Severity 'Verbose' @WriteLogParams
	try {
		Import-Module VMware.VimAutomation.Core -ErrorAction Stop
	} catch {
		throw "Failed to load VMware.VimAutomation.Core module. Please ensure PowerCLI is installed: $($_.Exception.Message)"
	}

	# issue #307: validate the VMware.PowerCLI META-MODULE is genuinely loaded, not just
	# the VMware.VimAutomation.Core submodule checked above. A -NoProfile invocation (or
	# any process for which the image's AllUsersAllHosts profile didn't run) leaves
	# PowerCLI on cmdlet autoload, which silently loads only a handful of submodules and
	# breaks live guest data hydration -- self-heal it here instead of failing at scan time.
	$ProfilePath = '/opt/microsoft/powershell/7/profile.ps1'
	$PowerCLIModule = Get-Module -Name VMware.PowerCLI
	if ($PowerCLIModule) {
		$LoadedVMwareModuleCount = @(Get-Module VMware.*).Count
		Write-Log "VMware.PowerCLI already loaded ($LoadedVMwareModuleCount VMware.* modules)" -Severity 'Info' @WriteLogParams
	} else {
		Write-Log "VMware.PowerCLI not loaded; the AllUsersAllHosts profile ($ProfilePath) did not run for this process. Importing it now." -Severity 'Warning' @WriteLogParams
		try {
			$ImportDuration = Measure-Command { Import-Module VMware.PowerCLI -ErrorAction Stop }
		} catch {
			throw "Failed to load VMware.PowerCLI module. Please ensure PowerCLI is installed and the $ProfilePath profile is intact: $($_.Exception.Message)"
		}
		$LoadedVMwareModuleCount = @(Get-Module VMware.*).Count
		Write-Log "VMware.PowerCLI imported in $([math]::Round($ImportDuration.TotalSeconds, 1))s ($LoadedVMwareModuleCount VMware.* modules)" -Severity 'Info' @WriteLogParams
	}

	if ($LoadedVMwareModuleCount -lt 50) {
		throw "VMware.PowerCLI module set appears incomplete: only $LoadedVMwareModuleCount VMware.* modules loaded (expected at least 50). The PowerCLI installation may be broken or partial."
	}

	Write-Log "All environment dependencies validated" -Severity 'Success' @WriteLogParams
}

#endregion

#region New-ScanDirectoryStructure

<#
.SYNOPSIS
	Create the timestamped report directory structure for a scan run.

.DESCRIPTION
	Creates the base report directory with a timestamped subdirectory, and CKL
	subdirectories organized by catalog-declared report group (issue #134): the set of
	ckls/<group> subdirectories to create comes from Get-CatalogReportGroupMap (every
	stig-kind reportGroup in the loaded catalog) rather than a hardcoded product list, so
	a new product's report subdirectory needs only a catalog change, not a code change.
	The one exception is "srg" -- the shared HDF-only report group every SRG product
	declares -- which is created directly under the run root (outside the watched ckls/
	tree, since SRG products produce no CKL) rather than as a ckls/ subdirectory.

	Sets $Script:RuntimeConfig.WatcherCklPath to the CKL base directory (issue #217 slice
	2 of epic #137 -- WatcherCklPath is sole-sourced from RuntimeConfig, no standalone
	global).

.PARAMETER ReportPath
	Base directory for reports. Defaults to $Script:RuntimeConfig.ReportsBasePath.

.PARAMETER Source
	Component identifier for logging.

.OUTPUTS
	[PSCustomObject] with properties: RunRoot, CklBase, VMCklPath, ESXiCklPath,
	VCenterCklPath, VCSACklPath.
#>
function New-ScanDirectoryStructure {
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Creates the timestamped report/CKL directory tree inside the container''s local scan-output filesystem; this scaffolding is required for every scan run to proceed, so there is no operator-facing or destructive change for -WhatIf to preview')]
	[CmdletBinding()]
	param(
		[Parameter()]
		[string]$ReportPath,

		[Parameter()]
		[string]$Source = 'Setup'
	)

	$WriteLogParams = Get-LogSplat $Source

	if (-not $ReportPath) {
		$ReportPath = $Script:RuntimeConfig.ReportsBasePath
	}

	Write-Log "Creating report output directories" -Severity 'Info' @WriteLogParams

	if (-not (Test-Path -Path $ReportPath)) {
		Write-Log "Base report path '$ReportPath' doesn't exist, creating it" -Severity 'Verbose' @WriteLogParams
		New-Item -ItemType Directory -Path $ReportPath -Force -ErrorAction Stop | Out-Null
	}

	$RunReportRoot = New-Item -ItemType Directory -Path (Join-Path $ReportPath "reports_$(Get-Date -Format 'MM-dd-yyyy_HH-mm')") -Force -ErrorAction Stop
	Write-Log "Reports will be saved in: $($RunReportRoot.FullName)" -Severity 'Success' @WriteLogParams

	$CklBaseDir = New-Item -ItemType Directory -Path (Join-Path $RunReportRoot.FullName "ckls") -Force -ErrorAction Stop
	$Script:RuntimeConfig.WatcherCklPath = $CklBaseDir.FullName

	# Catalog-declared report groups (issue #134). Only STIG groups get a directory here:
	# a watched ckls/<group> subdirectory (converted to CKL, uploaded to STIG Manager).
	# SRG groups are HDF-only (no CKL/STIG Manager; viewed in Heimdall) and each writing
	# transport creates its own per-component run-root <group>/ dir on demand (issue
	# #350/#351/#356) -- so a group whose transport writes elsewhere (the vmware/nsx-api
	# transports use their own paths) never leaves an empty dir here, and only the srg
	# products actually scanned this run leave one. An empty map means the catalog hasn't
	# been loaded (Import-StigCatalog must run before this function) -- fail loudly rather
	# than silently create zero subdirectories, which would misroute every report.
	$Map = Get-CatalogReportGroupMap
	if ($Map.Count -eq 0) {
		throw "No catalog 'reportGroup' entries found; Import-StigCatalog must run before New-ScanDirectoryStructure. Check settings/catalog.json."
	}

	$CklPaths = @{}
	foreach ($Entry in $Map.Values) {
		if ($Entry.Kind -eq 'srg' -or [string]::IsNullOrEmpty($Entry.ReportGroup)) { continue }
		if (-not $CklPaths.ContainsKey($Entry.ReportGroup)) {
			$CklPaths[$Entry.ReportGroup] = New-Item -ItemType Directory -Path (Join-Path $CklBaseDir.FullName $Entry.ReportGroup) -Force -ErrorAction Stop
		}
	}

	return [PSCustomObject]@{
		RunRoot        = $RunReportRoot
		CklBase        = $CklBaseDir
		VMCklPath      = $CklPaths['vm']
		ESXiCklPath    = $CklPaths['esxi']
		VCenterCklPath = $CklPaths['vcenter']
		VCSACklPath    = $CklPaths['vcsa']
		NsxCklPath     = $CklPaths['nsx']
	}
}

#endregion

#region Get-SanitizedName

<#
.SYNOPSIS
	Sanitize a name for use in filenames.

.DESCRIPTION
	Removes or replaces characters that are not safe for filenames.

.PARAMETER Name
	The name to sanitize.

.OUTPUTS
	[string] Sanitized name.
#>
function Get-SanitizedName {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[string]$Name
	)

	return ($Name -replace '[^a-zA-Z0-9_-]', '')
}

#endregion


#region Get-TargetShortName

<#
.SYNOPSIS
	Derive a target's short name from its connection host (issue #244).

.DESCRIPTION
	Every transport builds a short, filename-/asset-name-friendly identifier from a site
	target's connection.host. Historically this was always "take the first dot-separated
	label" (`$HostName.Split('.')[0]`), which is correct for an FQDN
	(esxi-01.example.internal -> esxi-01) but wrong for an IP-literal host: splitting
	192.0.2.1 on '.' yields '192', so two targets that share a first octet (192.0.2.10
	and 192.0.2.110) collide on report/CKL filenames and STIG Manager asset names.

	This function keeps that FQDN behavior byte-identical but special-cases IP literals
	(detected via [ipaddress]::TryParse, so it covers IPv4 and IPv6 alike): an IPv4
	literal is returned unchanged (its dots are legal in filenames on both Linux and
	Windows, so nothing needs sanitizing); an IPv6 literal has its ':' characters
	replaced with '-' because ':' is illegal in Windows filenames/paths and
	reports/CKLs are routinely opened on Windows (e.g. STIG Viewer). The substitution is
	a 1:1 character replacement, so two distinct IPv6 addresses can never collide as a
	result of it. A single-label bare hostname (no dot) is returned unchanged, same as
	today's Split behavior.

.PARAMETER HostName
	The connection host to derive a short name from -- an FQDN, a single-label
	hostname, or an IPv4/IPv6 literal.

.OUTPUTS
	[string] The derived short name.
#>
function Get-TargetShortName {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[string]$HostName
	)

	$ParsedIp = $null
	if ([ipaddress]::TryParse($HostName, [ref]$ParsedIp)) {
		if ($ParsedIp.AddressFamily -eq 'InterNetworkV6') {
			return ($HostName -replace ':', '-')
		}
		return $HostName
	}

	return ($HostName -split '\.')[0]
}

#endregion


#region Site target short-name collision handling (issue #282)

<#
.SYNOPSIS
	Derive a row-specific differentiator string for a site target's short name.

.DESCRIPTION
	When two site.targets rows collide on Get-TargetShortName (e.g. web01.site-a.example
	and web01.site-b.example both derive 'web01'), Resolve-SiteTargetShortName appends a
	per-row differentiator so their report/CKL paths stay distinct (issue #282). This
	helper picks that differentiator: the row's optional `name` field when set (the
	operator's own identity for the row, epic #279), else the host's remaining FQDN
	labels (everything after the first label -- the part Get-TargetShortName discards,
	and exactly what distinguishes the colliding hosts), else '' (an IP-literal or
	single-label host has no remainder; Resolve-SiteTargetShortName then falls back to
	the row's site.targets position). The result is sanitized to filename-/CKL-asset-
	safe characters ([A-Za-z0-9_-]) via 1:1 replacement.

.PARAMETER Target
	A site.targets entry: @{ product; version; kind; connection = @{ host }; and
	optionally name }.

.OUTPUTS
	[string] The sanitized differentiator, or '' when the row offers none.
#>
function Get-SiteTargetDifferentiator {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		$Target
	)

	# Optional row name (epic #279): null-safe access -- rows without the field yield $null.
	$RowName = [string]$Target.name
	if (-not [string]::IsNullOrWhiteSpace($RowName)) {
		return ($RowName -replace '[^A-Za-z0-9_-]', '-')
	}

	$HostName = [string]$Target.connection.host
	$ParsedIp = $null
	if (-not [ipaddress]::TryParse($HostName, [ref]$ParsedIp) -and $HostName.Contains('.')) {
		# The FQDN labels Get-TargetShortName discarded -- what actually distinguishes
		# web01.site-a.example from web01.site-b.example.
		$Remainder = ($HostName -split '\.', 2)[1]
		return ($Remainder -replace '[^A-Za-z0-9_-]', '-')
	}

	return ''
}

<#
.SYNOPSIS
	Resolve a site target row's short name, disambiguated on collision (issue #282).

.DESCRIPTION
	Report/CKL file names and STIG Manager asset names key on Get-TargetShortName (the
	host's first FQDN label), so two site.targets rows whose hosts share a first label
	(web01.site-a.example + web01.site-b.example -> 'web01') would silently overwrite
	each other's HDF/CKL/summary files within one run. This function is the transport
	builders' collision guard: it pre-scans $Script:Site.targets for OTHER rows of the
	same product and kind (the rows that share this row's output-path shape) whose host
	derives the same short name.

	No collision (the overwhelmingly common case, and every single-row site): returns
	Get-TargetShortName unchanged -- existing paths and asset names stay byte-identical.

	Collision: logs a Warning naming every involved row and returns
	'<shortName>-<differentiator>' (row `name` if set, else the host's remaining FQDN
	labels -- see Get-SiteTargetDifferentiator). If the colliding rows' differentiators
	ALSO tie (e.g. the same host configured twice with no row names), the row's 1-based
	position in site.targets is appended ('-rowN') so the result is still distinct.

	The pre-scan is deliberately stateless and configuration-driven (all of
	$Script:Site.targets, not just the rows built this run): a builder sees one row at a
	time, and per-run mutable state would need an orchestrator-owned reset. It also
	makes the disambiguated names STABLE -- a row keeps the same short name whether its
	collision partner was scanned, filtered out, or unreachable this run, so STIG
	Manager asset names never flap between runs. Rows not present in $Script:Site
	(bare test/CLI construction) resolve exactly as before.

.PARAMETER Target
	A site.targets entry: @{ product; version; kind; connection = @{ host }; and
	optionally name }. The caller must have validated connection.host as non-empty.

.PARAMETER Source
	Component identifier for logging.

.OUTPUTS
	[string] The short name, disambiguated when the row collides with another.
#>
function Resolve-SiteTargetShortName {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		$Target,

		[Parameter()]
		[string]$Source = 'Common'
	)

	$WriteLogParams = Get-LogSplat $Source

	$ShortName = Get-TargetShortName -HostName ([string]$Target.connection.host)

	# Null-safe: $Script:Site is module.config state, unset until a site config loads.
	$SiteRows = @()
	if ($null -ne $Script:Site -and $null -ne $Script:Site.targets) {
		$SiteRows = @($Script:Site.targets)
	}

	# Other rows of the same product+kind (the ones whose report-path shape this row
	# shares) that derive the same short name. Reference equality excludes only THIS
	# row object, so a literal duplicate row still registers as a collision.
	$Colliders = [System.Collections.Generic.List[PSObject]]::new()
	foreach ($Row in $SiteRows) {
		if ($null -eq $Row -or [object]::ReferenceEquals($Row, $Target)) { continue }
		if ([string]$Row.product -ne [string]$Target.product) { continue }
		if ([string]$Row.kind -ne [string]$Target.kind) { continue }
		$RowHost = [string]$Row.connection.host
		if ([string]::IsNullOrWhiteSpace($RowHost)) { continue }
		if ((Get-TargetShortName -HostName $RowHost) -eq $ShortName) {
			$Colliders.Add($Row)
		}
	}

	if ($Colliders.Count -eq 0) { return $ShortName }

	# Name every involved row: the operator must see WHICH rows collide, not just that
	# something did.
	$RowLabel = {
		param($Row)
		if (-not [string]::IsNullOrWhiteSpace([string]$Row.name)) { [string]$Row.name } else { [string]$Row.connection.host }
	}
	$AllLabels = @(& $RowLabel $Target) + @($Colliders | ForEach-Object { & $RowLabel $_ })

	$Differentiator = Get-SiteTargetDifferentiator -Target $Target
	$Candidate = if ($Differentiator) { "$ShortName-$Differentiator" } else { $ShortName }

	# Guarantee distinctness even when the differentiators tie (identical hosts, no row
	# names): append this row's 1-based site.targets position. Colliding rows compute
	# their candidates the same way, so a tie is symmetric and each row gets its own
	# position suffix.
	$TiedCandidates = @($Colliders | Where-Object {
		$OtherDifferentiator = Get-SiteTargetDifferentiator -Target $_
		$OtherCandidate = if ($OtherDifferentiator) { "$ShortName-$OtherDifferentiator" } else { $ShortName }
		$OtherCandidate -eq $Candidate
	})
	if ($TiedCandidates.Count -gt 0) {
		for ($i = 0; $i -lt $SiteRows.Count; $i++) {
			if ([object]::ReferenceEquals($SiteRows[$i], $Target)) {
				$Candidate = "$Candidate-row$($i + 1)"
				break
			}
		}
	}

	Write-Log ("Short-name collision on '$ShortName' for product '$([string]$Target.product)': site target rows '" +
		($AllLabels -join "', '") +
		"' all derive it; this row's reports use disambiguated short name '$Candidate' " +
		"(its CKL hostname and STIG Manager asset names follow)") -Severity 'Warning' @WriteLogParams

	return $Candidate
}

#endregion
