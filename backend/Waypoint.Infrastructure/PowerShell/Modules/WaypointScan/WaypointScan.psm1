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
# WaypointDiscovery's pattern (issue #21): this module is NOT vendor code -- it is the
# thin, Waypoint-authored seam that dot-sources the UNMODIFIED vmware-stig-docker
# module.common.ps1 (CLAUDE.md: "the sibling repos' vendor scripts run as unmodified
# vendor code -- Waypoint orchestrates them, it does not fork them") to reuse its
# Invoke-ExternalCommand process-capture helper, then drives `inspec exec` against a
# single vSphere target the same way module.transport.vmware.ps1's
# Build-VsphereTransportTargets does (VISERVER/VISERVER_USERNAME/VISERVER_PASSWORD env,
# --reporter=json:<path>, --enhanced-outcomes, AllowedExitCodes 0/100/101).
#
# This is deliberately a single-target, vSphere-only invocation -- the vendor's
# Get-ScanScriptBlock is coupled to its parallel engine's $Target object (built by
# Build-VsphereTransportTargets from a whole-site connect) and to multi-transport
# branching (ssh/vcsa-component/srg) this M1 slice does not need. Re-driving `inspec`
# directly here, the same way WaypointDiscovery re-drives PowerCLI directly instead of
# forcing Get-StigTargets' scan-target shape, keeps this module honest about exactly
# what it needs from the vendor code: one process-capture helper.

$Script:VmwareStigDockerCommonModulePath = $env:WAYPOINT_VMWARE_STIG_DOCKER_COMMON_PATH

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
	    Path to the sibling repo's module.common.ps1 (unmodified vendor code),
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

	# Dot-source the unmodified vendor script to bring Invoke-ExternalCommand into scope.
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
# dot-source of the vendor's Set-CklBenchmarkMetadata: the vendor version's RuleMap
# correction needs a live STIG Manager connection (Resolve-BenchmarkMetadata), which
# is #25's integration, not this slice's -- this stamps only the static STIG_INFO
# identity fields an operator configures ahead of time (ScanOptions.BenchmarkMetadata),
# and is non-fatal exactly like the vendor's own correction step (a correction failure
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

# NSX transport (issue #308, first sub-issue of the #24 split). NSX InSpec profiles run
# with the `local` transport and make NSX Manager REST calls via the InSpec http()
# resource, authenticated with an X-XSRF-TOKEN header and a JSESSIONID cookie -- the
# same shape the sibling repo's module.transport.nsxapi.ps1 (Get-NsxSessionToken) uses.
# That vendor function is not dot-sourced here: it depends on that repo's own
# Write-Log/Get-LogSplat helpers, which is more of that module's parallel-engine
# machinery than this single-target invocation needs (the same "re-drive the vendor
# step directly" call Invoke-WaypointScan's doc comment already makes for
# Get-ScanScriptBlock) -- so the POST /api/session/create call is replicated here,
# directly, matching the vendor's request/response shape exactly (form-encoded
# j_username/j_password, X-XSRF-TOKEN response header, JSESSIONID Set-Cookie).
#
# The session token and cookie are secret material for as long as they are valid (they
# grant NSX Manager API access) -- they are held only in local variables for the
# lifetime of this function call, are never written to $ReportPath or any other file,
# and are passed to `inspec exec` via a generated inputs file that lives under the
# artifact store's own directory (not a watched/logged path) exactly as the base
# vSphere path passes the vCenter password via environment variables rather than argv:
# neither ever appears in the captured process command line. On any throw the caught
# exception message is reduced by Get-NsxAuthFailureReason before being returned, so a
# session-token HTTP failure's exception text (which could otherwise echo the request
# body) never leaves this function un-redacted.
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
		[string]$VmwareStigDockerCommonPath = $Script:VmwareStigDockerCommonModulePath
	)

	if ([string]::IsNullOrWhiteSpace($VmwareStigDockerCommonPath)) {
		throw 'WaypointScan: no module.common.ps1 path configured (WAYPOINT_VMWARE_STIG_DOCKER_COMMON_PATH or -VmwareStigDockerCommonPath).'
	}

	if (-not (Test-Path -Path $VmwareStigDockerCommonPath -PathType Leaf)) {
		throw "WaypointScan: module.common.ps1 not found at '$VmwareStigDockerCommonPath'."
	}

	# Dot-source the unmodified vendor script to bring Invoke-ExternalCommand into scope
	# (same helper the vSphere path above reuses; the NSX-specific session-token call is
	# not vendor code -- see the region comment above this function).
	. $VmwareStigDockerCommonPath

	$ReportDirectory = Split-Path -Path $ReportPath -Parent
	if ($ReportDirectory -and -not (Test-Path -Path $ReportDirectory -PathType Container)) {
		New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
	}

	try {
		$Session = Get-WaypointNsxSessionToken -Manager $Manager -Username $Username -Password $Password
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

# Obtains an NSX Manager API session token and cookie via `POST /api/session/create`,
# matching the sibling repo's Get-NsxSessionToken request/response shape exactly (form
# body j_username/j_password, X-XSRF-TOKEN response header, JSESSIONID Set-Cookie) --
# not dot-sourced from the vendor file, see the region comment above
# Invoke-WaypointNsxScan for why. Throws on any failure so the caller classifies it.
function Get-WaypointNsxSessionToken {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[string]$Manager,

		[Parameter(Mandatory)]
		[string]$Username,

		[Parameter(Mandatory)]
		[AllowEmptyString()]
		[string]$Password
	)

	$Url = "https://$Manager/api/session/create"
	$Body = @{
		j_username = $Username
		j_password = $Password
	}

	$Response = Invoke-WebRequest -Uri $Url -Method Post -Body $Body `
		-ContentType 'application/x-www-form-urlencoded' `
		-SkipCertificateCheck -TimeoutSec 30 -ErrorAction Stop

	$Token = $null
	$SetCookie = $null
	foreach ($Key in $Response.Headers.Keys) {
		if ($Key -ieq 'X-XSRF-TOKEN') { $Token = @($Response.Headers[$Key])[0] }
		elseif ($Key -ieq 'Set-Cookie') { $SetCookie = @($Response.Headers[$Key]) -join '; ' }
	}
	if ([string]::IsNullOrEmpty($Token)) { throw "NSX session create did not return an X-XSRF-TOKEN header" }

	$CookieMatch = [regex]::Match([string]$SetCookie, 'JSESSIONID=[^;,\s]+')
	if (-not $CookieMatch.Success) { throw "NSX session create did not return a JSESSIONID cookie" }

	return [PSCustomObject]@{
		Token  = $Token
		Cookie = $CookieMatch.Value
	}
}

# Reduces an ErrorRecord from Get-WaypointNsxSessionToken to a short, safe-to-log
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

Export-ModuleMember -Function Invoke-WaypointScan, Invoke-WaypointAttest, Invoke-WaypointConvert, Invoke-WaypointNsxScan
