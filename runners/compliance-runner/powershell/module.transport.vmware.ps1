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
# for this header, the sanitization noted in NOTICE, and one functional edit (issue
# #580): Connect-StigVIServer gained an optional -SkipVCSACredential switch so
# Waypoint's API-only callers (discovery, vSphere API credential test) can decline
# VCSA credential resolution/prompting instead of hitting an impossible Get-Credential
# prompt in the noninteractive compliance runner. Every other caller and default
# behavior is unchanged. See runners/compliance-runner/powershell/README.md and
# NOTICE for full provenance.

<#
.MODULE
	module.transport.vmware.ps1

.SYNOPSIS
	vSphere (vmware transport) connection and target discovery for the STIG Runner.

.DESCRIPTION
	Handles vCenter connections (AllLinked), target discovery (vCenters, ESXi hosts,
	VCSA components, VMs), and InSpec profile path resolution. Profile paths and the
	VCSA component set come from the product catalog; vCenter/VCSA credentials are
	resolved from the vault. vSphere is a stateful transport: the vCenter PowerCLI
	session is reused by the worker (credential injection) and disconnected at cleanup,
	so Build-VsphereTransportTargets returns the connection handle to the router for
	Invoke-StigScan to reuse and dispose, rather than owning the session lifecycle.

.DEPENDENCIES
	module.common.ps1      (Script:RuntimeConfig.ProfilesBasePath, New-StigTarget)
	module.catalog.ps1     (Get-CatalogKind, Get-CatalogComponent, Get-CatalogRelease, Resolve-CatalogProfilePath)
	module.config.ps1      (Resolve-Credential)
	module.attestation.ps1 (Get-AttestationPaths)
#>

#region Connect-StigVIServer

<#
.SYNOPSIS
	Connect to vCenter Server (AllLinked) with the supplied credentials.

.DESCRIPTION
	Connects to the specified vCenter using AllLinked mode to discover all linked
	vCenters. Credentials are supplied as PSCredential objects (resolved from the
	vault by the caller); when absent, falls back to an interactive prompt. Reuses
	existing PowerCLI sessions only when one of them is already established to the
	requested vCenter (matched by session name); sessions to other vCenters -- e.g. a
	previous site row's environment in a multi-row scan (issue #281) -- never satisfy
	the reuse check, so every row gets its own fresh connect.

.PARAMETER VCenter
	FQDN or IP of the vCenter Server.

.PARAMETER VSphereCredential
	PSCredential for vSphere SSO authentication.

.PARAMETER VCSACredential
	PSCredential for VCSA SSH authentication (root).

.PARAMETER SkipVCSACredential
	Waypoint noninteractive addition (issue #580): when set, never resolves or
	prompts for a VCSA SSH credential -- the returned VCSACredential is always
	$null in that case, regardless of whether -VCSACredential was supplied. Use
	for API-only operations (inventory discovery, vSphere API credential testing)
	that only ever open a vSphere SSO session and have no legitimate use for a
	VCSA credential at all. Defaults to $false so every pre-existing caller (bare
	CLI, full scan setup via Connect-VsphereTransportRow) is unchanged: VCSA
	credential resolution/prompting still happens exactly as before.

.PARAMETER Source
	Component identifier for logging.

.OUTPUTS
	[PSCustomObject] with properties: Sessions, VSphereCredential, VCSACredential, DisconnectAtCleanup.
#>
function Connect-StigVIServer {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[string]$VCenter,

		[Parameter()]
		[pscredential]$VSphereCredential,

		[Parameter()]
		[pscredential]$VCSACredential,

		[Parameter()]
		[switch]$SkipVCSACredential,

		[Parameter()]
		[string]$Source = 'vSphere'
	)

	$WriteLogParams = Get-LogSplat $Source

	Write-Log "Connecting to vCenter '$VCenter' and all linked vCenters..." -Severity 'Info' @WriteLogParams

	$DisconnectAtCleanup = $false

	# Reuse existing sessions only when one of them IS the requested vCenter. The bare
	# any-session-exists check this replaces broke multi-row scans (issue #281): row A's
	# fresh connect populates $Global:DefaultVIServers, so row B's connect would silently
	# "reuse" row A's sessions and rescan row A's environment instead of ever reaching
	# row B's vCenter. Name-matching keeps the legitimate reuse case (a pre-established
	# session to THIS vCenter) and falls through to a fresh connect for any other host.
	# On reuse it still returns ALL established sessions, unchanged from before: in the
	# single-environment case they are that environment's AllLinked set.
	if (@($Global:DefaultVIServers | Where-Object { $_.Name -eq $VCenter }).Count -gt 0) {
		$VISessions = $Global:DefaultVIServers
		Write-Log "Using existing vSphere sessions" -Severity 'Info' @WriteLogParams
	} else {
		# Credentials are resolved from the vault by the caller and passed in. When
		# absent (e.g. a bare CLI run with no site target), fall back to a prompt.
		if ($null -eq $VSphereCredential) {
			Write-Log "vSphere SSO credentials not provided. Prompting for input." -Severity 'Info' @WriteLogParams
			$VSphereCredential = Get-Credential -Message "Enter vSphere SSO credentials (e.g., administrator@vsphere.local)"
		}

		if ($SkipVCSACredential) {
			# Waypoint noninteractive addition (issue #580): the caller has declared
			# this connection is API-only (discovery, vSphere API credential test)
			# and will never use a VCSA credential -- never resolve or prompt for
			# one, and never carry forward one that happened to be passed anyway.
			$VCSACredential = $null
		} elseif ($null -eq $VCSACredential) {
			Write-Log "VCSA root credentials not provided. Prompting for input." -Severity 'Info' @WriteLogParams
			$VCSACredential = Get-Credential -Message "Enter VCSA root SSH credentials (Username: root)" -UserName "root"
		}

		try {
			$VISessions = Connect-VIServer -Server $VCenter -AllLinked -Credential $VSphereCredential -Protocol https
			$DisconnectAtCleanup = $true
		} catch {
			throw "Failed to establish vCenter connection(s) to '$VCenter': $($_.Exception.Message)"
		}
	}

	foreach ($Session in $VISessions) {
		Write-Log "Successfully connected to '$($Session.Name)'" -Severity 'Success' @WriteLogParams
	}

	if ($null -eq $VISessions -or @($VISessions).Count -eq 0) {
		throw "No vCenter sessions could be established after attempting connection to '$VCenter'."
	}

	return [PSCustomObject]@{
		Sessions            = $VISessions
		VSphereCredential   = $VSphereCredential
		VCSACredential      = $VCSACredential
		DisconnectAtCleanup = $DisconnectAtCleanup
	}
}

#endregion

#region Get-InspecProfilePaths

<#
.SYNOPSIS
	Resolve and validate InSpec profile paths for the configured vSphere/STIG version.

.DESCRIPTION
	Builds the full paths to all InSpec profiles (vSphere and VCSA components) from the
	product catalog (catalog.products.vsphere.<Version>.stig) and validates they exist.

.PARAMETER ProfilesBase
	Root directory of the DoD Compliance and Automation repo.

.PARAMETER Version
	Catalog vSphere version key (e.g. "8-0"). Defaults from
	$Script:RuntimeConfig.VSphereVersion (dotted, e.g. "8.0") converted to the dash form.

.PARAMETER Kind
	Catalog kind for the vSphere product: 'stig' (vsphere/8-0, CKL + STIG Manager) or
	'srg' (vsphere/9-0, HDF-only -- the VCF 9.x content decomposed into the vsphere
	product, issue #326). Defaults to 'stig' so the pre-existing vSphere-8 callers are
	unchanged. Selects which catalog kind block (and therefore profileBase/release/
	components) the profile paths resolve from; the returned object echoes it back as
	`Kind` so the target builders know to build HDF-only srg targets.

.PARAMETER ScanInput
	The vSphere site target's per-component scanInput map (keys: vcenter, esxi, vcsa),
	or $null. When a component's key is set it points at the operator's /config input
	file; when absent the catalog exampleInput from the mounted repo is used as a bare
	fallback, and when neither resolves the scan runs without an --input-file.

.PARAMETER Source
	Component identifier for logging.

.OUTPUTS
	[PSCustomObject] with properties: VMProfile, ESXiProfile, VCenterProfile,
	VCSAStigs (ordered hashtable Name -> path), EsxiInputsFile, VCenterInputsFile,
	VCSAInputsFile (each input file may be '' when none applies).
#>
function Get-InspecProfilePaths {
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Resolves the full set of InSpec profile paths (vSphere components plus the VCSA profile map) and their input files')]
	[CmdletBinding()]
	param(
		[Parameter()]
		[string]$ProfilesBase,

		[Parameter()]
		[string]$Version,

		[Parameter()]
		[ValidateSet('stig', 'srg')]
		[string]$Kind = 'stig',

		[Parameter()]
		[AllowNull()]
		[object]$ScanInput,

		[Parameter()]
		[string]$Source = 'Profiles'
	)

	$WriteLogParams = Get-LogSplat $Source

	if (-not $ProfilesBase) { $ProfilesBase = $Script:RuntimeConfig.ProfilesBasePath }
	# Catalog version key (e.g. '8-0'); derive from the dotted $Script:RuntimeConfig.VSphereVersion
	# when absent. No '?? default' guard needed: $Script:RuntimeConfig.VSphereVersion is
	# provably never null (seeded from $Script:RuntimeConfigDefaults.VSphereVersion at
	# bootstrap in module.common.ps1, and every writer -- the Invoke-StigScan CLI override
	# and the Invoke-CliRoute ConfigFile route -- only assigns a truthy value).
	if (-not $Version) { $Version = ($Script:RuntimeConfig.VSphereVersion -replace '\.', '-') }

	Write-Log "Validating InSpec profile paths (vsphere $Version $Kind)" -Severity 'Info' @WriteLogParams

	if (-not (Test-Path -Path $ProfilesBase)) {
		throw "InSpec profiles base path does not exist: $ProfilesBase"
	}

	$KindBlock = Get-CatalogKind -Product 'vsphere' -Version $Version -Kind $Kind
	$Components = Get-CatalogComponent -Product 'vsphere' -Version $Version -Kind $Kind
	if ($null -eq $Components) {
		throw "No catalog entry for vsphere/$Version/$Kind. Check the catalog and the site target version."
	}
	$ProfileBase = $KindBlock.profileBase
	# Node-level release (e.g. "v2r3-stig" for 8-0 stig, "Y26M05-srg" for 9-0 srg),
	# shared by the stig|srg and remediate kinds.
	$Release = Get-CatalogRelease -Product 'vsphere' -Version $Version

	# Scan inputs, per component: the operator's site scanInput wins, else the repo
	# example, else none ('' -> the scan runs without an --input-file). vcenter/esxi
	# carry a catalog exampleInput; VCSA has none, so it resolves operator-only.
	$VCenterComp = Get-PSObjectMember -InputObject $Components -Name 'vcenter'
	$EsxiComp = Get-PSObjectMember -InputObject $Components -Name 'esxi'
	$VCenterInputsFile = Resolve-ScanInputFile `
		-SiteInput (Get-PSObjectMember -InputObject $ScanInput -Name 'vcenter') `
		-ExampleSubpath (Get-PSObjectMember -InputObject $VCenterComp -Name 'exampleInput') `
		-ProfileBase $ProfileBase -Version $Release -ProfilesBase $ProfilesBase
	$EsxiInputsFile = Resolve-ScanInputFile `
		-SiteInput (Get-PSObjectMember -InputObject $ScanInput -Name 'esxi') `
		-ExampleSubpath (Get-PSObjectMember -InputObject $EsxiComp -Name 'exampleInput') `
		-ProfileBase $ProfileBase -Version $Release -ProfilesBase $ProfilesBase
	$VCSAInputsFile = Resolve-ScanInputFile `
		-SiteInput (Get-PSObjectMember -InputObject $ScanInput -Name 'vcsa') `
		-ExampleSubpath $null `
		-ProfileBase $ProfileBase -Version $Release -ProfilesBase $ProfilesBase

	# Resolve component profile paths from the catalog.
	$GetComp = {
		param($Key)
		$C = Get-PSObjectMember -InputObject $Components -Name $Key
		if ($null -eq $C) { throw "Catalog vsphere/$Version/stig is missing component '$Key'." }
		Resolve-CatalogProfilePath -ProfileBase $ProfileBase -Version $Release -ProfileSubpath $C.profileSubpath -ProfilesBase $ProfilesBase
	}
	$VMProfile = & $GetComp 'vm'
	$ESXiProfile = & $GetComp 'esxi'
	$VCenterProfile = & $GetComp 'vcenter'

	# VCSA components: catalog keys are 'vcsa-component-<Name>'; VCSAStigs is keyed by the
	# bare <Name> because Build-VCSATarget makes the TargetType 'vcsa-component-<Name>'.
	$VCSAStigs = [ordered]@{}
	foreach ($CompProp in $Components.PSObject.Properties) {
		if ($CompProp.Name -like 'vcsa-component-*') {
			$Display = $CompProp.Name -replace '^vcsa-component-', ''
			$VCSAStigs[$Display] = Resolve-CatalogProfilePath -ProfileBase $ProfileBase -Version $Release -ProfileSubpath $CompProp.Value.profileSubpath -ProfilesBase $ProfilesBase
		}
	}

	# Validate all profile paths exist
	$ProfilePathsToValidate = @($VMProfile, $ESXiProfile, $VCenterProfile)
	foreach ($Entry in $VCSAStigs.GetEnumerator()) {
		$ProfilePathsToValidate += $Entry.Value
	}

	foreach ($Path in $ProfilePathsToValidate) {
		if (-not (Test-Path $Path -PathType Container)) {
			throw "Required InSpec profile directory not found: '$Path'. Please check profiles mount and version/STIG settings."
		}
	}

	Write-Log "All required InSpec profiles validated" -Severity 'Success' @WriteLogParams

	return [PSCustomObject]@{
		Kind             = $Kind
		VMProfile        = $VMProfile
		ESXiProfile      = $ESXiProfile
		VCenterProfile   = $VCenterProfile
		VCSAStigs        = $VCSAStigs
		EsxiInputsFile   = $EsxiInputsFile
		VCenterInputsFile = $VCenterInputsFile
		VCSAInputsFile   = $VCSAInputsFile
	}
}

#endregion

#region Get-StigTargets

<#
.SYNOPSIS
	Discover all scan targets from the vSphere environment.

.DESCRIPTION
	Connects to all linked vCenters and discovers vCenter servers, VCSA components,
	ESXi hosts, and virtual machines. Builds a target queue with all metadata
	needed for parallel scanning.

.PARAMETER VISessions
	Array of VIServer sessions to discover and build targets from. Under issue #293's
	claim-before-build dedupe this may be only the subset of the row's sessions the
	router's claim registry assigned to this row.

.PARAMETER LookupSessions
	The row's FULL connected session set, threaded to Build-VCenterTarget and
	Build-VCSATarget as their -VISessions for the issue #292 identity-verified
	appliance-VM lookup. Defaults to -VISessions when omitted/empty (the single-row
	and bare-CLI paths, where the two sets are identical). Kept separate from
	-VISessions because claims govern what is BUILT, never what the lookup can SEE
	(issue #293): a row that lost a vCenter to an earlier row's claim must still
	search its own full AllLinked inventory for appliance VMs.

.PARAMETER Profiles
	Profile paths object from Get-InspecProfilePaths.

.PARAMETER Attestations
	Attestation paths object from Get-AttestationPaths.

.PARAMETER DirStructure
	Directory structure object from New-ScanDirectoryStructure.

.PARAMETER VSphereVersion
	vSphere version string.

.PARAMETER ScanFilter
	Filter targets: 'all', 'vcenter', 'esxi', 'vcsa', 'vm'.

.PARAMETER VCSACredential
	Resolved VCSA SSH PSCredential, forwarded to Build-VCSATarget so the ssh transport
	URI's username and the target's attached Credential come from the same source
	(issue #228). Only required when ScanFilter includes 'vcsa'.

	When $null (e.g. Connect-StigVIServer's existing-session-reuse branch, which does
	not resolve vcsaCredentialRef or prompt), Build-VCSATarget is never called -- the
	null-credential throw it documents is defense-in-depth for direct callers, not the
	behavior seen through this path. Instead (issue #235): a VCSA-only ScanFilter
	('vcsa') throws here so the scan still fails loudly (a VCSA-only run with nothing
	to scan is not a success); any other ScanFilter (in practice 'all') logs a
	Warning naming the missing credential and skips only the VCSA component targets for
	that session, leaving vCenter/ESXi/VM discovery to proceed.

.PARAMETER Source
	Component identifier for logging.

.OUTPUTS
	[PSCustomObject] with TargetsList (Queue) and CompletedTargets (ConcurrentBag).
#>
function Get-StigTargets {
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Discovers and returns the queue of all vSphere scan targets (vCenter, VCSA, ESXi, VM)')]
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		$VISessions,

		[Parameter()]
		[AllowNull()]
		$LookupSessions,

		[Parameter(Mandatory)]
		$Profiles,

		[Parameter(Mandatory)]
		$Attestations,

		[Parameter(Mandatory)]
		$DirStructure,

		[Parameter()]
		[string]$VSphereVersion,

		[Parameter()]
		[ValidateSet('all', 'vcenter', 'esxi', 'vcsa', 'vm')]
		[string]$ScanFilter = 'all',

		[Parameter()]
		[pscredential]$VCSACredential,

		[Parameter()]
		[string]$Source = 'Discovery'
	)

	$WriteLogParams = Get-LogSplat $Source
	if (-not $VSphereVersion) { $VSphereVersion = $Script:RuntimeConfig.VSphereVersion }
	# The appliance-VM lookup set defaults to the build set (single-row/bare-CLI: they
	# are the same sessions); the router's claim-before-build pass (issue #293) passes
	# the row's full session set here while -VISessions carries only the claimed subset.
	if (-not $LookupSessions) { $LookupSessions = $VISessions }

	Write-Log "Gathering target information for all vCenter(s), ESXi hosts, and VMs" -Severity 'Info' @WriteLogParams

	$FqdnRegex = '^(?=.{1,255}$)(?!-)[A-Za-z0-9-]{1,63}(?<!-)\.(?!-)(?:[A-Za-z0-9-]{1,63}\.?)+[A-Za-z]{2,6}$'
	$NameRegex = "[^a-zA-Z0-9_-]"
	$TargetsList = [System.Collections.Generic.Queue[PSObject]]::new()
	$CompletedTargets = [System.Collections.Concurrent.ConcurrentBag[PSObject]]::new()

	foreach ($VCSession in $VISessions) {
		$CurrentVcReportPath = Join-Path $DirStructure.RunRoot.FullName $VCSession.Name
		New-Item -ItemType Directory -Path $CurrentVcReportPath -Force -ErrorAction Stop | Out-Null

		# --- vCenter Targets ---
		if ($ScanFilter -in @('all', 'vcenter')) {
			Build-VCenterTarget -VCSession $VCSession -VISessions $LookupSessions -Profiles $Profiles -Attestations $Attestations -DirStructure $DirStructure -ReportPath $CurrentVcReportPath -VSphereVersion $VSphereVersion -FqdnRegex $FqdnRegex -TargetsList $TargetsList @WriteLogParams
		}

		# --- VCSA Targets ---
		if ($ScanFilter -in @('all', 'vcsa')) {
			if ($null -eq $VCSACredential) {
				# No VCSA credential resolved -- reachable via Connect-StigVIServer's
				# existing-session-reuse branch, which skips both vcsaCredentialRef
				# resolution and the interactive prompt (issue #235). Scope the failure:
				# a VCSA-only request has nothing else to scan, so fail loudly rather than
				# silently reporting an empty "success"; a mixed request (ScanFilter
				# 'all') skips only the VCSA family -- loudly, via a Warning naming the
				# missing credential -- and lets vCenter/ESXi/VM discovery proceed.
				$Message = "No VCSA credential resolved for '$($VCSession.Name)' -- check vcsaCredentialRef in site config, the --vcsa-user/-VCSACredential CLI override, or that the reused vCenter session was connected with a VCSA credential."
				if ($ScanFilter -eq 'vcsa') {
					throw "Get-StigTargets: $Message VCSA-only scan requested ('-Scan vcsa'); no other target family to fall back to."
				}
				Write-Log "Skipping VCSA component targets for '$($VCSession.Name)': $Message vCenter/ESXi/VM targets will still be discovered." -Severity 'Warning' @WriteLogParams
				# issue #246: record the skip so Invoke-StigScan's final [Summary] line,
				# AllTargetsSummary.xml, and process exit code (issue #243's contract, via
				# Get-ScanExitCode) all surface this VCSA family as skipped instead of the
				# run silently reading as a full success once vCenter/ESXi/VM discovery
				# completes. Add-ScanSkip/Get-ScanSkips/Clear-ScanSkips live in
				# module.common.ps1, already dot-sourced alongside this module.
				Add-ScanSkip -Family 'vcsa' -Reason $Message
			} else {
				Build-VCSATarget -VCSession $VCSession -VISessions $LookupSessions -Profiles $Profiles -Attestations $Attestations -DirStructure $DirStructure -ReportPath $CurrentVcReportPath -VSphereVersion $VSphereVersion -FqdnRegex $FqdnRegex -TargetsList $TargetsList -VCSACredential $VCSACredential @WriteLogParams
			}
		}

		# --- ESXi Targets ---
		if ($ScanFilter -in @('all', 'esxi')) {
			Build-ESXiTarget -VCSession $VCSession -Profiles $Profiles -Attestations $Attestations -DirStructure $DirStructure -ReportPath $CurrentVcReportPath -VSphereVersion $VSphereVersion -TargetsList $TargetsList @WriteLogParams
		}

		# --- VM Targets ---
		if ($ScanFilter -in @('all', 'vm')) {
			Build-VMTarget -VCSession $VCSession -Profiles $Profiles -Attestations $Attestations -DirStructure $DirStructure -ReportPath $CurrentVcReportPath -VSphereVersion $VSphereVersion -FqdnRegex $FqdnRegex -NameRegex $NameRegex -TargetsList $TargetsList @WriteLogParams
		}
	}

	Write-Log "Finished gathering $($TargetsList.Count) targets" -Severity 'Success' @WriteLogParams

	return [PSCustomObject]@{
		TargetsList      = $TargetsList
		CompletedTargets = $CompletedTargets
	}
}

#endregion

#region Connect-VsphereTransportRow

<#
.SYNOPSIS
	Resolve one vsphere site row's config and establish its vCenter connection (connect phase).

.DESCRIPTION
	The connect half of the vSphere transport flow, split out of
	Build-VsphereTransportTargets (issue #293) so the router can connect EVERY vsphere
	row before any row builds targets. Connect-all-first is what enables both halves
	of the multi-row story: (a) the router's claim registry can assign each unique
	discovered vCenter to the first row that found it BEFORE any duplicate targets
	are built, and (b) the issue #292 cross-row appliance-VM lookup sees every row's
	sessions regardless of site order (previously a row listed before the row hosting
	its appliance VM fell back to name/DNS metadata).

	For ONE row (or the bare-CLI no-row path) this: derives the catalog version from
	the row, resolves the vCenter (CLI override > row connection.host > prompt) and
	credentials (CLI > the row's vault refs > the connect prompt), validates profile
	and attestation paths (both read from the row: scanInput/attestation are
	row-scoped), and connects (AllLinked, with issue #281's name-scoped session
	reuse). Everything up to -- but not including -- target discovery, preserving the
	pre-split failure ordering exactly: a bad profile path or a refused connection
	throws here, before any target exists. Build-VsphereTransportTargets consumes the
	returned context for the build phase.

.PARAMETER SiteTargetRow
	The vsphere/stig site.json target row to connect, selected and passed in by the
	router. $null/omitted is the bare-CLI path -- no site row exists, so the vCenter
	comes from -TargetVCenter (or a prompt) and the credentials from the CLI
	overrides (or prompts).

.PARAMETER ScanFilter
	Product/sub-family scan filter -- used only in the per-row routing log line, so
	the connect log reads identically to the pre-split combined flow.

.PARAMETER TargetVCenter
	vCenter FQDN/IP CLI override; wins over the site target's connection.host. May be ''.

.PARAMETER VSphereCredential
	vSphere SSO PSCredential CLI override; wins over the site target's credentialRef.

.PARAMETER VCSACredential
	VCSA root PSCredential CLI override; wins over the site target's vcsaCredentialRef.

.PARAMETER Source
	Component identifier for logging.

.OUTPUTS
	[PSCustomObject] @{ Connection; Profiles; Attestations; DisplayVersion } -- the
	Connect-StigVIServer handle (Sessions + resolved credentials + cleanup flag), the
	row's resolved InSpec profile and attestation paths, and the dotted vSphere
	version string.
#>
function Connect-VsphereTransportRow {
	[CmdletBinding()]
	param(
		[Parameter()]
		[AllowNull()]
		[object]$SiteTargetRow,

		[Parameter()]
		[string]$ScanFilter = 'all',

		[Parameter()]
		[string]$TargetVCenter = '',

		[Parameter()]
		[pscredential]$VSphereCredential,

		[Parameter()]
		[pscredential]$VCSACredential,

		[Parameter()]
		[string]$Source = 'vSphere'
	)

	$WriteLogParams = Get-LogSplat $Source

	# The row is supplied by the router, which loops every scan-selected vsphere/stig
	# row (issue #281) -- one call per row, so credentialRef/vcsaCredentialRef/scanInput/
	# attestation below are all read from THIS row, never re-resolved via a first-match
	# site lookup. $null = the bare-CLI no-row path (see .PARAMETER SiteTargetRow).
	$VsphereTarget = $SiteTargetRow
	# No '?? default' guard on RuntimeConfig.VSphereVersion needed here either -- see the
	# provably-never-null note in Get-InspecProfilePaths above.
	$CatalogVersion = $VsphereTarget.version ?? ($Script:RuntimeConfig.VSphereVersion -replace '\.', '-')
	$DisplayVersion = $CatalogVersion -replace '-', '.'
	# The row's kind selects the catalog block: 'stig' (vsphere/8-0) or 'srg' (vsphere/9-0,
	# the VCF 9.x content, issue #326). The bare-CLI no-row path ($SiteTargetRow $null)
	# defaults to 'stig', preserving the ad-hoc vSphere-8 behavior. Threaded into
	# Get-InspecProfilePaths so the srg row resolves HDF-only profiles from vsphere/9-0/srg,
	# and onto the returned context so the build phase / Get-StigTargets can build srg targets.
	$CatalogKind = $VsphereTarget.kind ?? 'stig'

	Write-Log "Routing vSphere $DisplayVersion $CatalogKind (filter '$ScanFilter')" -Severity 'Info' @WriteLogParams

	# Resolve the vCenter by truthiness (the CLI passes '' when unset, not $null): a
	# non-empty CLI override wins, else the site target's connection.host, else prompt.
	$ResolvedVCenter = $TargetVCenter
	if (-not $ResolvedVCenter) {
		$ResolvedVCenter = if ($VsphereTarget.connection.host) { $VsphereTarget.connection.host } else { '' }
		if (-not $ResolvedVCenter) { $ResolvedVCenter = Read-Host "Enter vCenter FQDN or IP" }
	}

	# Credentials: an explicit CLI -*Credential wins, else the site target's vault refs.
	$VSCred = $VSphereCredential
	if ($null -eq $VSCred -and $VsphereTarget.credentialRef) { $VSCred = Resolve-Credential -Ref $VsphereTarget.credentialRef }
	$VCSACred = $VCSACredential
	if ($null -eq $VCSACred -and $VsphereTarget.vcsaCredentialRef) { $VCSACred = Resolve-Credential -Ref $VsphereTarget.vcsaCredentialRef }

	$Profiles = Get-InspecProfilePaths -Version $CatalogVersion -Kind $CatalogKind -ScanInput $VsphereTarget.scanInput
	$Attestations = Get-AttestationPaths -Attestation $VsphereTarget.attestation
	$ConnectionResult = Connect-StigVIServer -VCenter $ResolvedVCenter -VSphereCredential $VSCred -VCSACredential $VCSACred

	return [PSCustomObject]@{
		Connection     = $ConnectionResult
		Profiles       = $Profiles
		Attestations   = $Attestations
		DisplayVersion = $DisplayVersion
	}
}

#endregion

#region Build-VsphereTransportTargets

<#
.SYNOPSIS
	Discover and enqueue all vSphere scan targets for the router (vmware transport).

.DESCRIPTION
	The vmware-transport entry point Get-RoutedTargets dispatches to -- once per
	vsphere/stig site row (issue #281), the row supplied via -SiteTargetRow.
	Encapsulates the stateful vSphere flow for ONE environment in two halves:

	1. Connect (Connect-VsphereTransportRow): resolve the row's vCenter/credentials/
	   profile and attestation paths, connect (AllLinked). Run inline here for the
	   single-row and bare-CLI paths; already run by the router -- and passed in as
	   -RowContext -- for the multi-row claim-before-build pass (issue #293), which
	   must connect every row before any row builds.
	2. Build: discover vCenters/ESXi/VMs/VCSA from the sessions being built (all of
	   the row's sessions, or only the claimed subset via -BuildSessions), attach the
	   right credential to each discovered target (VCSA components ssh in as VCSA
	   root; vCenter/ESXi/VM use the vSphere SSO credential), and enqueue them into
	   the shared router queue.

	vSphere is the one stateful transport: the PowerCLI session it returns is reused by
	the parallel worker and disconnected by Invoke-StigScan at cleanup, so this returns
	the connection handle rather than owning the session lifecycle itself.

.PARAMETER DirStructure
	Scan directory structure from New-ScanDirectoryStructure.

.PARAMETER TargetsList
	The shared router queue the discovered targets are enqueued into.

.PARAMETER SiteTargetRow
	The vsphere/stig site.json target row this call scans, selected and passed in by
	the router (Get-RoutedTargets Pass 1 loops every scan-selected row). $null/omitted
	is the bare-CLI path -- no site row exists, so the vCenter comes from -TargetVCenter
	(or a prompt) and the credentials from the CLI overrides (or prompts), preserving
	the ad-hoc single-environment behavior that predates site.json rows.

.PARAMETER ScanFilter
	Product/sub-family scan filter. 'all'/'vsphere' discover every component; a
	sub-family ('vcenter'/'esxi'/'vcsa'/'vm') restricts discovery to that family.

.PARAMETER TargetVCenter
	vCenter FQDN/IP CLI override; wins over the site target's connection.host. May be ''.

.PARAMETER VSphereCredential
	vSphere SSO PSCredential CLI override; wins over the site target's credentialRef.

.PARAMETER VCSACredential
	VCSA root PSCredential CLI override; wins over the site target's vcsaCredentialRef.

.PARAMETER RowContext
	Pre-connected row context from Connect-VsphereTransportRow, passed by the router's
	multi-row claim-before-build pass (issue #293), which runs the connect phase for
	every row before any row builds. When omitted/$null (the single-row and bare-CLI
	paths), the connect phase runs inline here -- the pre-split one-shot flow.

.PARAMETER BuildSessions
	The subset of the row's connected sessions to actually build targets from -- the
	vCenters the router's claim registry assigned to this row (issue #293). Claims
	govern building, never lookup visibility: the row's FULL session set (from
	RowContext.Connection.Sessions) is still handed to discovery as the issue #292
	appliance-VM lookup scope. Omitted/empty builds every session of the row (the
	single-row and bare-CLI paths, where nothing is contested).

.PARAMETER Source
	Component identifier for logging.

.OUTPUTS
	[PSCustomObject] @{ Connection; BenchmarkVersion } -- the PowerCLI connection handle
	(from Connect-StigVIServer, for worker reuse + cleanup) and the dotted vSphere
	version string contributed for STIG-Manager benchmark resolution.
#>
function Build-VsphereTransportTargets {
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Targets is the established plural for the Build-*TransportTargets builder family')]
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		$DirStructure,

		[Parameter(Mandatory)]
		$TargetsList,

		[Parameter()]
		[AllowNull()]
		[object]$SiteTargetRow,

		[Parameter()]
		[string]$ScanFilter = 'all',

		[Parameter()]
		[string]$TargetVCenter = '',

		[Parameter()]
		[pscredential]$VSphereCredential,

		[Parameter()]
		[pscredential]$VCSACredential,

		[Parameter()]
		[AllowNull()]
		[object]$RowContext,

		[Parameter()]
		[AllowNull()]
		[object[]]$BuildSessions,

		[Parameter()]
		[string]$Source = 'vSphere'
	)

	# Connect phase: run inline (the pre-#293 one-shot flow -- single row or bare CLI)
	# unless the router already ran it for the multi-row claim-before-build pass and
	# passed the resulting context in.
	if ($null -eq $RowContext) {
		$RowContext = Connect-VsphereTransportRow -SiteTargetRow $SiteTargetRow -ScanFilter $ScanFilter `
			-TargetVCenter $TargetVCenter -VSphereCredential $VSphereCredential -VCSACredential $VCSACredential -Source $Source
	}
	$ConnectionResult = $RowContext.Connection

	# Build only the claimed subset when the router passed one (issue #293), but hand
	# discovery the row's FULL session set as the issue #292 appliance-VM lookup scope:
	# claims govern what is BUILT, never what the lookup can SEE.
	$RowSessions = @($ConnectionResult.Sessions)
	$SessionsToBuild = if ($null -ne $BuildSessions -and @($BuildSessions).Count -gt 0) { @($BuildSessions) } else { $RowSessions }

	# Scope which COMPONENT FAMILIES of this (already row-selected) row to discover. Only the
	# four vSphere sub-families map straight through to Get-StigTargets' ValidateSet; every
	# other filter that reaches here -- 'all', 'vsphere', or a kind-group filter ('srg'/'stig',
	# issue #326: the router routes -Scan srg to this pass) -- means "every component family",
	# so it maps to 'all'. Mapping kind-group filters to 'all' is safe: the row-KIND selection
	# already happened in the router (this row is only built because it matched the scan), and
	# the row's own kind (RowContext.Profiles.Kind) still governs whether each built target is
	# stig or srg. Passing 'srg'/'stig' straight through would throw -- Get-StigTargets'
	# [ValidateSet('all','vcenter','esxi','vcsa','vm')] rejects them.
	$VsphereScanFilter = if ($ScanFilter -in @('vcenter', 'esxi', 'vcsa', 'vm')) { $ScanFilter } else { 'all' }
	$TargetData = Get-StigTargets -VISessions $SessionsToBuild -LookupSessions $RowSessions -Profiles $RowContext.Profiles -Attestations $RowContext.Attestations -DirStructure $DirStructure -VSphereVersion $RowContext.DisplayVersion -ScanFilter $VsphereScanFilter -VCSACredential $ConnectionResult.VCSACredential
	while ($TargetData.TargetsList.Count -gt 0) {
		$VsTarget = $TargetData.TargetsList.Dequeue()
		# Carry the credential on the target (a runspace-injected global PSCredential does
		# not reliably reach the worker). VCSA components ssh in as VCSA root (or whatever
		# vcsaCredentialRef resolves to -- Build-VCSATarget already attached this same
		# $ConnectionResult.VCSACredential object to the target and used its .UserName in
		# the ssh:// URI, so this re-assignment is a no-op for VCSA components; it stays
		# here so every target type is attached through one uniform code path). vCenter/
		# ESXi/VM use the vSphere SSO credential for the vmware:// transport. The wildcard
		# has a leading '*' so it also matches the srg-kind 'srg-vcsa-component-*' TargetType
		# (vsphere/9-0, issue #326), which must likewise carry the VCSA credential.
		$VsCredForTarget = if ($VsTarget.TargetType -like '*vcsa-component*') { $ConnectionResult.VCSACredential } else { $ConnectionResult.VSphereCredential }
		$VsTarget | Add-Member -NotePropertyName 'Credential' -NotePropertyValue $VsCredForTarget -Force
		$TargetsList.Enqueue($VsTarget)
	}

	# Only STIG-kind vSphere (vsphere/8-0) contributes a benchmark version for STIG-Manager
	# CKL correction. The srg kind (vsphere/9-0, issue #326) is HDF-only -- no CKL, no STIG
	# Manager -- so it contributes no benchmark version, exactly as the ssh/srg transport
	# path does (see module.targets.ps1 pass 2).
	$RowKind = if ($RowContext.Profiles.Kind) { $RowContext.Profiles.Kind } else { 'stig' }
	$BenchmarkVersion = if ($RowKind -eq 'srg') { $null } else { $RowContext.DisplayVersion }

	return [PSCustomObject]@{
		Connection       = $ConnectionResult
		BenchmarkVersion = $BenchmarkVersion
	}
}

#endregion

#region Get-VCenterApplianceHostInfo

<#
.SYNOPSIS
	Extract Name/FQDN/IP/MAC for a vCenter session's appliance VM (for CKL population).

.DESCRIPTION
	Finds the vCenter Appliance VM for the session and pulls Name/IP/MAC/FQDN from
	guest tools. The lookup (issue #292) searches the row's own sessions first, then
	every other connected server in a stable name-sorted order -- the appliance VM may
	legitimately live in another environment's inventory (ELM sibling-hosting; a VCF
	management domain hosting workload vCenters' appliances). The name regex is only a
	cheap pre-filter: a candidate is accepted ONLY when its guest identity matches the
	vCenter being built (Guest.HostName equals the vCenter FQDN, case-insensitive, or
	a Guest.IPAddress equals the vCenter's DNS-resolved IP). A cross-row match logs
	which inventory produced it. No verified candidate anywhere -> fallbacks: MAC from
	the virtual NIC hardware, FQDN from the session name (which is itself the FQDN),
	and IP via DNS -- never an unverified name hit's metadata. Used by both
	Build-VCenterTarget and Build-VCSATarget, which derive from the same vCenter
	session. Since issue #293's connect-all-before-build restructure, every row's
	sessions are established before any row builds, so this cross-row search is
	order-independent: a row no longer has to be listed after the row hosting its
	appliance VM for the lookup to see that inventory.

.PARAMETER VCSession
	The VIServer session object.

.PARAMETER VISessions
	The current row's full connected session set (the AllLinked Connect-StigVIServer
	result), searched first and in the given order. Omitted/empty defaults to
	@($VCSession) -- a row-local search around the one session.

.PARAMETER FqdnRegex
	Regex a guest HostName must match to be accepted as the FQDN.

.PARAMETER Source
	Component identifier for logging.

.OUTPUTS
	[PSCustomObject] @{ Name; FQDN; IP; Mac }.
#>
function Get-VCenterApplianceHostInfo {
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidGlobalVars', '', Justification = '$Global:DefaultVIServers is PowerCLI''s own connected-session registry -- the only authoritative list of OTHER rows'' servers for the deliberate, identity-verified cross-row appliance lookup (issue #292). Read-only here; Connect-StigVIServer reads the same global')]
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		$VCSession,

		[Parameter()]
		$VISessions,

		[Parameter()]
		[string]$FqdnRegex = '.*',

		[Parameter()]
		[string]$Source = 'Discovery'
	)

	$WriteLogParams = Get-LogSplat $Source

	# Degrade to a row-local search when the caller didn't thread the row's session
	# set down (covers an omitted parameter and an explicit $null/empty alike) -- the
	# session being built is always a valid row-local scope.
	if (-not $VISessions) { $VISessions = @($VCSession) }

	# issue #244: Get-TargetShortName keeps the full address for an IP-literal
	# VCSession.Name instead of truncating to the first octet, so this match pattern
	# stays specific (an inventory VM Name is unlikely to contain the full dotted/
	# colon-separated address as a substring) rather than matching on a bare octet like
	# '10', which risked over-matching unrelated VMs.
	$NamePattern = Get-TargetShortName -HostName $VCSession.Name

	# issue #292: resolve the vCenter's DNS IP once, up front. It feeds both the
	# IP-based identity check in the loop below and the no-appliance-VM IP fallback at
	# the end. '' when resolution fails (Resolve-HostIpViaDns never throws): the IP
	# check is then skipped cleanly and hostname-only verification still applies.
	$ResolvedVCenterIp = Resolve-HostIpViaDns -HostName $VCSession.Name -Source $Source

	# issue #292: deterministic search order -- the row's own sessions first (in their
	# connect order), then every OTHER connected server sorted by name. Cross-row
	# search is deliberate (see .DESCRIPTION), but only an identity-VERIFIED candidate
	# is ever accepted; a name hit alone proves nothing about which vCenter it is (the
	# original cross-row metadata bleed).
	$RowSessionNames = @(@($VISessions) | ForEach-Object { [string]$_.Name })
	$OtherSessions = @(@($Global:DefaultVIServers) | Where-Object { $null -ne $_ -and [string]$_.Name -notin $RowSessionNames } | Sort-Object -Property Name)

	# issue #305: every rejected name-matching candidate gets a concrete, one-line Debug
	# reason (nothing is emitted for a candidate that verifies) so a total lookup failure
	# is diagnosable from the DEFAULT-level fallback Warning without re-running at Debug.
	# Reasons are collected here and summarized into that Warning below.
	$RejectionReasons = [System.Collections.Generic.List[string]]::new()

	$VM_VCenterAppliance = $null
	$MatchInventoryName = ''
	foreach ($Scope in (@($VISessions) + $OtherSessions)) {
		foreach ($Candidate in @(Get-VM -Server $Scope -ErrorAction SilentlyContinue | Where-Object Name -Match $NamePattern)) {
			# A candidate with no Guest data (powered off / no VMware Tools) cannot
			# prove its identity -- keep searching rather than trusting the name match.
			if ($null -eq $Candidate.Guest) {
				$Reason = 'guest data absent (no VMware Tools / powered off)'
				$RejectionReasons.Add($Reason)
				Write-Log "Rejected appliance-VM candidate '$($Candidate.Name)' for '$($VCSession.Name)': $Reason" -Severity 'Debug' @WriteLogParams
				continue
			}
			$HostNameVerified = $Candidate.Guest.HostName -and ($Candidate.Guest.HostName -eq $VCSession.Name)
			$IpVerified = $ResolvedVCenterIp -and (@($Candidate.Guest.IPAddress) -contains $ResolvedVCenterIp)
			if ($HostNameVerified -or $IpVerified) {
				$VM_VCenterAppliance = $Candidate
				$MatchInventoryName = [string]$Scope.Name
				break
			}
			# Both predicates failed -- one line per candidate explaining why the OVERALL
			# verification failed, not one line per predicate. The guest-IP clause is
			# phrased for which of the three reasons actually applies: DNS never resolved
			# the vCenter's IP (the IP check was skipped entirely), the candidate reported
			# no guest IPs at all, or it reported IPs but none matched.
			$HostNamePart = if ($Candidate.Guest.HostName) { "hostName '$($Candidate.Guest.HostName)' != '$($VCSession.Name)'" } else { 'hostName not reported' }
			$CandidateIPs = @($Candidate.Guest.IPAddress | Where-Object { $_ })
			$IpPart =
				if (-not $ResolvedVCenterIp) { 'vCenter IP could not be resolved via DNS (IP check skipped)' }
				elseif ($CandidateIPs.Count -eq 0) { 'candidate reports no guest IP addresses' }
				else { "no guest IP matched '$ResolvedVCenterIp'" }
			$Reason = "$HostNamePart and $IpPart"
			$RejectionReasons.Add($Reason)
			Write-Log "Rejected appliance-VM candidate '$($Candidate.Name)' for '$($VCSession.Name)': $Reason" -Severity 'Debug' @WriteLogParams
		}
		if ($null -ne $VM_VCenterAppliance) { break }
	}

	if ($null -ne $VM_VCenterAppliance -and $MatchInventoryName -notin $RowSessionNames) {
		Write-Log "Appliance VM '$($VM_VCenterAppliance.Name)' for '$($VCSession.Name)' found in connected inventory '$MatchInventoryName' (cross-row), identity-verified." -Severity 'Info' @WriteLogParams
	}

	$Name = ""; $IP = ""; $MAC = ""; $FQDN = ""
	if ($null -ne $VM_VCenterAppliance) {
		$Name = $VM_VCenterAppliance.Name
		$IP = if ($null -ne $VM_VCenterAppliance.Guest -and $null -ne $VM_VCenterAppliance.Guest.IPAddress) { $VM_VCenterAppliance.Guest.IPAddress[0] } else { "" }
		if ($null -ne $VM_VCenterAppliance.Guest -and $null -ne $VM_VCenterAppliance.Guest.Nics) {
			$MacAddresses = @($VM_VCenterAppliance.Guest.Nics.Device.MacAddress)
			if ($MacAddresses.Count -gt 0) { $MAC = $MacAddresses[0] }
		}
		$FQDN = if ($null -ne $VM_VCenterAppliance.Guest -and $null -ne $VM_VCenterAppliance.Guest.HostName -and $VM_VCenterAppliance.Guest.HostName -match $FqdnRegex) { $VM_VCenterAppliance.Guest.HostName } else { "" }
		# Fallback: Get MAC from virtual NIC hardware if Guest tools didn't provide it
		if (-not $MAC) {
			try {
				$VNic = Get-NetworkAdapter -VM $VM_VCenterAppliance -ErrorAction SilentlyContinue | Select-Object -First 1
				if ($null -ne $VNic -and $VNic.MacAddress) { $MAC = $VNic.MacAddress }
			} catch { }
		}
	} else {
		# issue #305: summarize the rejections so the DEFAULT-level fallback Warning
		# explains itself without turning on Debug -- distinct reasons only, so N
		# candidates rejected for the same cause collapse to one clause.
		$RejectionSummary =
			if ($RejectionReasons.Count -gt 0) {
				$DistinctReasons = @($RejectionReasons | Select-Object -Unique)
				"$($RejectionReasons.Count) name-matching candidate$(if ($RejectionReasons.Count -ne 1) { 's' }) rejected: $($DistinctReasons -join '; ')"
			} else {
				'no name-matching candidates in any connected inventory'
			}
		Write-Log "Could not find an identity-verified vCenter Appliance VM for $($VCSession.Name) ($RejectionSummary). Using vCenter name as fallback." -Severity 'Warning' @WriteLogParams
		$Name = $VCSession.Name
	}

	# Fallback: FQDN from vCenter session name (it IS the FQDN)
	if (-not $FQDN) { $FQDN = $VCSession.Name }

	# Fallback: the DNS-resolved IP (resolved once, above) if Guest tools didn't provide one
	if (-not $IP) { $IP = $ResolvedVCenterIp }

	return [PSCustomObject]@{ Name = $Name; FQDN = $FQDN; IP = $IP; Mac = $MAC }
}

#endregion

#region Build-VCenterTarget

<#
.SYNOPSIS
	Build scan target object for a vCenter Server.

.DESCRIPTION
	Composes the single vCenter scan target for one VIServer session: creates its
	report subdirectory, resolves the appliance host info (Name/FQDN/IP/MAC) via
	Get-VCenterApplianceHostInfo, builds the InSpec/SAF/CKL command arguments (New-
	AttestStep, New-CklConvertArgs), and enqueues the resulting target (New-StigTarget)
	onto TargetsList for the parallel worker to pick up.

.PARAMETER VCSession
	One connected VIServer session (an element of the AllLinked Connect-StigVIServer
	result) to build the vCenter target for.

.PARAMETER VISessions
	The row's full connected session set, threaded down to
	Get-VCenterApplianceHostInfo so its identity-verified appliance-VM lookup searches
	the row's own inventories before other connected servers (issue #292). Optional --
	omitted degrades that lookup to a row-local search around VCSession.

.PARAMETER Profiles
	Profile paths object from Get-InspecProfilePaths (VCenterProfile, VCenterInputsFile).

.PARAMETER Attestations
	Attestation paths object from Get-AttestationPaths (VCenterTemplate).

.PARAMETER DirStructure
	Directory structure object from New-ScanDirectoryStructure (VCenterCklPath).

.PARAMETER ReportPath
	Per-vCenter report root this target's InSpec/CKL files are written under.

.PARAMETER VSphereVersion
	vSphere version string, used in generated report/CKL file names.

.PARAMETER FqdnRegex
	Regex used by Get-VCenterApplianceHostInfo to validate the appliance's advertised
	hostname as a real FQDN.

.PARAMETER TargetsList
	Queue the built target is enqueued into.

.PARAMETER Source
	Component identifier for logging.
#>
function Build-VCenterTarget {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]$VCSession,
		[Parameter()]$VISessions,
		[Parameter(Mandatory)]$Profiles,
		[Parameter(Mandatory)]$Attestations,
		[Parameter(Mandatory)]$DirStructure,
		[Parameter(Mandatory)][string]$ReportPath,
		[Parameter()][string]$VSphereVersion,
		[Parameter()][string]$FqdnRegex,
		[Parameter(Mandatory)]$TargetsList,
		[Parameter()][string]$Source = 'Discovery'
	)

	$WriteLogParams = Get-LogSplat $Source

	Write-Log "Collecting vCenter target data for $($VCSession.Name)" -Severity 'Verbose' @WriteLogParams

	# Kind is carried on the Profiles object (Get-InspecProfilePaths): 'stig' (vsphere/8-0)
	# builds the full CKL + STIG-Manager pipeline; 'srg' (vsphere/9-0, issue #326) builds an
	# HDF-only target -- srg- prefixed TargetType (so Get-CatalogReportGroupMap resolves it),
	# no CKL, a saf-view-summary artifact -- mirroring the ssh/srg target shape.
	$TargetKind = if ($Profiles.Kind) { $Profiles.Kind } else { 'stig' }
	$IsSrg = ($TargetKind -eq 'srg')

	$VCenterStigReportPath = Join-Path $ReportPath "vcenter"
	New-Item -ItemType Directory -Path $VCenterStigReportPath -Force -ErrorAction Stop | Out-Null

	$InspecFileName = "vSphere_$($VSphereVersion)_STIG_vCenter_Inspec_$($VCSession.Name)_$(Get-Date -Format 'MM-dd-yyyy_HH-mm')"
	$ReportFile = Join-Path $VCenterStigReportPath "$InspecFileName.json"

	$Attest = New-AttestStep -ReportFile $ReportFile -AttestTemplate $Attestations.VCenterTemplate `
		-AttestReportFile (Join-Path $VCenterStigReportPath "$InspecFileName.attest.json")

	# vCenter appliance host info (Name/FQDN/IP/MAC) for CKL population.
	$HostInfo = Get-VCenterApplianceHostInfo -VCSession $VCSession -VISessions $VISessions -FqdnRegex $FqdnRegex -Source $Source
	$Name = $HostInfo.Name; $IP = $HostInfo.IP; $MAC = $HostInfo.Mac; $FQDN = $HostInfo.FQDN

	$InputArg = if ($Profiles.VCenterInputsFile) { " --input-file `"$($Profiles.VCenterInputsFile)`"" } else { "" }

	$TargetArgs = @{
		TargetType       = if ($IsSrg) { 'srg-vcenter' } else { 'vcenter' }
		Kind             = $TargetKind
		AttestRequired   = $Attest.AttestRequired
		Name             = $Name; Fqdn = $FQDN; Ip = $IP; Mac = $MAC
		ReportFile       = $ReportFile
		AttestReportFile = $Attest.AttestReportFile
		AttestTemplate   = $Attestations.VCenterTemplate
		InspecProfile    = $Profiles.VCenterProfile
		InspecArgs       = "`"$($Profiles.VCenterProfile)`" -t vmware://$InputArg --reporter=json:`"$ReportFile`" --show-progress --enhanced-outcomes"
		SafAttestArgs    = $Attest.SafAttestArgs
		VCenterSession   = $VCSession
	}
	if ($IsSrg) {
		$TargetArgs.SummaryFile = Join-Path $VCenterStigReportPath "$InspecFileName.summary.yml"
	} else {
		$CklFileName = "VMware_vSphere_$($VSphereVersion)_STIG_vCenter_CKL_$($VCSession.Name)_$(Get-Date -Format 'MM-dd-yyyy_HH-mm')"
		# SAF writes the CKL to this staging path in the (non-watched) report dir; the scan
		# worker corrects it, then atomically moves it to $CklPath so the STIGMAN Watcher
		# only ever sees the finished, corrected file (see issue #50).
		$CklStagingPath = Join-Path $VCenterStigReportPath "$CklFileName.ckl"
		$TargetArgs.CklPath = Join-Path $DirStructure.VCenterCklPath.FullName "$CklFileName.ckl"
		$TargetArgs.CklStagingPath = $CklStagingPath
		$TargetArgs.SafConvertArgs = New-CklConvertArgs -ConvertInputFile $Attest.ConvertInputFile -CklStagingPath $CklStagingPath -Hostname $Name -Fqdn $FQDN -Ip $IP -Mac $MAC
	}
	$TargetItem = New-StigTarget @TargetArgs

	Write-Log "Built vCenter Target: Name=$Name, FQDN=$FQDN, IP=$IP, MAC=$MAC" -Severity 'Debug' @WriteLogParams
	$TargetsList.Enqueue($TargetItem)
}

#endregion

#region Build-VCSATarget

<#
.SYNOPSIS
	Build scan target objects for all VCSA components.

.DESCRIPTION
	Runs a cheap TCP reachability probe (Test-TargetReachable) against the VCSA
	appliance on ssh port 22 before building anything; an unreachable/hung appliance
	is recorded via Add-ScanSkip and no component target is built for it (issue #261).
	Otherwise resolves the vCenter appliance host info once for the session, then
	builds one scan target per VCSA component profile (Profiles.VCSAStigs) -- each
	ssh's into the VCSA as the resolved VCSA credential's username
	(VCSACredential.UserName; commonly but not necessarily 'root' -- see issue #228),
	carries no attestation template, and gets its own InSpec/SAF/CKL command arguments
	-- and enqueues every one onto TargetsList. The same VCSACredential is both
	interpolated into the ssh:// URI and attached to each target's Credential
	property, so the URI's username and the credential the scan worker actually
	authenticates with can never drift apart.

.PARAMETER VCSession
	One connected VIServer session (an element of the AllLinked Connect-StigVIServer
	result) whose VCSA components are being built.

.PARAMETER VISessions
	The row's full connected session set, threaded down to
	Get-VCenterApplianceHostInfo so its identity-verified appliance-VM lookup searches
	the row's own inventories before other connected servers (issue #292). Optional --
	omitted degrades that lookup to a row-local search around VCSession.

.PARAMETER Profiles
	Profile paths object from Get-InspecProfilePaths (VCSAStigs, VCSAInputsFile).

.PARAMETER Attestations
	Attestation paths object from Get-AttestationPaths. VCSA components carry no
	dedicated attestation template; passed through for signature parity with the
	sibling Build-*Target functions.

.PARAMETER DirStructure
	Directory structure object from New-ScanDirectoryStructure (VCSACklPath).

.PARAMETER ReportPath
	Per-vCenter report root this target's InSpec/CKL files are written under.

.PARAMETER VSphereVersion
	vSphere version string, used in generated report/CKL file names and to gate the
	7.0-only VCSA input file.

.PARAMETER FqdnRegex
	Regex used by Get-VCenterApplianceHostInfo to validate the appliance's advertised
	hostname as a real FQDN.

.PARAMETER TargetsList
	Queue the built targets are enqueued into.

.PARAMETER VCSACredential
	Resolved VCSA SSH PSCredential (from Connect-StigVIServer's connection result,
	itself resolved from vcsaCredentialRef / a CLI override / an interactive prompt).
	Its .UserName is interpolated into the ssh:// transport URI and the credential
	object itself is attached to every built target (New-StigTarget -Credential), so
	both come from this single source. Required -- a target-build call with no
	resolvable credential throws rather than silently defaulting the URI to 'root'
	(issue #228).

.PARAMETER Source
	Component identifier for logging.
#>
function Build-VCSATarget {
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'Attestations', Justification = 'VCSA components carry no dedicated attestation template; kept for signature parity with the sibling Build-*Target functions, which all accept -Attestations from the same caller loop')]
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]$VCSession,
		[Parameter()]$VISessions,
		[Parameter(Mandatory)]$Profiles,
		[Parameter(Mandatory)]$Attestations,
		[Parameter(Mandatory)]$DirStructure,
		[Parameter(Mandatory)][string]$ReportPath,
		[Parameter()][string]$VSphereVersion,
		[Parameter()][string]$FqdnRegex,
		[Parameter(Mandatory)]$TargetsList,
		[Parameter()][pscredential]$VCSACredential,
		[Parameter()][string]$Source = 'Discovery'
	)

	$WriteLogParams = Get-LogSplat $Source

	# No resolvable VCSA credential -- fail loudly rather than silently defaulting the
	# ssh:// URI to 'root' (issue #228). Connect-StigVIServer CAN return a $null
	# VCSACredential (e.g. the existing-session reuse branch skips both the vault-ref
	# resolution and the interactive-prompt fallback), so this is a real, reachable path.
	if ($null -eq $VCSACredential) {
		throw "Build-VCSATarget: no VCSA credential resolved for '$($VCSession.Name)'. Cannot build the ssh transport target without a username to authenticate as -- check vcsaCredentialRef in site config, the --vcsa-user/-VCSACredential CLI override, or that the reused vCenter session was connected with a VCSA credential."
	}

	# issue #261: cheap TCP reachability probe (Test-TargetReachable, module.common.ps1)
	# before any VCSA component target is built. Every component ssh's to the same
	# $VCSession.Name host on port 22, so one probe covers the whole family; an
	# unreachable/hung VCSA appliance previously ran unchecked all the way to the full
	# scan timeout. On failure, skip every component -- record it via Add-ScanSkip so it
	# surfaces in the [Summary] line, AllTargetsSummary.xml, and the exit code -- instead
	# of enqueuing InSpec runs that would hang.
	if (-not (Test-TargetReachable -TargetHost $VCSession.Name -Port 22 -Source $Source)) {
		$UnreachableMessage = "VCSA appliance '$($VCSession.Name)' not reachable on ssh port 22 within the connect timeout; skipping VCSA component targets."
		Write-Log $UnreachableMessage -Severity 'Warning' @WriteLogParams
		Add-ScanSkip -Family "vcsa-$($VCSession.Name)" -Reason $UnreachableMessage
		return
	}

	Write-Log "Collecting VCSA component target data for $($VCSession.Name)" -Severity 'Verbose' @WriteLogParams

	$VCSAReportPath = Join-Path $ReportPath "VCSA"
	New-Item -ItemType Directory -Path $VCSAReportPath -Force -ErrorAction Stop | Out-Null

	# vCenter appliance host info (Name/FQDN/IP/MAC) for CKL population -- VCSA components
	# derive from the same vCenter session.
	$HostInfo = Get-VCenterApplianceHostInfo -VCSession $VCSession -VISessions $VISessions -FqdnRegex $FqdnRegex -Source $Source
	$Name = $HostInfo.Name; $IP = $HostInfo.IP; $MAC = $HostInfo.Mac; $FQDN = $HostInfo.FQDN

	# 'stig' (vsphere/8-0): CKL + STIG-Manager pipeline, 'vcsa-component-<Name>' TargetType.
	# 'srg' (vsphere/9-0, issue #326): HDF-only, 'srg-vcsa-component-<Name>' TargetType (so
	# Get-CatalogReportGroupMap resolves it) + saf-view-summary artifact. Either way the
	# component still ssh's into the VCSA as VCSA root -- unchanged transport.
	$TargetKind = if ($Profiles.Kind) { $Profiles.Kind } else { 'stig' }
	$IsSrg = ($TargetKind -eq 'srg')

	foreach ($VCSAProfile in $Profiles.VCSAStigs.GetEnumerator()) {
		$InspecFileName = "vSphere_$($VSphereVersion)_STIG_$($VCSAProfile.Key)_Inspec_$($VCSession.Name)_$(Get-Date -Format 'MM-dd-yyyy_HH-mm')"
		$ReportFile = Join-Path $VCSAReportPath "$InspecFileName.json"

		# VCSA components have no dedicated attestation template currently (AttestTemplate $null);
		# New-AttestStep yields AttestRequired=$false and converts the raw HDF.
		$Attest = New-AttestStep -ReportFile $ReportFile -AttestTemplate $null

		$InspecProfilePathTemp = $VCSAProfile.Value
		# 7.0 VCSA profiles take an input file; 8.0 does not. Apply the resolved input
		# (operator /config or repo example) only when one exists for 7.0.
		$VCSAInputArg = if ($VSphereVersion -eq "7.0" -and $Profiles.VCSAInputsFile) { " --input-file `"$($Profiles.VCSAInputsFile)`"" } else { "" }
		# ssh:// URI username comes from the same $VCSACredential attached to the target
		# below (New-StigTarget -Credential) -- not a literal "root" -- so the URI and the
		# credential the scan worker actually authenticates with can never drift (#228).
		$InspecArgsTemp = "`"$InspecProfilePathTemp`" -t ssh://$($VCSACredential.UserName)@$($VCSession.Name)$VCSAInputArg --reporter=json:`"$ReportFile`" --show-progress --enhanced-outcomes"

		$TargetArgs = @{
			TargetType       = if ($IsSrg) { "srg-vcsa-component-$($VCSAProfile.Key)" } else { "vcsa-component-$($VCSAProfile.Key)" }
			Kind             = $TargetKind
			AttestRequired   = $Attest.AttestRequired
			Name             = "$Name-$($VCSAProfile.Key)"; Fqdn = $FQDN; Ip = $IP; Mac = $MAC
			ReportFile       = $ReportFile
			AttestReportFile = $Attest.AttestReportFile
			AttestTemplate   = $null
			InspecProfile    = $InspecProfilePathTemp
			InspecArgs       = $InspecArgsTemp
			SafAttestArgs    = $Attest.SafAttestArgs
			VCenterSession   = $VCSession
			Credential       = $VCSACredential
		}
		if ($IsSrg) {
			$TargetArgs.SummaryFile = Join-Path $VCSAReportPath "$InspecFileName.summary.yml"
		} else {
			$CklFileName = "VMware_vSphere_$($VSphereVersion)_STIG_$($VCSAProfile.Key)_CKL_$($VCSession.Name)_$(Get-Date -Format 'MM-dd-yyyy_HH-mm')"
			# Staging path in the non-watched report dir; corrected then moved to $CklPath (issue #50).
			$CklStagingPath = Join-Path $VCSAReportPath "$CklFileName.ckl"
			$TargetArgs.CklPath = Join-Path $DirStructure.VCSACklPath.FullName "$CklFileName.ckl"
			$TargetArgs.CklStagingPath = $CklStagingPath
			$TargetArgs.SafConvertArgs = New-CklConvertArgs -ConvertInputFile $Attest.ConvertInputFile -CklStagingPath $CklStagingPath -Hostname $Name -Fqdn $FQDN -Ip $IP -Mac $MAC
		}
		$TargetItem = New-StigTarget @TargetArgs

		Write-Log "Built VCSA Component Target: $($VCSAProfile.Key) for $($VCSession.Name)" -Severity 'Debug' @WriteLogParams
		$TargetsList.Enqueue($TargetItem)
	}

}

#endregion

#region Build-ESXiTarget

<#
.SYNOPSIS
	Build scan target objects for all ESXi hosts in a vCenter.

.DESCRIPTION
	Discovers every ESXi host under VCSession (Get-VMHost), then builds one scan target
	per host that is both Connected and not in maintenance mode: resolves its vmk0
	IP/MAC (non-fatal on failure), builds the InSpec/SAF/CKL command arguments
	(New-AttestStep, New-CklConvertArgs), and enqueues each target (New-StigTarget)
	onto TargetsList.

	A host that is not Connected (Disconnected/NotResponding) or is in maintenance mode
	is excluded from scanning and recorded via Add-ScanSkip instead of silently vanishing
	(issue #261): PowerCLI's VMHostImpl exposes both -ConnectionState and -State typed as
	the same VMHostState enum (Connected/Disconnected/NotResponding/Maintenance), but
	they are populated differently -- ConnectionState mirrors the vSphere API's raw
	runtime.connectionState (which never reports Maintenance), while State folds in
	runtime.inMaintenanceMode and reports Maintenance for a host that is otherwise
	Connected. So a maintenance-mode host can slip past `ConnectionState -eq Connected`
	and must be checked separately via `State -eq Maintenance`.

.PARAMETER VCSession
	One connected VIServer session (an element of the AllLinked Connect-StigVIServer
	result) whose ESXi hosts are being built.

.PARAMETER Profiles
	Profile paths object from Get-InspecProfilePaths (ESXiProfile, EsxiInputsFile).

.PARAMETER Attestations
	Attestation paths object from Get-AttestationPaths (ESXiTemplate).

.PARAMETER DirStructure
	Directory structure object from New-ScanDirectoryStructure (ESXiCklPath).

.PARAMETER ReportPath
	Per-vCenter report root this target's InSpec/CKL files are written under.

.PARAMETER VSphereVersion
	vSphere version string, used in generated report/CKL file names.

.PARAMETER TargetsList
	Queue the built targets are enqueued into.

.PARAMETER Source
	Component identifier for logging.
#>
function Build-ESXiTarget {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]$VCSession,
		[Parameter(Mandatory)]$Profiles,
		[Parameter(Mandatory)]$Attestations,
		[Parameter(Mandatory)]$DirStructure,
		[Parameter(Mandatory)][string]$ReportPath,
		[Parameter()][string]$VSphereVersion,
		[Parameter(Mandatory)]$TargetsList,
		[Parameter()][string]$Source = 'Discovery'
	)

	$WriteLogParams = Get-LogSplat $Source

	Write-Log "Collecting ESXi host target data in $($VCSession.Name)" -Severity 'Verbose' @WriteLogParams

	$DiscoveredVMHosts = @(Get-VMHost -Server $VCSession -ErrorAction SilentlyContinue)
	$VMHosts = @($DiscoveredVMHosts | Where-Object { $_.ConnectionState -eq 'Connected' -and $_.State -ne 'Maintenance' })

	# issue #261: a host dropped here used to vanish with no record -- an operator
	# couldn't tell "3 of 8 ESXi hosts were skipped" from "the run scanned 5 hosts".
	# Record each excluded host via Add-ScanSkip so it surfaces in the final [Summary]
	# line, AllTargetsSummary.xml, and the process exit code (Get-ScanExitCode) instead.
	foreach ($ExcludedVMHost in ($DiscoveredVMHosts | Where-Object { $_.ConnectionState -ne 'Connected' -or $_.State -eq 'Maintenance' })) {
		$ExclusionReasons = [System.Collections.Generic.List[string]]::new()
		if ($ExcludedVMHost.ConnectionState -ne 'Connected') { $ExclusionReasons.Add("connection state '$($ExcludedVMHost.ConnectionState)'") }
		if ($ExcludedVMHost.State -eq 'Maintenance') { $ExclusionReasons.Add('in maintenance mode') }
		$SkipReason = "ESXi host '$($ExcludedVMHost.Name)' excluded from scan: $($ExclusionReasons -join '; ')"

		Write-Log $SkipReason -Severity 'Warning' @WriteLogParams
		Add-ScanSkip -Family "esxi-$($ExcludedVMHost.Name)" -Reason $SkipReason
	}

	$VMHostsReportPath = Join-Path $ReportPath "VMHosts"
	New-Item -ItemType Directory -Path $VMHostsReportPath -Force -ErrorAction Stop | Out-Null

	# 'stig' (vsphere/8-0): CKL + STIG-Manager pipeline. 'srg' (vsphere/9-0, issue #326):
	# HDF-only, srg- prefixed TargetType, saf-view-summary artifact. The per-host input name
	# also differs between baselines -- vsphere-8 esxi/inspec.yml takes `vmhostName`, the VCF
	# 9.x vsphere/esx profile takes `esx_vmhostName` -- so it tracks the kind too.
	$TargetKind = if ($Profiles.Kind) { $Profiles.Kind } else { 'stig' }
	$IsSrg = ($TargetKind -eq 'srg')
	$HostInputName = if ($IsSrg) { 'esx_vmhostName' } else { 'vmhostName' }

	foreach ($VMHost in $VMHosts) {
		$InspecFileName = "vSphere_$($VSphereVersion)_STIG_ESXi_Inspec_$($VMHost.Name)_$(Get-Date -Format 'MM-dd-yyyy_HH-mm')"
		$ReportFile = Join-Path $VMHostsReportPath "$InspecFileName.json"

		$Attest = New-AttestStep -ReportFile $ReportFile -AttestTemplate $Attestations.ESXiTemplate `
			-AttestReportFile (Join-Path $VMHostsReportPath "$InspecFileName.attest.json")

		$Name = $VMHost.Name
		$FQDN = $Name
		$IP = ""; $MAC = ""
		try {
			$VMHostNetworkAdapter = Get-VMHostNetworkAdapter -VMHost $VMHost -Name "vmk0" -ErrorAction SilentlyContinue
			if ($null -ne $VMHostNetworkAdapter) {
				$IP = $VMHostNetworkAdapter.IP
				$MAC = $VMHostNetworkAdapter.Mac
			}
		} catch {
			Write-Log "Could not retrieve vmk0 IP/MAC for ESXi host $($VMHost.Name): $($_.Exception.Message)" -Severity 'Warning' @WriteLogParams
		}

		$InputArg = if ($Profiles.EsxiInputsFile) { " --input-file `"$($Profiles.EsxiInputsFile)`"" } else { "" }

		$TargetArgs = @{
			TargetType       = if ($IsSrg) { 'srg-esxi' } else { 'esxi' }
			Kind             = $TargetKind
			AttestRequired   = $Attest.AttestRequired
			Name             = $Name; Fqdn = $FQDN; Ip = $IP; Mac = $MAC
			ReportFile       = $ReportFile
			AttestReportFile = $Attest.AttestReportFile
			AttestTemplate   = $Attestations.ESXiTemplate
			InspecProfile    = $Profiles.ESXiProfile
			InspecArgs       = "`"$($Profiles.ESXiProfile)`" -t vmware:// --input $HostInputName=`"$($VMHost.Name)`"$InputArg --reporter=json:`"$ReportFile`" --show-progress --enhanced-outcomes"
			SafAttestArgs    = $Attest.SafAttestArgs
			VCenterSession   = $VCSession
		}
		if ($IsSrg) {
			$TargetArgs.SummaryFile = Join-Path $VMHostsReportPath "$InspecFileName.summary.yml"
		} else {
			$CklFileName = "VMware_vSphere_$($VSphereVersion)_STIG_ESXi_CKL_$($VMHost.Name)_$(Get-Date -Format 'MM-dd-yyyy_HH-mm')"
			# Staging path in the non-watched report dir; corrected then moved to $CklPath (issue #50).
			$CklStagingPath = Join-Path $VMHostsReportPath "$CklFileName.ckl"
			$TargetArgs.CklPath = Join-Path $DirStructure.ESXiCklPath.FullName "$CklFileName.ckl"
			$TargetArgs.CklStagingPath = $CklStagingPath
			$TargetArgs.SafConvertArgs = New-CklConvertArgs -ConvertInputFile $Attest.ConvertInputFile -CklStagingPath $CklStagingPath -Hostname $Name -Fqdn $FQDN -Ip $IP -Mac $MAC
		}
		$TargetItem = New-StigTarget @TargetArgs

		Write-Log "Built ESXi Target: $($VMHost.Name)" -Severity 'Debug' @WriteLogParams
		$TargetsList.Enqueue($TargetItem)
	}

}

#endregion

#region Build-VMTarget

<#
.SYNOPSIS
	Build scan target objects for all VMs in a vCenter.

.DESCRIPTION
	Discovers every VM under VCSession (excluding vCLS system VMs), then builds one
	scan target per VM: resolves guest IP/FQDN/MAC (falling back to the virtual NIC
	hardware MAC when guest tools don't report one), builds the InSpec/SAF/CKL command
	arguments (New-AttestStep, New-CklConvertArgs), and enqueues each target
	(New-StigTarget) onto TargetsList.

.PARAMETER VCSession
	One connected VIServer session (an element of the AllLinked Connect-StigVIServer
	result) whose VMs are being built.

.PARAMETER Profiles
	Profile paths object from Get-InspecProfilePaths (VMProfile).

.PARAMETER Attestations
	Attestation paths object from Get-AttestationPaths (VMTemplate).

.PARAMETER DirStructure
	Directory structure object from New-ScanDirectoryStructure (VMCklPath).

.PARAMETER ReportPath
	Per-vCenter report root this target's InSpec/CKL files are written under.

.PARAMETER VSphereVersion
	vSphere version string, used in generated report/CKL file names.

.PARAMETER FqdnRegex
	Regex a guest HostName must match to be accepted as the VM's FQDN.

.PARAMETER NameRegex
	Regex of characters stripped from the VM name to build the sanitized name used in
	generated report/CKL file names. Defaults to non-alphanumeric/underscore/hyphen.

.PARAMETER TargetsList
	Queue the built targets are enqueued into.

.PARAMETER Source
	Component identifier for logging.
#>
function Build-VMTarget {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]$VCSession,
		[Parameter(Mandatory)]$Profiles,
		[Parameter(Mandatory)]$Attestations,
		[Parameter(Mandatory)]$DirStructure,
		[Parameter(Mandatory)][string]$ReportPath,
		[Parameter()][string]$VSphereVersion,
		[Parameter()][string]$FqdnRegex,
		[Parameter()][string]$NameRegex = "[^a-zA-Z0-9_-]",
		[Parameter(Mandatory)]$TargetsList,
		[Parameter()][string]$Source = 'Discovery'
	)

	$WriteLogParams = Get-LogSplat $Source

	Write-Log "Collecting VM target data in $($VCSession.Name)" -Severity 'Verbose' @WriteLogParams

	$VMs = Get-VM -Server $VCSession -ErrorAction SilentlyContinue | Where-Object Name -NotMatch "vCLS" | Sort-Object Name
	$VMReportPath = Join-Path $ReportPath "VMs"
	New-Item -ItemType Directory -Path $VMReportPath -Force -ErrorAction Stop | Out-Null

	# 'stig' (vsphere/8-0): CKL + STIG-Manager pipeline, input `vmName`. 'srg' (vsphere/9-0,
	# issue #326): HDF-only, srg- prefixed TargetType, saf-view-summary artifact, and the VCF
	# 9.x vsphere/vm profile takes `vm_Name` rather than vsphere-8's `vmName`.
	$TargetKind = if ($Profiles.Kind) { $Profiles.Kind } else { 'stig' }
	$IsSrg = ($TargetKind -eq 'srg')
	$VmInputName = if ($IsSrg) { 'vm_Name' } else { 'vmName' }

	foreach ($VM in $VMs) {
		$SanitizedVMName = $VM.Name -replace $NameRegex, ""
		$InspecFileName = "vSphere_$($VSphereVersion)_STIG_VM_Inspec_$($SanitizedVMName)_$(Get-Date -Format 'MM-dd-yyyy_HH-mm')"
		$ReportFile = Join-Path $VMReportPath "$InspecFileName.json"

		$Attest = New-AttestStep -ReportFile $ReportFile -AttestTemplate $Attestations.VMTemplate `
			-AttestReportFile (Join-Path $VMReportPath "$InspecFileName.attest.json")

		$IP = if ($null -ne $VM.Guest -and $null -ne $VM.Guest.IPAddress) { $VM.Guest.IPAddress[0] } else { "" }
		$MAC = ""
		if ($null -ne $VM.Guest -and $null -ne $VM.Guest.Nics) {
			$MacAddresses = @($VM.Guest.Nics.Device.MacAddress)
			if ($MacAddresses.Count -gt 0) { $MAC = $MacAddresses[0] }
		}
		$FQDN = if ($null -ne $VM.Guest -and $null -ne $VM.Guest.HostName -and $VM.Guest.HostName -match $FqdnRegex) { $VM.Guest.HostName } else { "" }

		# Fallback: Get MAC from virtual NIC hardware if Guest tools didn't provide it
		if (-not $MAC) {
			try {
				$VNic = Get-NetworkAdapter -VM $VM -ErrorAction SilentlyContinue | Select-Object -First 1
				if ($null -ne $VNic -and $VNic.MacAddress) { $MAC = $VNic.MacAddress }
			} catch { }
		}

		$TargetArgs = @{
			TargetType       = if ($IsSrg) { 'srg-vm' } else { 'vm' }
			Kind             = $TargetKind
			AttestRequired   = $Attest.AttestRequired
			Name             = $VM.Name; Fqdn = $FQDN; Ip = $IP; Mac = $MAC
			ReportFile       = $ReportFile
			AttestReportFile = $Attest.AttestReportFile
			AttestTemplate   = $Attestations.VMTemplate
			InspecProfile    = $Profiles.VMProfile
			InspecArgs       = "`"$($Profiles.VMProfile)`" -t vmware:// --input $VmInputName=`"$($VM.Name)`" --reporter=json:`"$ReportFile`" --show-progress --enhanced-outcomes"
			SafAttestArgs    = $Attest.SafAttestArgs
			VCenterSession   = $VCSession
		}
		if ($IsSrg) {
			$TargetArgs.SummaryFile = Join-Path $VMReportPath "$InspecFileName.summary.yml"
		} else {
			$CklFileName = "VMware_vSphere_$($VSphereVersion)_STIG_VM_CKL_$($SanitizedVMName)_$(Get-Date -Format 'MM-dd-yyyy_HH-mm')"
			# Staging path in the non-watched report dir; corrected then moved to $CklPath (issue #50).
			$CklStagingPath = Join-Path $VMReportPath "$CklFileName.ckl"
			$TargetArgs.CklPath = Join-Path $DirStructure.VMCklPath.FullName "$CklFileName.ckl"
			$TargetArgs.CklStagingPath = $CklStagingPath
			$TargetArgs.SafConvertArgs = New-CklConvertArgs -ConvertInputFile $Attest.ConvertInputFile -CklStagingPath $CklStagingPath -Hostname $SanitizedVMName -Fqdn $FQDN -Ip $IP -Mac $MAC
		}
		$TargetItem = New-StigTarget @TargetArgs

		Write-Log "Built VM Target: $($VM.Name)" -Severity 'Debug' @WriteLogParams
		$TargetsList.Enqueue($TargetItem)
	}

}

#endregion
