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

	.PARAMETER SelectorKind
	    Issue #737 item-4: when set (vcenter|esxi|vm), narrows the scan to one component
	    object rather than the whole vCenter. Absent/empty = whole-target scan (the
	    pre-#737 behavior, preserved verbatim for legacy jobs and the collapsed
	    whole-target remainder job).

	.PARAMETER SelectorName
	    The identity a narrowed esxi/vm scan scopes to -- the HOST/VM NAME the target
	    profile matches on (issue #1123: the discovered component's DisplayName, not
	    its VendorIdentity/MoRef -- the profile's InSpec resource matches by name, not
	    by vCenter managed-object reference). A vcenter SelectorKind needs no name (the
	    whole vCenter IS the object). Passed to InSpec as a generated --input-file (the
	    same non-argv, owner-only-0600 discipline Invoke-WaypointNsxScan uses for its
	    session token), so the object identity never lands on the process command
	    line, under the key name the resolved frozen profile itself declares (issue
	    #1123, Get-WaypointVsphereProfileSelectorInputKeySet) -- never a hardcoded 8.0
	    or VCF 9.x name.

	.PARAMETER VmwareStigDockerCommonPath
	    Path to the sibling repo's unmodified module.common.ps1,
	    dot-sourced to bring Invoke-ExternalCommand into scope.

	.PARAMETER InputsFilePath
	    Issue #738, generalized to esxi/vm by #739/#740: an already-materialized InSpec
	    inputs YAML file (the vcenter/esxi/vm component item's frozen, resolved
	    config-doc Inputs, already filtered of #911's reserved scoping keys --
	    ScanJobHandler writes this file BEFORE calling in, owner-only 0600, and deletes
	    it after). Passed to InSpec as its OWN --input-file flag, alongside (never
	    merged with) the SelectorKind/SelectorName scoping file below -- InSpec accepts
	    multiple --input-file flags on one invocation. Issue #911: appended BEFORE the
	    selector-scoping file so the platform's own scope always wins InSpec's
	    last-`--input-file`-key-wins resolution on any collision. Absent/empty = no
	    additional resolved inputs (the pre-#738 behavior for every non-component job).

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
		[ValidateSet('vcenter', 'esxi', 'vm')]
		[string]$SelectorKind,

		[Parameter()]
		[string]$SelectorName,

		[Parameter()]
		[string]$InputsFilePath,

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

	# Issue #738: the vCenter/esxi/vm component item's already-materialized
	# resolved-Input file, passed as its own --input-file flag -- ScanJobHandler owns
	# this file's entire lifecycle (creation, filtering of #911's reserved scoping keys,
	# 0600 mode, deletion); this function only reads its path and never writes/deletes
	# it, unlike $InputsPath below.
	#
	# Issue #911: this flag is appended BEFORE the platform selector-scoping flag below
	# (was AFTER, pre-#911 -- InSpec's last-`--input-file`-key-wins semantics meant the
	# operator-authored config-doc body silently won a key collision). Ordering the
	# operator file FIRST and the platform scoping file LAST means the platform's own
	# vsphereSelectorKind/vmhostName/vmName always wins InSpec's last-file-wins
	# resolution even if a colliding key somehow reached this file -- belt and
	# suspenders alongside ScanJobHandler's own ScanScopingInputFilter drop.
	if ($InputsFilePath) {
		$InspecArguments += " --input-file `"$InputsFilePath`""
	}

	# Issue #737 item-4, key names fixed by #1123: a narrowed scan scopes InSpec to one
	# component object rather than the whole vCenter. The narrowing selector is
	# written into a generated --input-file (created 0600 BEFORE the value is
	# written, the same owner-only, non-argv discipline Invoke-WaypointNsxScan uses
	# for its session token), so the object identity never appears on the process
	# command line. Absent SelectorKind = whole-target scan (the pre-#737 InSpec args,
	# unchanged). A vcenter selector scopes to the vCenter itself and carries no
	# object name.
	#
	# Issue #1123: the esxi/vm object-scoping key name is no longer hardcoded to the
	# 8.0 STIG names (vmhostName/vmName) -- it is resolved from the RESOLVED FROZEN
	# PROFILE's own declared inputs (Get-WaypointVsphereProfileSelectorInputKeySet,
	# the same #917-derived approach Invoke-WaypointNsxScan already uses for its NSX
	# auth-input keys), because the VCF 9.x SRG baselines declare a different,
	# per-selector-kind prefixed name (esx_vmhostName / vm_Name) that the 8.0 name
	# never matches. Unlike the NSX helper, an unmatched slot here is NOT defaulted to
	# the legacy name -- epic #726's "never guess" rule means a profile declaring
	# neither known name fails this scan closed (below) rather than silently emitting
	# an unscoped or mis-scoped narrowing input, which is the exact #1123 defect.
	# Appended LAST (issue #911) so the platform's own scoping always wins InSpec's
	# last-`--input-file`-key-wins resolution over the operator inputs file above.
	$InputsPath = $null
	if ($SelectorKind) {
		$InputsContent = "vsphereSelectorKind: '$SelectorKind'`n"
		if ($SelectorName) {
			if ($SelectorKind -notin @('esxi', 'vm')) {
				throw "WaypointScan: SelectorName was supplied for SelectorKind '$SelectorKind', which carries no object-scoping input (only 'esxi'/'vm' do)."
			}

			$ResolvedKey = Get-WaypointVsphereProfileSelectorInputKeySet -ProfilePath $ProfilePath -SelectorKind $SelectorKind
			if (-not $ResolvedKey) {
				return [pscustomobject]@{
					Success       = $false
					ExitCode      = $null
					ReportPath    = $null
					FailureReason = "WaypointScan: resolved profile at '$ProfilePath' declares no recognized '$SelectorKind' object-scoping input (checked both the 8.0 STIG and VCF 9.x SRG names) -- refusing to guess a key name and silently scope this narrowed scan to nothing. Verify the frozen profile's inspec.yml declares the expected input, or file the profile's own key name as a catalog gap."
				}
			}

			$InputsContent += "${ResolvedKey}: '$SelectorName'`n"
		}

		$InputsPath = Join-Path $ReportDirectory "$([Guid]::NewGuid().ToString('N')).vsphere-scope.generated.yml"
		New-Item -ItemType File -Path $InputsPath -Force -ErrorAction Stop | Out-Null
		if (-not $IsWindows) {
			[System.IO.File]::SetUnixFileMode(
				$InputsPath,
				[System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite)
		}
		Set-Content -Path $InputsPath -Value $InputsContent -ErrorAction Stop

		$InspecArguments += " --input-file `"$InputsPath`""
	}

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
	} finally {
		if ($InputsPath -and (Test-Path -Path $InputsPath -PathType Leaf)) {
			Remove-Item -Path $InputsPath -Force -ErrorAction SilentlyContinue
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

		# Issue #744: the mapped benchmark revision's own rules (migration 0052's
		# benchmark_rules, read by ScanJobHandler via IBenchmarkRepository.ListRulesAsync
		# before this call), keyed by rule_id -> vuln_id. When supplied, every CKL Vuln
		# entry's Rule_ID/Vuln_Num STIG_DATA identity is corrected to the mapped
		# revision's exact identifiers rather than trusted as SAF emitted them --
		# $null/empty means "no frozen benchmark revision" (legacy/unmapped path):
		# rule correction is skipped entirely and RuleCoverage reports zero matched/
		# unmatched, exactly like the pre-#744 behavior.
		[Parameter()]
		[AllowNull()]
		[hashtable]$RuleCorrections,

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
				RuleCoverage    = $null
			}
		}
	} catch {
		return [pscustomobject]@{
			Success         = $false
			CklPath         = $null
			MetadataApplied = $false
			FailureReason   = "SAF conversion failed: $($_.Exception.Message)"
			RuleCoverage    = $null
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

	# Issue #744: rule-level correction is independent of (and never blocks) the
	# STIG_INFO stamp above -- a correction failure here still returns Success with the
	# raw, uploadable CKL untouched below the point of failure (never destroys the
	# artifact, AC "Preserve raw HDF/CKL when metadata correction or upload fails").
	$RuleCoverage = $null
	if ($RuleCorrections -and $RuleCorrections.Count -gt 0) {
		try {
			$RuleCoverage = Set-WaypointCklRuleIdentity -CklPath $CklOutputPath -RuleCorrections $RuleCorrections
		} catch {
			$RuleCoverage = [pscustomobject]@{
				Matched   = 0
				Unmatched = @()
				Error     = $_.Exception.Message
			}
		}
	}

	return [pscustomobject]@{
		Success         = $true
		CklPath         = $CklOutputPath
		MetadataApplied = $MetadataApplied
		FailureReason   = $null
		RuleCoverage    = $RuleCoverage
	}
}

# Issue #744 (epic #726 Wave 4 first slice): corrects each CKL Vuln entry's STIG_DATA
# rule identity (Rule_ID/Vuln_Num) from the mapped benchmark revision's own rules
# (benchmark_rules, migration 0052) rather than trusting whatever SAF's hdf2ckl
# converter emitted from the raw HDF/InSpec control metadata. Matching key is the
# CKL's OWN existing Rule_ID STIG_DATA value against $RuleCorrections' keys (the
# frozen revision's rule_id set) -- a Vuln entry whose existing Rule_ID has no entry
# in $RuleCorrections is left untouched and counted as Unmatched (issue #744 AC
# "unresolved/ambiguous rules are visible and cannot masquerade as complete"; this
# function NEVER silently drops an unmatched rule -- it reports it, it does not
# remove or blank it). Returns a coverage summary: Matched (count corrected),
# Unmatched (list of the CKL's own uncorrected Rule_ID values).
function Set-WaypointCklRuleIdentity {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[string]$CklPath,

		[Parameter(Mandatory)]
		[hashtable]$RuleCorrections
	)

	[xml]$Xml = Get-Content -Path $CklPath -Raw

	$Matched = 0
	$Unmatched = [System.Collections.Generic.List[string]]::new()

	foreach ($Vuln in @($Xml.SelectNodes('//VULN'))) {
		$StigDataNodes = @($Vuln.SelectNodes('STIG_DATA'))
		$RuleIdNode = $StigDataNodes | Where-Object {
			$_.SelectSingleNode('VULN_ATTRIBUTE').InnerText -eq 'Rule_ID'
		} | Select-Object -First 1

		if ($null -eq $RuleIdNode) {
			continue
		}

		$ExistingRuleId = $RuleIdNode.SelectSingleNode('ATTRIBUTE_DATA').InnerText
		if (-not $RuleCorrections.ContainsKey($ExistingRuleId)) {
			$Unmatched.Add($ExistingRuleId)
			continue
		}

		$CorrectVulnId = $RuleCorrections[$ExistingRuleId]
		$VulnNumNode = $StigDataNodes | Where-Object {
			$_.SelectSingleNode('VULN_ATTRIBUTE').InnerText -eq 'Vuln_Num'
		} | Select-Object -First 1

		if ($null -ne $VulnNumNode -and $CorrectVulnId) {
			$VulnNumNode.SelectSingleNode('ATTRIBUTE_DATA').InnerText = [string]$CorrectVulnId
		}

		$Matched++
	}

	$Xml.Save($CklPath)

	return [pscustomobject]@{
		Matched   = $Matched
		Unmatched = @($Unmatched)
		Error     = $null
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

# Get-LogSplat/Write-Log for the sibling repo's unmodified Get-NsxSessionToken
# (module.transport.nsxapi.ps1, dot-sourced below) are provided by the shared
# WaypointLogging adapter module (issue #579), preloaded into every compliance
# runspace ahead of any imported transport module -- see
# Modules/WaypointLogging/WaypointLogging.psm1 for the full rationale. This module
# no longer carries its own stand-ins.

# NSX transport (issue #308, first sub-issue of the #24 split). NSX InSpec profiles run
# with the `local` transport and make NSX Manager REST calls via the InSpec http()
# resource, authenticated with an X-XSRF-TOKEN header and a JSESSIONID cookie. The session
# token is obtained by the sibling repo's unmodified Get-NsxSessionToken (module.transport.nsxapi.ps1),
# dot-sourced at runtime from WAYPOINT_VMWARE_STIG_DOCKER_NSXAPI_PATH the same way
# module.common.ps1 is dot-sourced for Invoke-ExternalCommand (the #298 shim pattern) --
# no function body from that sibling-repository file is duplicated here; the two Get-LogSplat/Write-Log
# helpers it needs are provided by the shared WaypointLogging adapter (issue #579).
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
#
# Issue #742 (epic #726 Wave 3's final transport, expanding one NSX Manager entry point
# into its catalog-defined functional components -- Manager/DFW/tier-0/tier-1
# firewall/router/newer sets): session-reuse reading. The issue asks to "reuse one
# bounded session acquisition where safe while keeping job credential/lease ownership
# clear." This function is called ONCE PER COMPONENT JOB (ScanComponentNarrowing now
# narrows nsx-api/service, so RunCreationService fans out one job per NSX function),
# and each job independently decrypts its own credential and acquires its own session
# token here -- there is no cross-JOB session cache. That is a deliberate reading, not
# an oversight: ADR-0014 gives each job its own credential decrypt/lease, and a session
# token cached across job boundaries would either (a) require a shared cache keyed by
# manager+credential outside any one job's lease/cancellation scope, silently
# reintroducing cross-job coupling the runner topology was built to avoid, or (b) risk
# handing a still-valid token to a job whose credential was rotated/revoked between
# acquisitions. The session IS reused in the one place that is safe without crossing a
# job boundary: within a single job's own invocation of this function, ONE session
# token is acquired and used for that job's one InSpec `inspec exec` call against its
# one component's profile -- there is nothing else to reuse it FOR inside one job
# (unlike the sibling `vmware-stig-docker` engine's per-run session honoring N
# component targets from one token acquisition, which this runner's one-job-per-
# component model does not have an analogous multi-target loop for). If a future
# measured-performance case justifies a real cross-job cache, it needs its own ADR
# addressing lease/revocation semantics -- not a change folded into this issue.

# Issue #917/#1071 helper, generalized by #1123 (module-private): reads the TOP-LEVEL
# `inputs:` block of a resolved frozen profile's inspec.yml and returns every declared
# input name, in declaration order. Both Get-WaypointNsxProfileAuthInputKeySet (NSX
# session auth keys) and Get-WaypointVsphereProfileSelectorInputKeySet (vSphere
# esxi/vm narrowing keys, issue #1123) resolve their own known-name pairs from this
# ONE shared scan -- the structural guard against a fourth "input key names hardcoded
# to one content generation" instance (#917 NSX, #1123 vSphere, and the parsing
# follow-up #1071): any future transport that needs a profile-declared input name
# reads it from here rather than growing its own hardcoded map or its own manifest
# scan.
#
# Deliberately line-oriented rather than a full YAML parse -- the same constraint the
# sibling's Remove-NsxAuthInputKeys documents (no YAML parser module is available in
# the runner container). Input-name discovery is scoped to the manifest's TOP-LEVEL
# `inputs:` block by INDENTATION/STRUCTURE (issue #1071): a column-0 *mapping* key
# (e.g. `depends:`) or a document marker (`---`) closes the block, while a column-0
# `- name:` sequence entry is a MEMBER of it; within the block a `name:` counts only
# at the input entry's OWN key level. So `- name:` entries under `depends:` (or any
# other named list) never count as inputs, and neither does a `name:` nested inside
# an input's own `value:` mapping or `value:` sequence. A missing/unreadable
# inspec.yml simply yields an empty list -- callers decide what an empty/no-match
# result means for their own slot (NSX defaults to its 4.x legacy name; vSphere,
# issue #1123, fails closed instead -- see Get-WaypointVsphereProfileSelectorInputKeySet).
function Get-WaypointProfileDeclaredInputNameSet {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$ProfilePath
	)

	$DeclaredNames = [System.Collections.Generic.List[string]]::new()
	$ManifestPath = Join-Path $ProfilePath 'inspec.yml'
	if (Test-Path -Path $ManifestPath -PathType Leaf) {
		# Issue #1071: the block is scoped by INDENTATION/STRUCTURE, not by "next
		# column-0 line" -- a column-0 `- name:` sequence entry (every shipped NSX
		# 4.x/3.x manifest's style) is a MEMBER of the open `inputs:` block, while a
		# column-0 *mapping* key (e.g. `depends:`) or a `---` document marker closes
		# it. Within the block the FIRST sequence entry fixes the input entries' dash
		# column, and each entry's own key column follows from its dash line, so a
		# `name:` is accepted on an entry's dash line OR on any later line at that
		# entry's key column -- and never when nested deeper (inside an input's own
		# `value:` mapping or `value:` sequence). Comment-only lines (first non-space
		# char `#`, at any indentation) never affect block state.
		$InInputsBlock = $false
		$EntryDashColumn = -1
		$EntryKeyColumn = -1
		foreach ($Line in [System.IO.File]::ReadAllLines($ManifestPath)) {
			if ($Line -notmatch '^(\s*)(\S.*)$') {
				# Blank (or whitespace-only) line: never affects block state.
				continue
			}
			$Indent = $Matches[1].Length
			$Content = $Matches[2]

			if ($Content.StartsWith('#')) {
				continue
			}

			# A `-` list marker only opens a sequence entry when followed by
			# whitespace or end-of-line -- `---` (document marker) is NOT an entry.
			$IsSequenceEntry = $Content -match '^-(\s|$)'

			if (-not $IsSequenceEntry -and $Indent -eq 0) {
				# A column-0 mapping key (or a document marker) starts a new
				# top-level scope; only `inputs:` (re)opens the discovery block.
				$InInputsBlock = $Content -match '^inputs\s*:'
				$EntryDashColumn = -1
				$EntryKeyColumn = -1
				continue
			}

			if (-not $InInputsBlock) {
				continue
			}

			$NameCandidate = $null
			if ($IsSequenceEntry) {
				if ($EntryDashColumn -lt 0 -or $Indent -lt $EntryDashColumn) {
					$EntryDashColumn = $Indent
				}
				if ($Indent -gt $EntryDashColumn) {
					# A sequence nested under one of this input's own keys (e.g. a
					# `value:` list of mappings) -- never an input entry.
					continue
				}
				if ($Content -match '^-(\s+)(\S.*)$') {
					# The entry's keys sit at the column of its first inline key.
					$EntryKeyColumn = $Indent + 1 + $Matches[1].Length
					$EntryBody = $Matches[2]
					if ($EntryBody -match '^name\s*:\s*(.+?)\s*$') {
						$NameCandidate = $Matches[1]
					}
				} else {
					# A bare `-`: this entry's key column is not known until its
					# first key line appears.
					$EntryKeyColumn = -1
				}
			} else {
				if ($EntryDashColumn -lt 0) {
					# A mapping key inside `inputs:` before any sequence entry --
					# not an input entry's key.
					continue
				}
				if ($EntryKeyColumn -lt 0 -and $Indent -gt $EntryDashColumn) {
					$EntryKeyColumn = $Indent
				}
				if ($Indent -ne $EntryKeyColumn) {
					# Deeper than the entry's key column (nested under one of its
					# keys), or shallower (a stray/malformed line) -- not an input
					# entry's `name:`.
					continue
				}
				if ($Content -match '^name\s*:\s*(.+?)\s*$') {
					$NameCandidate = $Matches[1]
				}
			}

			if ($null -ne $NameCandidate) {
				$Name = $NameCandidate
				if ($Name.Length -ge 2 -and ($Name[0] -eq "'" -or $Name[0] -eq '"')) {
					# Quoted name: take ONLY the quoted content. Anything after the
					# closing quote (a trailing `#...` comment, separated by any run
					# of whitespace) is discarded without ever treating a `#` INSIDE
					# the quotes as a comment introducer -- issue #1136's adjacent
					# concern, which this change does not attempt to fix but must not
					# regress further.
					$QuoteChar = $Name[0]
					$ClosingIndex = $Name.IndexOf($QuoteChar, 1)
					if ($ClosingIndex -gt 0) {
						$Name = $Name.Substring(1, $ClosingIndex - 1)
					}
				} else {
					# Strip a trailing `#...` comment (issue #1071 shape 3) separated
					# by ANY run of whitespace -- issue #1152: the previous match
					# required a literal single space immediately before `#` (`' #'`),
					# so a tab (or more than one space) before the `#` left the
					# comment text folded into the captured name, which then never
					# matched a known auth-key name.
					$CommentMatch = [regex]::Match($Name, '\s#')
					if ($CommentMatch.Success) {
						$Name = $Name.Substring(0, $CommentMatch.Index).TrimEnd()
					}
				}
				$DeclaredNames.Add($Name)
			}
		}
	}

	return $DeclaredNames
}

# Issue #917 helper (module-private, used only by Invoke-WaypointNsxScan): resolves
# which NSX auth-input key names the resolved frozen profile itself declares, from
# Get-WaypointProfileDeclaredInputNameSet's shared manifest scan. Returns the same
# { manager, token, cookie } shape the sibling transport's catalog `authInputKeys`
# map carries, with any undeclared slot left $null so the caller's per-slot 4.x
# legacy defaulting (taken verbatim from module.transport.nsxapi.ps1) applies.
function Get-WaypointNsxProfileAuthInputKeySet {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$ProfilePath
	)

	$DeclaredNames = Get-WaypointProfileDeclaredInputNameSet -ProfilePath $ProfilePath

	# Per-slot: whichever of the two known key names (4.x legacy / 9.x SRG) the
	# profile declares, in the profile's own declaration order; $null when neither is
	# declared (caller defaults that slot to the 4.x legacy name).
	$ManagerName = $DeclaredNames | Where-Object { $_ -in @('nsxManager', 'nsx_managerAddress') } | Select-Object -First 1
	$TokenName = $DeclaredNames | Where-Object { $_ -in @('sessionToken', 'nsx_sessionToken') } | Select-Object -First 1
	$CookieName = $DeclaredNames | Where-Object { $_ -in @('sessionCookieId', 'nsx_sessionCookieId') } | Select-Object -First 1

	return [pscustomobject]@{
		manager = $ManagerName
		token   = $TokenName
		cookie  = $CookieName
	}
}

# Issue #1123 helper (module-private, used only by Invoke-WaypointScan): resolves
# which vSphere object-narrowing input key name the resolved frozen profile itself
# declares for the given SelectorKind ('esxi' or 'vm'), from
# Get-WaypointProfileDeclaredInputNameSet's shared manifest scan. Unlike the NSX auth-key
# resolution above, this NEVER defaults an unmatched slot to a hardcoded name -- epic
# #726 Wave 2's "never guess" rule applies to a scoping key exactly as it does to
# object identity: a profile that declares neither the 8.0 name nor the VCF 9.x SRG
# name for this SelectorKind gets $null back, and the caller must fail the scan
# closed (a diagnosable failure) rather than silently emit an unscoped or
# wrongly-scoped narrowing input -- the exact #1123 defect (the 8.0-only key written
# against a 9.x profile silently scoped nothing).
function Get-WaypointVsphereProfileSelectorInputKeySet {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$ProfilePath,

		[Parameter(Mandatory)]
		[ValidateSet('esxi', 'vm')]
		[string]$SelectorKind
	)

	$DeclaredNames = Get-WaypointProfileDeclaredInputNameSet -ProfilePath $ProfilePath

	# Known name pairs per selector kind: 8.0 STIG baseline (esxi/vmhostName,
	# vm/vmName) and the VCF 9.x SRG baselines (esxi/esx_vmhostName, vm/vm_Name --
	# note the two content generations do NOT share a common prefix scheme between
	# selector kinds, which is exactly why this must be discovered per profile rather
	# than templated from a version conditional).
	switch ($SelectorKind) {
		'esxi' { return $DeclaredNames | Where-Object { $_ -in @('vmhostName', 'esx_vmhostName') } | Select-Object -First 1 }
		'vm'   { return $DeclaredNames | Where-Object { $_ -in @('vmName', 'vm_Name') } | Select-Object -First 1 }
	}
}

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

	.PARAMETER SelectorName
	    Issue #742: the catalog's own named-function identity for this NSX component
	    (e.g. 'manager', 'dfw', 'tier0-fw') -- there is no separate discovered vendor
	    identity per NSX function, the catalog name IS the stable identity (same
	    convention as Invoke-WaypointSrgScan's ssh `service` selector). Used only for
	    logging/diagnostics here (ProcessName) -- the actual component/profile scoping
	    already happened one layer up: ProfilePath IS this component's own resolved
	    leaf profile (ComponentProfileRevisionResolver), so nothing about the NSX
	    Manager API call itself needs to change per component; unlike the vmware
	    transport, there is no whole-Manager-vs-one-function InSpec input to narrow.

	.PARAMETER InputsFilePath
	    Issue #742: an already-materialized InSpec inputs YAML file (the nsx-api
	    component item's frozen, resolved config-doc Inputs, already filtered of
	    ScanScopingInputFilter's reserved keys -- including the NSX auth-input key
	    names below). ScanJobHandler writes this file BEFORE calling in, owner-only
	    0600, and deletes it after -- same non-argv discipline as
	    Invoke-WaypointScan/Invoke-WaypointSrgScan's own InputsFilePath. Passed to
	    InSpec as its own --input-file flag, appended BEFORE the runner's own
	    auth-block file (below) -- so even an operator value that collided with a
	    reserved auth-input key AND somehow survived the C#-side filter would still
	    lose to the runner's real session on InSpec's last-file-wins semantics (belt
	    and suspenders, matching the vmware/ssh families' own ordering discipline).
	    Absent/empty = no additional resolved inputs (the pre-#742 behavior).

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
		[string]$SelectorName,

		[Parameter()]
		[string]$InputsFilePath,

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
	# the sibling repo's Get-NsxSessionToken into scope unmodified (the #298 shim pattern); the
	# Get-LogSplat/Write-Log helpers that function needs are provided by the shared
	# WaypointLogging adapter module (issue #579), preloaded ahead of this module.
	. $VmwareStigDockerCommonPath
	. $VmwareStigDockerNsxApiPath

	$ReportDirectory = Split-Path -Path $ReportPath -Parent
	if ($ReportDirectory -and -not (Test-Path -Path $ReportDirectory -PathType Container)) {
		New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
	}

	# Issue #917, adopting the sibling nsxapi transport's own precheck (its issue #295;
	# the same wholesale Test-TargetReachable reuse Invoke-WaypointSrgScan makes for ssh
	# port 22): a cheap bounded TCP probe on 443 BEFORE the session-token HTTP call, so
	# an unreachable/hung NSX Manager fails fast with a classified reachability reason
	# instead of surfacing as an opaque session-token failure after that call's own
	# timeout -- unreachable-vs-auth-failure must classify crisply. Probe-only, never a
	# mutation. Test-TargetReachable comes from the already-dot-sourced module.common.ps1.
	if (-not (Test-TargetReachable -TargetHost $Manager -Port 443 -Source 'nsx')) {
		return [pscustomobject]@{
			Success       = $false
			ExitCode      = $null
			ReportPath    = $null
			FailureReason = "NSX Manager $Manager is not reachable on port 443 within the connect timeout; the manager may be down, blocked, or unresolvable -- restore HTTPS access to the manager and retry."
		}
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

	# Issue #917: auth input key names are baseline-specific. NSX 4.x profiles read
	# nsxManager/sessionToken/sessionCookieId; the VCF 9.x NSX SRG profiles
	# (products.nsx.9-x -- docs/compliance-parity.md's `NSX 9-x` row) read
	# nsx_managerAddress/nsx_sessionToken/nsx_sessionCookieId instead. The sibling
	# transport (module.transport.nsxapi.ps1) resolves the names from its catalog
	# kind's optional `authInputKeys` map; Waypoint's catalog carries no such signal,
	# so the equivalent authority here is the RESOLVED FROZEN PROFILE itself:
	# ProfilePath IS this component's own activated leaf profile
	# (ComponentProfileRevisionResolver), and its inspec.yml declares exactly which
	# auth-input names the profile reads -- emitting what the profile declares also
	# makes any future upstream key-name unification a no-op here. The per-slot
	# defaulting below is the sibling's resolution verbatim: any slot the profile does
	# not declare falls back to the 4.x legacy name, so a legacy job shape against a
	# bare/manifest-less profile keeps the pre-#917 auth block byte-identical. Both key
	# sets stay reserved on the drop-and-warn side (ScanScopingInputFilter
	# .ReservedNsxAuthKeys) regardless of which set is emitted.
	$AuthKeys = Get-WaypointNsxProfileAuthInputKeySet -ProfilePath $ProfilePath
	$ManagerKey = if ($AuthKeys -and $AuthKeys.manager) { $AuthKeys.manager } else { 'nsxManager' }
	$TokenKey = if ($AuthKeys -and $AuthKeys.token) { $AuthKeys.token } else { 'sessionToken' }
	$CookieKey = if ($AuthKeys -and $AuthKeys.cookie) { $AuthKeys.cookie } else { 'sessionCookieId' }
	$InputsContent = "${ManagerKey}: '$Manager'`n${TokenKey}: '$($Session.Token)'`n${CookieKey}: '$($Session.Cookie)'`n"

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

		$InspecArguments = "`"$ProfilePath`" -t local"

		# Issue #742: the nsx-api component item's already-materialized resolved Input
		# config docs, passed as their OWN --input-file flag -- ScanJobHandler owns this
		# file's entire lifecycle (creation, ScanScopingInputFilter filtering including
		# the NSX auth-input key names, 0600 mode, deletion); this function only reads
		# its path and never writes/deletes it, mirroring Invoke-WaypointScan/
		# Invoke-WaypointSrgScan's own InputsFilePath handling. Appended BEFORE the
		# runner's own auth-block file below -- so the runner's real session always wins
		# InSpec's last-`--input-file`-key-wins resolution over any operator value, even
		# one that collided with a reserved auth key and somehow survived the C#-side
		# filter (belt and suspenders, same ordering discipline the vmware/ssh families
		# use for their own platform-computed files).
		if ($InputsFilePath) {
			$InspecArguments += " --input-file `"$InputsFilePath`""
		}

		$InspecArguments += " --input-file `"$InputsPath`" --reporter=json:`"$ReportPath`" --show-progress --enhanced-outcomes"

		$ProcessLabel = if ($SelectorName) { "InSpec NSX scan for $Manager/$SelectorName" } else { "InSpec NSX scan for $Manager" }

		# AllowedExitCodes 0/100/101, same predecessor constraint as the vSphere path:
		# InSpec exit 100 (compliance failures present) and 101 (skipped controls
		# present) are both a completed, reportable scan, not a tool failure.
		$null = Invoke-ExternalCommand -Executable 'inspec' -Arguments "exec $InspecArguments" `
			-TimeoutMilliseconds ($TimeoutSeconds * 1000) -ProcessName $ProcessLabel `
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

	.PARAMETER InputsFilePath
	    Issue #741/#743: an already-materialized InSpec inputs YAML file (a narrowed
	    ssh-transport item's -- VCSA service or whole-appliance SSH product -- frozen,
	    resolved config-doc Inputs). ScanJobHandler writes this file BEFORE calling in,
	    owner-only 0600, and deletes it after -- same non-argv discipline as
	    Invoke-WaypointScan's own InputsFilePath. Passed to InSpec as its own
	    --input-file flag. Absent/empty = no additional resolved inputs (the pre-#741
	    behavior for every SRG job).

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
		[string]$InputsFilePath,

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

	# Predecessor behavior (module.transport.ssh.ps1's Build-SshTransportTargets, its
	# issue #261), adopted wholesale via the already-dot-sourced module.common.ps1's own
	# Test-TargetReachable: a cheap bounded TCP probe on ssh port 22 BEFORE InSpec runs,
	# so a host whose SSH is disabled/blocked/unreachable fails fast with an actionable
	# reason instead of hanging to the full scan timeout. Probe-only, never a mutation:
	# Waypoint never enables SSH during a scan (issue #741 -- temporary enablement is a
	# separate, ADR-gated owner decision), so the honest outcome here is a classified
	# execution failure naming the reachability gap, the runner-native analogue of the
	# sibling engine recording the same condition via Add-ScanSkip.
	if (-not (Test-TargetReachable -TargetHost $SshHost -Port 22 -Source 'srg')) {
		return [pscustomobject]@{
			Success       = $false
			ExitCode      = $null
			ReportPath    = $null
			FailureReason = "SSH port 22 on $SshHost is not reachable within the connect timeout; SSH may be disabled, blocked, or the host unreachable. Waypoint never enables SSH as part of a scan -- restore SSH access on the appliance and retry."
		}
	}

	# Predecessor behavior (module.transport.ssh.ps1's Build-SshTransportTargets): remove
	# any stale inspec.lock next to the profile before running -- a lock written under a
	# different mount pins absolute paths that don't exist here and breaks the wrapper
	# profile's dependency resolution.
	Remove-Item -Path (Join-Path $ProfilePath 'inspec.lock') -Force -ErrorAction SilentlyContinue

	$InspecArguments = "`"$ProfilePath`" -t ssh://$Username@$SshHost --reporter=json:`"$ReportPath`" --show-progress --enhanced-outcomes"

	# Issue #741/#743: the narrowed ssh-transport item's already-materialized resolved
	# Input file, passed as its own --input-file flag -- ScanJobHandler owns this file's
	# entire lifecycle (creation, filtering, 0600 mode, deletion); this function only
	# reads its path and never writes/deletes it, mirroring Invoke-WaypointScan's own
	# InputsFilePath handling.
	if ($InputsFilePath) {
		$InspecArguments += " --input-file `"$InputsFilePath`""
	}

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
