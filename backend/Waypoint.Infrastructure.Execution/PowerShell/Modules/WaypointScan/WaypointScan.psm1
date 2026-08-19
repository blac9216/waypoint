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

# Waypoint-owned shim (issue #274, second slice of the #23 split), following
# WaypointDiscovery's pattern (issue #21): this is the thin, Waypoint-authored seam
# that dot-sources the project-owned sibling repository's unmodified
# vmware-stig-docker module.common.ps1 to reuse its
# Invoke-ExternalCommand process-capture helper, then drives `inspec exec` against a
# single vSphere target the same way module.transport.vmware.ps1's
# Build-VsphereTransportTargets does (VISERVER/VISERVER_USERNAME/VISERVER_PASSWORD env,
# --reporter=json:<path>, --enhanced-outcomes, AllowedExitCodes 0/100/101).
#
# This is deliberately a single-target, vSphere-only invocation -- the sibling repo's
# Get-ScanScriptBlock is coupled to its parallel engine's $Target object (built by
# Build-VsphereTransportTargets from a whole-site connect) and to multi-transport
# branching (ssh/vcsa-component/srg) this M1 slice does not need. Re-driving `inspec`
# directly here, the same way WaypointDiscovery re-drives PowerCLI directly instead of
# forcing Get-StigTargets' scan-target shape, keeps this module honest about exactly
# what it needs from the sibling-repository code: one process-capture helper.

$Script:VmwareStigDockerCommonModulePath = $env:WAYPOINT_VMWARE_STIG_DOCKER_COMMON_PATH
$Script:VmwareStigDockerNsxApiModulePath = $env:WAYPOINT_VMWARE_STIG_DOCKER_NSXAPI_PATH

function Invoke-WaypointScan {
	<#
	.SYNOPSIS
	    Runs `inspec exec` against a single vSphere target and returns the HDF report
	    path plus outcome.

	.PARAMETER VCenter
	    FQDN or IP of the vCenter Server (target.connection.host).

	.PARAMETER Username
	    vSphere SSO username, decrypted credential username half.

	.PARAMETER Password
	    vSphere SSO password, bound as a typed parameter -- never interpolated into
	    script text (security.md controls 1/2).

	.PARAMETER ProfilePath
	    Path to the InSpec profile (compliance content) to execute.

	.PARAMETER ReportPath
	    Where InSpec writes its JSON (HDF) report.

	.PARAMETER VmwareStigDockerCommonPath
	    Path to the sibling repo's unmodified module.common.ps1,
	    dot-sourced to bring Invoke-ExternalCommand into scope.

	.OUTPUTS
	    One [pscustomobject]: Success (bool), ExitCode (int), ReportPath (string),
	    FailureReason (string, only set when Success is $false).
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$VCenter,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$Username,

		[Parameter(Mandatory)]
		[AllowEmptyString()]
		[string]$Password,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$ProfilePath,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$ReportPath,

		[Parameter()]
		[int]$TimeoutSeconds = 1800,

		[Parameter()]
		[string]$VmwareStigDockerCommonPath = $Script:VmwareStigDockerCommonModulePath
	)

	if ([string]::IsNullOrWhiteSpace($VmwareStigDockerCommonPath)) {
		throw 'WaypointScan: no module.common.ps1 path configured (WAYPOINT_VMWARE_STIG_DOCKER_COMMON_PATH or -VmwareStigDockerCommonPath).'
	}

	if (-not (Test-Path -Path $VmwareStigDockerCommonPath -PathType Leaf)) {
		throw "WaypointScan: module.common.ps1 not found at '$VmwareStigDockerCommonPath'."
	}

	# Dot-source the unmodified sibling-repository script to bring Invoke-ExternalCommand into scope.
	. $VmwareStigDockerCommonPath

	$ReportDirectory = Split-Path -Path $ReportPath -Parent
	if ($ReportDirectory -and -not (Test-Path -Path $ReportDirectory -PathType Container)) {
		New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
	}

	# Same env-export shape module.scan.ps1 uses for vmware:// transport targets: InSpec
	# (via train-vmware) reads these three, never argv, so the password never touches
	# the process table (issue #142's constraint, honored here too).
	$EnvVars = @{
		'NO_COLOR'          = $true
		'VISERVER'          = $VCenter
		'VISERVER_USERNAME' = $Username
		'VISERVER_PASSWORD' = $Password
	}

	$InspecArguments = "`"$ProfilePath`" -t vmware:// --reporter=json:`"$ReportPath`" --show-progress --enhanced-outcomes"

	try {
		# AllowedExitCodes 0/100/101 mirrors module.scan.ps1's own call: InSpec's exit
		# codes 100 (compliance failures present) and 101 (skipped controls present) are
		# BOTH a completed, reportable scan, not a tool failure -- only a code outside
		# this set (crash, argument error, transport failure) is InSpec itself failing.
		$null = Invoke-ExternalCommand -Executable 'inspec' -Arguments "exec $InspecArguments" `
			-TimeoutMilliseconds ($TimeoutSeconds * 1000) -ProcessName "InSpec scan for $VCenter" `
			-EnvironmentVars $EnvVars -AllowedExitCodes @(0, 100, 101) -Source 'vsphere' -SurfaceOutputOnFailure

		if (-not (Test-Path -Path $ReportPath -PathType Leaf)) {
			return [pscustomobject]@{
				Success       = $false
				ExitCode      = $null
				ReportPath    = $null
				FailureReason = "InSpec scan completed but report file not found at $ReportPath."
			}
		}

		return [pscustomobject]@{
			Success       = $true
			ExitCode      = 0
			ReportPath    = $ReportPath
			FailureReason = $null
		}
	} catch {
		return [pscustomobject]@{
			Success       = $false
			ExitCode      = $null
			ReportPath    = $null
			FailureReason = "InSpec scan failed for $VCenter`: $($_.Exception.Message)"
		}
	}
}

# Applies a SAF attestation template to an HDF report (issue #275, third slice of the
# #23 split), or passes the report through unattested when AttestTemplatePath is
# $null/empty -- a valid "no attestation docs" path per #275 AC. Mirrors
# module.scan.ps1's "Step 2: Apply SAF attestation" branch (`saf attest apply -i <hdf>
# <template> -o <attested>`), re-driven directly the same way Invoke-WaypointScan
# re-drives `inspec`. AttestTemplatePath is the handler-resolved, already
# expiry-filtered config-doc body (ScanJobHandler writes it to a temp file before
# calling this) -- this function does not itself decide expiry.
# Returns [pscustomobject]: Success, AttestApplied ($false for the pass-through path),
# ReportPath (the attested report when applied, else the original), FailureReason.
function Invoke-WaypointAttest {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$ReportPath,

		[Parameter()]
		[AllowNull()]
		[AllowEmptyString()]
		[string]$AttestTemplatePath,

		[Parameter()]
		[string]$AttestedReportPath,

		[Parameter()]
		[int]$TimeoutSeconds = 300,

		[Parameter()]
		[string]$VmwareStigDockerCommonPath = $Script:VmwareStigDockerCommonModulePath
	)

	if ([string]::IsNullOrWhiteSpace($AttestTemplatePath)) {
		return [pscustomobject]@{
			Success       = $true
			AttestApplied = $false
			ReportPath    = $ReportPath
			FailureReason = $null
		}
	}

	if ([string]::IsNullOrWhiteSpace($VmwareStigDockerCommonPath)) {
		throw 'WaypointScan: no module.common.ps1 path configured (WAYPOINT_VMWARE_STIG_DOCKER_COMMON_PATH or -VmwareStigDockerCommonPath).'
	}

	if (-not (Test-Path -Path $VmwareStigDockerCommonPath -PathType Leaf)) {
		throw "WaypointScan: module.common.ps1 not found at '$VmwareStigDockerCommonPath'."
	}

	. $VmwareStigDockerCommonPath

	try {
		# SafAttestArgs shape mirrors New-AttestStep's `attest apply -i <hdf> <template>
		# -o <attested>` exactly.
		$null = Invoke-ExternalCommand -Executable 'saf' -Arguments "attest apply -i `"$ReportPath`" `"$AttestTemplatePath`" -o `"$AttestedReportPath`"" `
			-TimeoutMilliseconds ($TimeoutSeconds * 1000) -ProcessName 'SAF attestation' `
			-AllowedExitCodes @(0) -Source 'vsphere' -SurfaceOutputOnFailure

		if (-not (Test-Path -Path $AttestedReportPath -PathType Leaf)) {
			return [pscustomobject]@{
				Success       = $false
				AttestApplied = $false
				ReportPath    = $null
				FailureReason = "SAF attestation completed but attested report file not found at $AttestedReportPath."
			}
		}

		return [pscustomobject]@{
			Success       = $true
			AttestApplied = $true
			ReportPath    = $AttestedReportPath
			FailureReason = $null
		}
	} catch {
		return [pscustomobject]@{
			Success       = $false
			AttestApplied = $false
			ReportPath    = $null
			FailureReason = "SAF attestation failed: $($_.Exception.Message)"
		}
	}
}

# Converts an (optionally attested) HDF report to a CKL and stamps static benchmark
# metadata onto its STIG_INFO header (issue #275, third slice of the #23 split).
# Mirrors module.scan.ps1's "Step 3: Convert HDF to CKL" (`saf convert hdf2ckl -i
# <input> -o <ckl>`) and "Step 4: Correct CKL benchmark metadata" -- re-driven
# directly, same rationale as Invoke-WaypointScan/Invoke-WaypointAttest. Metadata
# correction is Waypoint-owned (Set-WaypointCklBenchmarkMetadata below), not a
# dot-source of the sibling repo's Set-CklBenchmarkMetadata: that version's RuleMap
# correction needs a live STIG Manager connection (Resolve-BenchmarkMetadata), which
# is #25's integration, not this slice's -- this stamps only the static STIG_INFO
# identity fields an operator configures ahead of time (ScanOptions.BenchmarkMetadata),
# and is non-fatal exactly like the sibling repo's own correction step (a correction failure
# still returns Success -- the raw, already-uploadable CKL was already written).
# Returns [pscustomobject]: Success, CklPath, MetadataApplied, FailureReason.
function Invoke-WaypointConvert {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$ConvertInputPath,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$CklOutputPath,

		[Parameter()]
		[AllowNull()]
		[string]$BenchmarkId,

		[Parameter()]
		[AllowNull()]
		[string]$Title,

		[Parameter()]
		[AllowNull()]
		[string]$ReleaseInfo,

		[Parameter()]
		[AllowNull()]
		[string]$Version,

		[Parameter()]
		[int]$TimeoutSeconds = 300,

		[Parameter()]
		[string]$VmwareStigDockerCommonPath = $Script:VmwareStigDockerCommonModulePath
	)

	if ([string]::IsNullOrWhiteSpace($VmwareStigDockerCommonPath)) {
		throw 'WaypointScan: no module.common.ps1 path configured (WAYPOINT_VMWARE_STIG_DOCKER_COMMON_PATH or -VmwareStigDockerCommonPath).'
	}

	if (-not (Test-Path -Path $VmwareStigDockerCommonPath -PathType Leaf)) {
		throw "WaypointScan: module.common.ps1 not found at '$VmwareStigDockerCommonPath'."
	}

	. $VmwareStigDockerCommonPath

	$CklDirectory = Split-Path -Path $CklOutputPath -Parent
	if ($CklDirectory -and -not (Test-Path -Path $CklDirectory -PathType Container)) {
		New-Item -ItemType Directory -Path $CklDirectory -Force | Out-Null
	}

	try {
		# SafConvertArgs shape mirrors New-CklConvertArgs's `convert hdf2ckl -i <input>
		# -o <output>` (host-metadata flags --hostname/--fqdn/--ip/--mac are not needed
		# here -- STIG Manager identifies the asset from the CKL's STIG_INFO benchmark
		# fields this function stamps below, not from host metadata).
		$null = Invoke-ExternalCommand -Executable 'saf' -Arguments "convert hdf2ckl -i `"$ConvertInputPath`" -o `"$CklOutputPath`"" `
			-TimeoutMilliseconds ($TimeoutSeconds * 1000) -ProcessName 'SAF conversion' `
			-AllowedExitCodes @(0) -Source 'vsphere' -SurfaceOutputOnFailure

		if (-not (Test-Path -Path $CklOutputPath -PathType Leaf)) {
			return [pscustomobject]@{
				Success         = $false
				CklPath         = $null
				MetadataApplied = $false
				FailureReason   = "SAF conversion completed but CKL file not found at $CklOutputPath."
			}
		}
	} catch {
		return [pscustomobject]@{
			Success         = $false
			CklPath         = $null
			MetadataApplied = $false
			FailureReason   = "SAF conversion failed: $($_.Exception.Message)"
		}
	}

	# Non-fatal: a correction failure is swallowed (see doc comment above) -- the raw
	# CKL at $CklOutputPath is already valid and uploadable.
	$MetadataApplied = $false
	if ($BenchmarkId -or $Title -or $ReleaseInfo -or $Version) {
		try {
			Set-WaypointCklBenchmarkMetadata -CklPath $CklOutputPath -BenchmarkId $BenchmarkId -Title $Title -ReleaseInfo $ReleaseInfo -Version $Version
			$MetadataApplied = $true
		} catch {
		}
	}

	return [pscustomobject]@{
		Success         = $true
		CklPath         = $CklOutputPath
		MetadataApplied = $MetadataApplied
		FailureReason   = $null
	}
}

# Rewrites a CKL's STIG_INFO SID_DATA header fields in place (Waypoint-owned, static
# identity only -- see Invoke-WaypointConvert's doc comment). A $null/empty value
# leaves that field untouched.
function Set-WaypointCklBenchmarkMetadata {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[string]$CklPath,

		[Parameter()]
		[AllowNull()]
		[string]$BenchmarkId,

		[Parameter()]
		[AllowNull()]
		[string]$Title,

		[Parameter()]
		[AllowNull()]
		[string]$ReleaseInfo,

		[Parameter()]
		[AllowNull()]
		[string]$Version
	)

	[xml]$Xml = Get-Content -Path $CklPath -Raw

	$SiFields = @{
		stigid      = $BenchmarkId
		title       = $Title
		releaseinfo = $ReleaseInfo
		version     = $Version
	}

	foreach ($iStig in @($Xml.SelectNodes('//iSTIG'))) {
		foreach ($SiData in @($iStig.SelectNodes('STIG_INFO/SI_DATA'))) {
			$NameNode = $SiData.SelectSingleNode('SID_NAME')
			if ($null -eq $NameNode) { continue }
			$Key = $NameNode.InnerText
			if (-not $SiFields.ContainsKey($Key)) { continue }
			$Value = $SiFields[$Key]
			if ([string]::IsNullOrEmpty($Value)) { continue }

			$DataNode = $SiData.SelectSingleNode('SID_DATA')
			if ($null -eq $DataNode) {
				$DataNode = $SiData.OwnerDocument.CreateElement('SID_DATA')
				$SiData.AppendChild($DataNode) | Out-Null
			}
			$DataNode.InnerText = [string]$Value
		}
	}

	$Xml.Save($CklPath)
}

# A minimal Waypoint-owned logging shim so the sibling repo's unmodified
# Get-NsxSessionToken (module.transport.nsxapi.ps1) runs as-is when dot-sourced below. That function's
# only dependencies beyond Invoke-WebRequest are Get-LogSplat (returns a splat hashtable)
# and one Write-Log Debug line; the sibling repo defines both in module.logging.ps1, which
# in turn pulls in that repo's whole parallel-engine logging stack (LogQueue thread,
# Write-LogDirect, Format-LogLine) -- machinery this single-target invocation neither has
# nor needs. Rather than copy the sibling repo's function body (its Invoke-WebRequest call,
# header loop, throw strings, and JSESSIONID regex are the sibling repo's expressive code,
# and that repo carries no LICENSE -- CLAUDE.md's Borrowing Policy bars unlicensed code),
# Waypoint provides these two tiny, generic helpers so the function itself can be
# dot-sourced unmodified as project-owned sibling-repository code (the #298 shim pattern, exactly as
# module.common.ps1 is already dot-sourced for Invoke-ExternalCommand). These shims are
# only defined if the dot-sourced sibling-repository code has not already brought its own into scope,
# so if a future common.ps1 provides the real ones they win.
if (-not (Get-Command -Name 'Get-LogSplat' -ErrorAction SilentlyContinue)) {
	function Get-LogSplat {
		[CmdletBinding()]
		param([Parameter(Position = 0)][AllowNull()][AllowEmptyString()][string]$Source)
		if ($Source) { return @{ Source = $Source } }
		return @{}
	}
}
if (-not (Get-Command -Name 'Write-Log' -ErrorAction SilentlyContinue)) {
	# No-op-to-the-runspace sink: routes the sibling function's Debug line to Write-Verbose (never
	# a stream Waypoint watches/persists, never the token). Get-NsxSessionToken
	# never logs the token or credential -- only "Obtained NSX session token for <manager>".
	function Write-Log {
		[CmdletBinding()]
		param(
			[Parameter(Mandatory, Position = 0)][string]$Message,
			[Parameter()][string]$Severity = 'Info',
			[Parameter()][object]$LogQueue = $null,
			[Parameter()][string]$Source,
			[Parameter()][datetime]$Timestamp = (Get-Date)
		)
		Write-Verbose $Message
	}
}

# NSX transport (issue #308, first sub-issue of the #24 split). NSX InSpec profiles run
# with the `local` transport and make NSX Manager REST calls via the InSpec http()
# resource, authenticated with an X-XSRF-TOKEN header and a JSESSIONID cookie. The session
# token is obtained by the sibling repo's unmodified Get-NsxSessionToken (module.transport.nsxapi.ps1),
# dot-sourced at runtime from WAYPOINT_VMWARE_STIG_DOCKER_NSXAPI_PATH the same way
# module.common.ps1 is dot-sourced for Invoke-ExternalCommand (the #298 shim pattern) --
# no function body from that sibling-repository file is duplicated here; the two Get-LogSplat/Write-Log
# helpers it needs are provided as generic Waypoint shims above.
#
# The session token and cookie are secret material for as long as they are valid (they
# grant NSX Manager API access) -- they are held only in local variables for the
# lifetime of this function call, are never written to $ReportPath or any other file,
# and are passed to `inspec exec` via a generated inputs file (created 0600, owner-only,
# so the token is never world-readable during its on-disk window -- issue #304's pattern
# for this file) that lives under the artifact store's own directory (not a watched/logged
# path) exactly as the base vSphere path passes the vCenter password via environment
# variables rather than argv: neither ever appears in the captured process command line.
# On any throw the caught exception message is reduced by Get-NsxAuthFailureReason before
# being returned, so a session-token HTTP failure's exception text (which could otherwise
# echo the request body) never leaves this function un-redacted.
function Invoke-WaypointNsxScan {
	<#
	.SYNOPSIS
	    Acquires an NSX Manager session token and runs `inspec exec` (local transport)
	    against a single NSX target, returning the HDF report path plus outcome.

	.PARAMETER Manager
	    NSX Manager FQDN or IP (target.connection.host).

	.PARAMETER Username
	    NSX Manager username, decrypted credential username half.

	.PARAMETER Password
	    NSX Manager password, bound as a typed parameter -- never interpolated into
	    script text (security.md controls 1/2), matching Invoke-WaypointScan's Password.

	.PARAMETER ProfilePath
	    Path to the InSpec profile (compliance content) to execute.

	.PARAMETER ReportPath
	    Where InSpec writes its JSON (HDF) report.

	.OUTPUTS
	    One [pscustomobject]: Success (bool), ExitCode (int), ReportPath (string),
	    FailureReason (string, only set when Success is $false).
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$Manager,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$Username,

		[Parameter(Mandatory)]
		[AllowEmptyString()]
		[string]$Password,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$ProfilePath,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$ReportPath,

		[Parameter()]
		[int]$TimeoutSeconds = 1800,

		[Parameter()]
		[string]$VmwareStigDockerCommonPath = $Script:VmwareStigDockerCommonModulePath,

		[Parameter()]
		[string]$VmwareStigDockerNsxApiPath = $Script:VmwareStigDockerNsxApiModulePath
	)

	if ([string]::IsNullOrWhiteSpace($VmwareStigDockerCommonPath)) {
		throw 'WaypointScan: no module.common.ps1 path configured (WAYPOINT_VMWARE_STIG_DOCKER_COMMON_PATH or -VmwareStigDockerCommonPath).'
	}

	if (-not (Test-Path -Path $VmwareStigDockerCommonPath -PathType Leaf)) {
		throw "WaypointScan: module.common.ps1 not found at '$VmwareStigDockerCommonPath'."
	}

	if ([string]::IsNullOrWhiteSpace($VmwareStigDockerNsxApiPath)) {
		throw 'WaypointScan: no module.transport.nsxapi.ps1 path configured (WAYPOINT_VMWARE_STIG_DOCKER_NSXAPI_PATH or -VmwareStigDockerNsxApiPath).'
	}

	if (-not (Test-Path -Path $VmwareStigDockerNsxApiPath -PathType Leaf)) {
		throw "WaypointScan: module.transport.nsxapi.ps1 not found at '$VmwareStigDockerNsxApiPath'."
	}

	# Dot-source the unmodified sibling-repository scripts: module.common.ps1 brings Invoke-ExternalCommand
	# into scope (same helper the vSphere path reuses), and module.transport.nsxapi.ps1 brings
	# the sibling repo's Get-NsxSessionToken into scope unmodified (the #298 shim pattern -- see the
	# region comment above this function; the Get-LogSplat/Write-Log helpers that function
	# needs are provided as Waypoint shims above).
	. $VmwareStigDockerCommonPath
	. $VmwareStigDockerNsxApiPath

	$ReportDirectory = Split-Path -Path $ReportPath -Parent
	if ($ReportDirectory -and -not (Test-Path -Path $ReportDirectory -PathType Container)) {
		New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
	}

	try {
		# The sibling repo's Get-NsxSessionToken takes a [pscredential]; build one from the decrypted
		# username/password halves the handler bound as parameters (the password is never
		# interpolated into script text -- security.md controls 1/2 -- it goes straight into
		# the SecureString the PSCredential holds).
		$SecurePassword = ConvertTo-SecureString -String $Password -AsPlainText -Force
		$NsxCredential = [System.Management.Automation.PSCredential]::new($Username, $SecurePassword)
		$Session = Get-NsxSessionToken -Manager $Manager -Credential $NsxCredential -Source 'nsx'
	} catch {
		return [pscustomobject]@{
			Success       = $false
			ExitCode      = $null
			ReportPath    = $null
			FailureReason = "NSX session token request failed for $Manager`: $(Get-NsxAuthFailureReason -ErrorRecord $_)"
		}
	}

	# The token/cookie are written only into this generated inputs file, under the
	# artifact store's own report directory (not a watched/logged path) -- the same
	# non-argv, non-log discipline Invoke-WaypointScan's VISERVER_PASSWORD env var uses.
	# The file is best-effort deleted in `finally` below; on process crash it is left on
	# an artifact-store volume the operator already controls access to, same exposure as
	# any other generated-inputs file the sibling repo's own NSX transport writes.
	$InputsPath = Join-Path $ReportDirectory "$([Guid]::NewGuid().ToString('N')).nsx-inputs.generated.yml"
	$InputsContent = "nsxManager: '$Manager'`nsessionToken: '$($Session.Token)'`nsessionCookieId: '$($Session.Cookie)'`n"

	try {
		# Create the file 0600 (owner read/write only) BEFORE the secret is written, so the
		# session token + cookie are never world-readable during their on-disk window on the
		# shared artifact-store volume (issue #304's 0600 pattern, applied to this file --
		# the vSphere path avoids disk entirely via env vars; NSX's `local` transport needs an
		# --input-file). New-Item creates it empty; File.SetUnixFileMode narrows the mode on
		# Linux before Set-Content fills it. The `finally` deletion below still removes it once
		# the invocation completes.
		New-Item -ItemType File -Path $InputsPath -Force -ErrorAction Stop | Out-Null
		if (-not $IsWindows) {
			[System.IO.File]::SetUnixFileMode(
				$InputsPath,
				[System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite)
		}
		Set-Content -Path $InputsPath -Value $InputsContent -ErrorAction Stop

		$InspecArguments = "`"$ProfilePath`" -t local --input-file `"$InputsPath`" --reporter=json:`"$ReportPath`" --show-progress --enhanced-outcomes"

		# AllowedExitCodes 0/100/101, same predecessor constraint as the vSphere path:
		# InSpec exit 100 (compliance failures present) and 101 (skipped controls
		# present) are both a completed, reportable scan, not a tool failure.
		$null = Invoke-ExternalCommand -Executable 'inspec' -Arguments "exec $InspecArguments" `
			-TimeoutMilliseconds ($TimeoutSeconds * 1000) -ProcessName "InSpec NSX scan for $Manager" `
			-AllowedExitCodes @(0, 100, 101) -Source 'nsx' -SurfaceOutputOnFailure

		if (-not (Test-Path -Path $ReportPath -PathType Leaf)) {
			return [pscustomobject]@{
				Success       = $false
				ExitCode      = $null
				ReportPath    = $null
				FailureReason = "InSpec NSX scan completed but report file not found at $ReportPath."
			}
		}

		return [pscustomobject]@{
			Success       = $true
			ExitCode      = 0
			ReportPath    = $ReportPath
			FailureReason = $null
		}
	} catch {
		return [pscustomobject]@{
			Success       = $false
			ExitCode      = $null
			ReportPath    = $null
			FailureReason = "InSpec NSX scan failed for $Manager`: $($_.Exception.Message)"
		}
	} finally {
		if (Test-Path -Path $InputsPath -PathType Leaf) {
			Remove-Item -Path $InputsPath -Force -ErrorAction SilentlyContinue
		}
	}
}

# Reduces an ErrorRecord from the sibling repo's Get-NsxSessionToken to a short, safe-to-log
# reason: Invoke-WebRequest's own exception message for a non-2xx response
# (Microsoft.PowerShell.Commands.HttpResponseException) already includes the response
# status line ("401 Unauthorized" etc, which is what AuthFailureClassifier needs to see)
# without echoing the request body -- j_username/j_password never appear in that
# message because they were sent as a POST body, not a query string, so no further
# scrubbing is needed here; this exists to give the returned FailureReason one
# consistent, short shape regardless of exception type (timeout vs HTTP error vs DNS).
function Get-NsxAuthFailureReason {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		$ErrorRecord
	)

	return $ErrorRecord.Exception.Message
}

# SRG (ssh transport) scan (issue #309, second sub-issue of the #24 split). SRG
# products (Aria Operations/Automation/Lifecycle, vIDM, Photon) have no published DISA
# STIG -- module.transport.ssh.ps1's own doc comment: "per the VMware repo's own
# guidance they are NOT treated as STIGs: each scan produces HDF JSON only ... with NO
# CKL and NO STIG Manager upload." This function re-drives `inspec exec -t
# ssh://<user>@<host>` directly, the same single-target re-drive Invoke-WaypointScan
# already does for vmware:// -- the sibling repo's Build-SshTransportTargets/
# Get-ScanScriptBlock machinery is coupled to its whole-site parallel engine and
# catalog, which this single-target invocation does not need (same rationale as
# Invoke-WaypointScan's doc comment above). Only module.common.ps1 is dot-sourced --
# for Invoke-ExternalCommand (same as the vSphere path) AND New-InspecSecretConfigFile,
# which is how the sibling repo's own SRG branch (module.scan.ps1's Kind -eq 'srg' case) keeps
# the ssh password and optional sudo password off InSpec's argv (issue #142): both go
# into a per-invocation --config JSON file, written 0600, removed in `finally`.
#
# Predecessor behavior carried forward (module.transport.ssh.ps1's Build-SshTransportTargets):
# a stale inspec.lock under the profile directory pins absolute paths from a different
# mount and breaks the wrapper profile's dependency resolution, so any existing lock is
# removed before `inspec exec` runs, letting InSpec re-resolve fresh every time.
function Invoke-WaypointSrgScan {
	<#
	.SYNOPSIS
	    Runs `inspec exec` over ssh against a single SRG (Photon/Aria/vIDM) target and
	    returns the HDF report path plus outcome.

	.PARAMETER SshHost
	    FQDN or IP of the SRG target (target.connection.host).

	.PARAMETER Username
	    ssh username, decrypted credential username half.

	.PARAMETER Password
	    ssh password, bound as a typed parameter -- never interpolated into script text
	    (security.md controls 1/2) -- and reused as the sudo password when both Sudo and
	    SudoRequiresPassword are set, matching the sibling repo's own SRG credential shape (one
	    resolved credential covers both the ssh login and sudo elevation).

	.PARAMETER ProfilePath
	    Path to the InSpec wrapper profile (compliance content) to execute.

	.PARAMETER ReportPath
	    Where InSpec writes its JSON (HDF) report.

	.PARAMETER Sudo
	    Whether to run InSpec's `--sudo` (issue #309 AC "sudo_enabled honored"; sourced
	    from the resolved credential's typed SudoEnabled field, #249).

	.PARAMETER SudoRequiresPassword
	    Whether sudo needs the ssh password supplied via --config (Photon's default sudo
	    is passwordless; vIDM requires a sudo password) -- ignored when Sudo is $false.

	.OUTPUTS
	    One [pscustomobject]: Success (bool), ExitCode (int), ReportPath (string),
	    FailureReason (string, only set when Success is $false).
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$SshHost,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$Username,

		[Parameter(Mandatory)]
		[AllowEmptyString()]
		[string]$Password,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$ProfilePath,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$ReportPath,

		[Parameter()]
		[int]$TimeoutSeconds = 1800,

		[Parameter()]
		[bool]$Sudo = $false,

		[Parameter()]
		[bool]$SudoRequiresPassword = $true,

		[Parameter()]
		[string]$VmwareStigDockerCommonPath = $Script:VmwareStigDockerCommonModulePath
	)

	if ([string]::IsNullOrWhiteSpace($VmwareStigDockerCommonPath)) {
		throw 'WaypointScan: no module.common.ps1 path configured (WAYPOINT_VMWARE_STIG_DOCKER_COMMON_PATH or -VmwareStigDockerCommonPath).'
	}

	if (-not (Test-Path -Path $VmwareStigDockerCommonPath -PathType Leaf)) {
		throw "WaypointScan: module.common.ps1 not found at '$VmwareStigDockerCommonPath'."
	}

	# Dot-source the unmodified sibling-repository script to bring Invoke-ExternalCommand and
	# New-InspecSecretConfigFile into scope.
	. $VmwareStigDockerCommonPath

	$ReportDirectory = Split-Path -Path $ReportPath -Parent
	if ($ReportDirectory -and -not (Test-Path -Path $ReportDirectory -PathType Container)) {
		New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
	}

	# Predecessor behavior (module.transport.ssh.ps1's Build-SshTransportTargets): remove
	# any stale inspec.lock next to the profile before running -- a lock written under a
	# different mount pins absolute paths that don't exist here and breaks the wrapper
	# profile's dependency resolution.
	Remove-Item -Path (Join-Path $ProfilePath 'inspec.lock') -Force -ErrorAction SilentlyContinue

	$InspecArguments = "`"$ProfilePath`" -t ssh://$Username@$SshHost --reporter=json:`"$ReportPath`" --show-progress --enhanced-outcomes"

	$SudoPassword = $null
	if ($Sudo) {
		$InspecArguments += " --sudo"
		if ($SudoRequiresPassword) { $SudoPassword = $Password }
	}

	# Same non-argv discipline as the sibling repo's own SRG branch (module.scan.ps1): the ssh
	# password (and sudo password, when applicable) go into a per-invocation --config
	# JSON file -- created 0600 before the secret is written -- never into `inspec`'s
	# argv or a log line.
	$SecretConfigFile = New-InspecSecretConfigFile -Password $Password -SudoPassword $SudoPassword
	$InspecArguments += " --config `"$SecretConfigFile`""

	try {
		# AllowedExitCodes 0/100/101, same predecessor constraint as the vSphere/NSX
		# paths: InSpec exit 100 (compliance failures present) and 101 (skipped controls
		# present) are both a completed, reportable scan, not a tool failure.
		$null = Invoke-ExternalCommand -Executable 'inspec' -Arguments "exec $InspecArguments" `
			-TimeoutMilliseconds ($TimeoutSeconds * 1000) -ProcessName "InSpec SRG scan for $SshHost" `
			-AllowedExitCodes @(0, 100, 101) -Source 'srg' -SurfaceOutputOnFailure

		if (-not (Test-Path -Path $ReportPath -PathType Leaf)) {
			return [pscustomobject]@{
				Success       = $false
				ExitCode      = $null
				ReportPath    = $null
				FailureReason = "InSpec SRG scan completed but report file not found at $ReportPath."
			}
		}

		return [pscustomobject]@{
			Success       = $true
			ExitCode      = 0
			ReportPath    = $ReportPath
			FailureReason = $null
		}
	} catch {
		return [pscustomobject]@{
			Success       = $false
			ExitCode      = $null
			ReportPath    = $null
			FailureReason = "InSpec SRG scan failed for $SshHost`: $($_.Exception.Message)"
		}
	} finally {
		if (Test-Path -Path $SecretConfigFile -PathType Leaf) {
			Remove-Item -Path $SecretConfigFile -Force -ErrorAction SilentlyContinue
		}
	}
}

Export-ModuleMember -Function Invoke-WaypointScan, Invoke-WaypointAttest, Invoke-WaypointConvert, Invoke-WaypointNsxScan, Invoke-WaypointSrgScan
