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
	module.transport.nsxapi.ps1

.SYNOPSIS
	NSX (nsx-api transport) target builder for the transport-routed STIG Runner.

.DESCRIPTION
	NSX InSpec profiles run with the `local` transport and make NSX Manager REST API
	calls via the InSpec http() resource, authenticated with an X-XSRF-TOKEN header and
	a JSESSIONID cookie. This module generates that session token (POST
	/api/session/create) once per manager, bakes it into a generated inputs file in the
	non-watched report dir, and builds one scan target per NSX component from the
	product catalog. NSX requires no vCenter connection.

	Two catalog kinds route through this one builder (issue #327): the NSX 4.x product
	(kind stig) flows through the CKL -> benchmark-correction -> publish -> STIG Manager
	pipeline as vSphere; the VCF 9.x NSX baselines (products.nsx.9-x, kind srg --
	nsx/manager + nsx/routing) are HDF-only, mirroring the ssh/srg path (optional
	attestation, a `saf view summary` artifact, no CKL/correction/watcher). Both share
	the same per-manager session; only the injected auth INPUT KEY NAMES differ (4.x
	nsxManager/sessionToken/sessionCookieId vs 9.x nsx_managerAddress/nsx_sessionToken/
	nsx_sessionCookieId), resolved from the catalog kind's optional `authInputKeys`.

	This is the nsx-api branch of the transport-routed engine (issue #69): the router
	(module.targets.ps1) dispatches an `nsx-api` product here. Connection (manager) and
	credential come from the site target via Resolve-Credential; component profile paths
	come from the catalog -- replacing the legacy settings.profiles.nsx + STIG_NSX_* env.

.DEPENDENCIES
	module.common.ps1   (Script:RuntimeConfig.ProfilesBasePath, Script:RuntimeConfig.ConfigBasePath,
	                     Resolve-SiteTargetShortName, Test-TargetReachable, Add-ScanSkip)
	module.catalog.ps1  (Get-CatalogKind, Get-CatalogComponent, Get-CatalogRelease, Resolve-CatalogProfilePath, Resolve-ScanInputFile)
	module.config.ps1   (Resolve-Credential, Get-SiteTargetName)
	module.attestation.ps1 (Resolve-AttestationFile)
	module.parallelism.ps1 (Write-Log)
#>

#region Get-NsxSessionToken

<#
.SYNOPSIS
	Obtain an NSX Manager API session token and cookie.

.DESCRIPTION
	Performs the NSX `POST /api/session/create` form login and returns the
	X-XSRF-TOKEN header and JSESSIONID cookie the InSpec profile needs. Throws on
	any failure so discovery can surface it.

.PARAMETER Manager
	NSX Manager FQDN or IP.

.PARAMETER Credential
	PSCredential for the NSX user (typically 'admin').

.PARAMETER Source
	Component identifier for logging.

.OUTPUTS
	[PSCustomObject] @{ Token; Cookie } where Cookie is 'JSESSIONID=<value>'.
#>
function Get-NsxSessionToken {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[string]$Manager,

		[Parameter(Mandatory)]
		[pscredential]$Credential,

		[Parameter()]
		[string]$Source = 'NSX'
	)

	$WriteLogParams = Get-LogSplat $Source

	$Url = "https://$Manager/api/session/create"
	$Body = @{
		j_username = $Credential.UserName
		j_password = $Credential.GetNetworkCredential().Password
	}

	$Response = Invoke-WebRequest -Uri $Url -Method Post -Body $Body `
		-ContentType 'application/x-www-form-urlencoded' `
		-SkipCertificateCheck -TimeoutSec 30 -ErrorAction Stop

	# Both the X-XSRF-TOKEN and the JSESSIONID cookie come back as response headers
	# (header names are case-insensitive and values may be a single string or array).
	$Token = $null
	$SetCookie = $null
	foreach ($Key in $Response.Headers.Keys) {
		if ($Key -ieq 'X-XSRF-TOKEN') { $Token = @($Response.Headers[$Key])[0] }
		elseif ($Key -ieq 'Set-Cookie') { $SetCookie = @($Response.Headers[$Key]) -join '; ' }
	}
	if ([string]::IsNullOrEmpty($Token)) { throw "NSX session create did not return an X-XSRF-TOKEN header" }

	$CookieMatch = [regex]::Match([string]$SetCookie, 'JSESSIONID=[^;,\s]+')
	if (-not $CookieMatch.Success) { throw "NSX session create did not return a JSESSIONID cookie" }

	Write-Log "Obtained NSX session token for $Manager" -Severity 'Debug' @WriteLogParams
	return [PSCustomObject]@{
		Token  = $Token
		Cookie = $CookieMatch.Value
	}
}

#endregion

#region NSX auth-key YAML helpers

<#
.SYNOPSIS
	Read a top-level YAML scalar's value from raw text, unquoted.

.DESCRIPTION
	Line-oriented lookup for a single top-level (column-0) "key: value" scalar. Used
	to read the base inputs file's declared nsxManager (before it is stripped by
	Remove-NsxAuthInputKeys) so Build-NsxTransportTargets can warn on a mismatch
	against the target's real connection.host (issue #254). Returns $null when the
	key is absent, is nested under another mapping (indented), or has no scalar
	value on its own line (e.g. a block/mapping value) -- callers should treat $null
	as "nothing to compare". A single layer of surrounding quotes is stripped. This
	is deliberately not a general YAML parser -- none of this container's available
	modules ships one (see Remove-NsxAuthInputKeys for the same constraint).

.PARAMETER YamlText
	The YAML document text to search (may be '' or $null).

.PARAMETER KeyName
	The top-level key name to look up (exact, case-sensitive match).

.OUTPUTS
	[string] the unquoted scalar value, or $null when not found.
#>
function Get-NsxYamlTopLevelValue {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[AllowNull()]
		[AllowEmptyString()]
		[string]$YamlText,

		[Parameter(Mandatory)]
		[string]$KeyName
	)

	if ([string]::IsNullOrEmpty($YamlText)) { return $null }

	# ^ in multiline mode anchors to column 0, so an indented (nested) same-named key
	# never matches here -- only a true top-level key does.
	$Pattern = '(?m)^' + [regex]::Escape($KeyName) + ':[ \t]*(.+?)[ \t]*$'
	$Match = [regex]::Match($YamlText, $Pattern)
	if (-not $Match.Success) { return $null }

	$Value = $Match.Groups[1].Value
	if ($Value.Length -ge 2 -and (($Value[0] -eq "'" -and $Value[-1] -eq "'") -or ($Value[0] -eq '"' -and $Value[-1] -eq '"'))) {
		$Value = $Value.Substring(1, $Value.Length - 2)
	}
	return $Value
}

<#
.SYNOPSIS
	Strip top-level auth/connection keys from a base NSX inputs YAML document.

.DESCRIPTION
	Removes any top-level (column-0) YAML key in $KeyNames from $InputsText, along
	with any indented continuation lines that follow it (a block scalar or nested
	mapping value, e.g. "key:" followed by an indented block) -- so the generated
	inputs file, which concatenates this text with the runner's own auth block,
	contains exactly one occurrence of each of nsxManager/sessionToken/
	sessionCookieId: the runner's. This is the issue #254 fix -- a duplicate
	top-level YAML key resolves last-one-wins, so without this the base inputs
	file's own nsxManager/sessionToken/sessionCookieId (e.g. the repo example's
	placeholder host and blank token) would silently override the runner's real
	connection and credentials, while the scan still reported PASS.

	A blank line encountered while skipping a stripped key's continuation lines
	is treated as part of that key's value, not the end of it (issue #263) -- a
	block/mapping value can itself contain a blank line, and only a following
	line at column 0 (a new top-level key) or end-of-document ends the skip. In
	practice the three keys this filter targets are always scalar-valued, so
	this branch is defensive hardening rather than a live scenario.

	Deliberately line-oriented rather than a full YAML parse (none of this
	container's available modules ships a YAML parser): a key only matches at
	column 0 (no leading whitespace), so a same-named key nested under another
	mapping (e.g. "otherBlock:`n  sessionToken: ...") is left untouched -- stripping
	is scoped to true top-level keys only.

.PARAMETER InputsText
	The base inputs file content (may be '' or $null).

.PARAMETER KeyNames
	The top-level key names to remove (exact, case-sensitive match).

.OUTPUTS
	[string] $InputsText with the matched top-level keys, and any indented lines
	continuing their value, removed.
#>
function Remove-NsxAuthInputKeys {
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Removes potentially several top-level auth keys (nsxManager, sessionToken, sessionCookieId) from one YAML document in a single pass; Keys is accurate, not an incidental plural')]
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Pure string transform -- returns a new string, mutates no filesystem/session/system state -- so ShouldProcess confirmation does not apply despite the Remove verb')]
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[AllowNull()]
		[AllowEmptyString()]
		[string]$InputsText,

		[Parameter(Mandatory)]
		[string[]]$KeyNames
	)

	if ([string]::IsNullOrEmpty($InputsText)) { return $InputsText }

	$EscapedNames = $KeyNames | ForEach-Object { [regex]::Escape($_) }
	$KeyPattern = '^(' + ($EscapedNames -join '|') + '):(\s.*)?$'

	$Lines = $InputsText -split "`r?`n"
	$Kept = [System.Collections.Generic.List[string]]::new()
	$SkippingBlock = $false

	foreach ($Line in $Lines) {
		if ($SkippingBlock) {
			# An indented, non-blank line continues the stripped key's value (block
			# scalar or nested mapping). A blank (or whitespace-only) line is also
			# part of that value -- a block scalar can contain internal blank
			# lines -- so it does NOT end the skip either; only a following line
			# that starts at column 0 with non-whitespace content (a new top-level
			# key) ends it. Equivalently: keep skipping unless the line starts
			# with a non-whitespace character.
			if ($Line -notmatch '^\S') { continue }
			$SkippingBlock = $false
		}
		if ($Line -match $KeyPattern) {
			$SkippingBlock = $true
			continue
		}
		$Kept.Add($Line)
	}

	return ($Kept -join "`n")
}

#endregion

#region Build-NsxTransportTargets

<#
.SYNOPSIS
	Build NSX scan targets for one site target entry (nsx-api transport).

.DESCRIPTION
	Runs a cheap TCP reachability probe (Test-TargetReachable) against the site
	target's connection.host on port 443 before generating a session token or building
	anything; an unreachable/hung manager is recorded via Add-ScanSkip and no component
	target is built for it (issue #295, mirroring the ssh transport's #261 treatment).
	Otherwise, for each NSX manager in the site target's connection: generates a session
	token, writes a generated inputs file (connection/auth layered over the default NSX
	inputs) into the non-watched report dir, and enqueues one target per catalog
	component. Every pre-scan bail (missing connection.host, unreachable manager, or a
	failed session token) is recorded via Add-ScanSkip so it surfaces in the [Summary]
	line, AllTargetsSummary.xml, and the process exit code instead of the row silently
	dropping out of the run -- a single failing row does not abort the rest.

.PARAMETER Target
	A site.targets entry: @{ product; version; kind; connection = @{ host };
	credentialRef }.

.PARAMETER DirStructure
	Scan directory structure (provides NsxCklPath, the watched output dir).

.PARAMETER ReportPath
	Run report root (the non-watched dir staging/inputs live under).

.PARAMETER TargetsList
	Queue the built targets are enqueued into.

.PARAMETER Source
	Component identifier for logging.
#>
function Build-NsxTransportTargets {
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Builds and enqueues one scan target per NSX manager x catalog component; Targets is the established plural for the Build-*TransportTargets builder family')]
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		$Target,

		[Parameter(Mandatory)]
		$DirStructure,

		[Parameter(Mandatory)]
		[string]$ReportPath,

		[Parameter(Mandatory)]
		$TargetsList,

		[Parameter()]
		[string]$Source = 'NSX'
	)

	$WriteLogParams = Get-LogSplat $Source

	$Product = $Target.product
	$Version = $Target.version
	$Kind = $Target.kind

	$KindBlock = Get-CatalogKind -Product $Product -Version $Version -Kind $Kind
	$Components = Get-CatalogComponent -Product $Product -Version $Version -Kind $Kind
	if ($null -eq $Components) {
		Write-Log "NSX product '$Product/$Version/$Kind' not found in catalog; skipping" -Severity 'Warning' @WriteLogParams
		return
	}
	# Node-level release (e.g. "v1r2-stig"), shared by the stig and remediate kinds.
	$Release = Get-CatalogRelease -Product $Product -Version $Version

	$Manager = $Target.connection.host
	# issue #295: resolved once for every pre-scan skip below. Get-SiteTargetName is
	# null-safe (explicit name, else connection.host/manager/hosts[0], else $null) even
	# when connection.host itself turns out to be missing, so this is safe to compute
	# before the guard just below.
	$RowName = Get-SiteTargetName -Target $Target
	if ([string]::IsNullOrWhiteSpace($Manager)) {
		$Message = "NSX target '$Product' has no connection.host; skipping"
		Write-Log $Message -Severity 'Warning' @WriteLogParams
		Add-ScanSkip -Family "nsx-$RowName" -Reason $Message
		return
	}

	# issue #295: cheap TCP reachability probe (Test-TargetReachable, module.common.ps1,
	# issue #261) before generating a session token or building any component target for
	# this manager -- every NSX component talks to the same $Manager over the InSpec
	# http() resource on port 443, so one probe covers the whole row. An unreachable/hung
	# manager previously ran unchecked into the session-token HTTP call (its own 30s
	# timeout) with nothing recorded on failure. On failure here, skip the row -- record
	# it via Add-ScanSkip so it surfaces in the [Summary] line, AllTargetsSummary.xml, and
	# the exit code -- instead of silently building zero targets.
	if (-not (Test-TargetReachable -TargetHost $Manager -Port 443 -Source $Source)) {
		$UnreachableMessage = "NSX target '$Product' manager '$Manager' not reachable on port 443 within the connect timeout; skipping."
		Write-Log $UnreachableMessage -Severity 'Warning' @WriteLogParams
		Add-ScanSkip -Family "nsx-$RowName" -Reason $UnreachableMessage
		return
	}

	$Credential = Resolve-Credential -Ref $Target.credentialRef

	# Display version for filenames mirrors the legacy form (catalog '4-x' -> '4.x').
	$DisplayVersion = $Version -replace '-', '.'

	Write-Log "Generating NSX session token for $Manager" -Severity 'Verbose' @WriteLogParams
	try {
		$Session = Get-NsxSessionToken -Manager $Manager -Credential $Credential -Source $Source
	} catch {
		$TokenFailMessage = "NSX manager '$Manager' skipped (session token failed): $($_.Exception.Message)"
		Write-Log $TokenFailMessage -Severity 'Error' @WriteLogParams
		Add-ScanSkip -Family "nsx-$RowName" -Reason $TokenFailMessage
		return
	}

	# issue #282: the short name keys the per-manager report dir (and therefore every
	# generated-inputs, HDF, and CKL filename below), so two rows whose managers share a
	# first FQDN label would overwrite each other's files -- including the generated
	# auth inputs -- within one run. Resolve-SiteTargetShortName returns
	# Get-TargetShortName unchanged unless another same-product/kind site row collides,
	# in which case it warns and disambiguates.
	$ShortName = Resolve-SiteTargetShortName -Target $Target -Source $Source
	$IP = Resolve-HostIpViaDns -HostName $Manager -Source $Source

	# The auth block is the same for every component (one manager session); base inputs
	# and attestation are per component (each NSX component is its own baseline).
	$NsxReportPath = Join-Path $ReportPath "nsx/$ShortName"
	New-Item -ItemType Directory -Path $NsxReportPath -Force -ErrorAction Stop | Out-Null
	# Auth input key names are baseline-specific. NSX 4.x profiles read
	# nsxManager/sessionToken/sessionCookieId; the VCF 9.x NSX profiles
	# (products.nsx.9-x, srg -- nsx/manager + nsx/routing) read
	# nsx_managerAddress/nsx_sessionToken/nsx_sessionCookieId instead. The names come
	# from the catalog kind's optional `authInputKeys` map and default to the 4.x
	# legacy names when it is absent, so the 4.x product's generated inputs are
	# unchanged. The SAME three names drive BOTH the injected auth block and the strip
	# list (Remove-NsxAuthInputKeys, issue #254): a base input's own copy of these keys
	# (e.g. the repo example's blank placeholders) must be stripped or, appended after
	# the auth block, its blank value would override the runner's real session
	# (duplicate top-level YAML keys resolve last-one-wins).
	$AuthKeys = Get-PSObjectMember -InputObject $KindBlock -Name 'authInputKeys'
	$ManagerKey = if ($AuthKeys -and $AuthKeys.manager) { $AuthKeys.manager } else { 'nsxManager' }
	$TokenKey = if ($AuthKeys -and $AuthKeys.token) { $AuthKeys.token } else { 'sessionToken' }
	$CookieKey = if ($AuthKeys -and $AuthKeys.cookie) { $AuthKeys.cookie } else { 'sessionCookieId' }
	$AuthKeyNames = @($ManagerKey, $TokenKey, $CookieKey)
	$AuthBlock = "# Generated by stig-runner - connection/auth inputs for $Manager`n" +
		"${ManagerKey}: '$Manager'`n" +
		"${TokenKey}: '$($Session.Token)'`n" +
		"${CookieKey}: '$($Session.Cookie)'`n"

	# Optional waiver (mounted at /config/waivers/nsx.yml).
	$WaiverArg = ""
	$WaiverFile = Join-Path $Script:RuntimeConfig.ConfigBasePath "waivers/nsx.yml"
	if (Test-Path -Path $WaiverFile -PathType Leaf) {
		$WaiverArg = " --waiver-file `"$WaiverFile`""
		Write-Log "Applying NSX waiver file $WaiverFile" -Severity 'Info' @WriteLogParams
	}

	foreach ($CompProp in $Components.PSObject.Properties) {
		$CompKey = $CompProp.Name
		$Comp = $CompProp.Value
		$ShortComp = $CompKey -replace '^nsx-component-', ''

		$CompProfile = Resolve-CatalogProfilePath -ProfileBase $KindBlock.profileBase -Version $Release -ProfileSubpath $Comp.profileSubpath
		if (-not (Test-Path -Path $CompProfile -PathType Container)) {
			Write-Log "NSX component profile not found, skipping: '$CompProfile'" -Severity 'Warning' @WriteLogParams
			continue
		}

		$Stamp = Get-Date -Format 'MM-dd-yyyy_HH-mm'

		# Per-component generated inputs (auth block + stripped base inputs). Identical
		# for the stig (4.x) and srg (9.x) kinds -- every NSX component authenticates via
		# the same baked manager session regardless -- so it is built once here, before
		# the kind-specific target below. Per-component scan input (keyed by short
		# component name): operator's site scanInput wins, else the repo example. The base
		# inputs' own top-level auth keys (if any -- e.g. the repo example ships a
		# placeholder manager and a blank token) are stripped (Remove-NsxAuthInputKeys)
		# before the auth block is appended, so the generated inputs file contains exactly
		# one occurrence of each key: the runner's real connection/session (issue #254 --
		# duplicate top-level YAML keys resolve last-one-wins, which previously let the
		# base inputs silently override the runner's auth). $AuthKeyNames/$ManagerKey are
		# the baseline-appropriate names resolved above (4.x legacy vs 9.x nsx_*).
		$CompSiteInput = Get-PSObjectMember -InputObject $Target.scanInput -Name $ShortComp
		$InputsFile = Resolve-ScanInputFile -SiteInput $CompSiteInput -ExampleSubpath $Comp.exampleInput -ProfileBase $KindBlock.profileBase -Version $Release
		$BaseInputs = if ($InputsFile -and (Test-Path -Path $InputsFile -PathType Leaf)) { Get-Content -Path $InputsFile -Raw } else { '' }

		$BaseInputsManager = Get-NsxYamlTopLevelValue -YamlText $BaseInputs -KeyName $ManagerKey
		if ($BaseInputsManager -and $BaseInputsManager -ne $Manager) {
			Write-Log "NSX component '$ShortComp' base input declares ${ManagerKey} '$BaseInputsManager', which differs from target connection.host '$Manager'; the runner's '$Manager' always wins" -Severity 'Warning' @WriteLogParams
		}
		$BaseInputs = Remove-NsxAuthInputKeys -InputsText $BaseInputs -KeyNames $AuthKeyNames

		$GeneratedInputs = Join-Path $NsxReportPath "nsx-inputs-$ShortComp.generated.yml"
		Set-Content -Path $GeneratedInputs -Value ($AuthBlock + $BaseInputs) -ErrorAction Stop

		# Per-component attestation template (keyed by short component name): when set, the
		# HDF is attested first (the srg summary / stig CKL is then produced from the
		# attested report -- the ConvertInputFile indirection vSphere uses, issue #97).
		$CompAttestation = Get-PSObjectMember -InputObject $Target.attestation -Name $ShortComp
		$AttestTemplate = Resolve-AttestationFile -SiteAttestation $CompAttestation -Label "$Product/$ShortComp" -Source $Source

		# Shared InSpec args for both kinds: local transport (no -t), auth/connection come
		# from the generated inputs file. VCenterSession defaults to $null and no
		# credential is carried (NSX authenticates via the session token baked into the
		# generated inputs, not a per-target cred), so the srg worker's ssh --config secret
		# path -- gated on Config.Kind 'srg' AND a non-null Credential -- never fires here.
		if ($Kind -eq 'srg') {
			# HDF-only srg target (the VCF 9.x NSX baselines have no DISA STIG/CKL): mirror
			# the ssh/srg path -- no CKL staging, no benchmark correction, no STIG Manager
			# watcher. The worker (module.scan.ps1, Config.Kind='srg') optionally attests the
			# HDF, then writes a `saf view summary` artifact. TargetType 'srg-<component>'
			# matches Get-CatalogReportGroupMap's srg key so the target routes to the srg
			# report-group partition.
			$InspecFileName = "VMware_NSX_$($DisplayVersion)_SRG_$($ShortComp)_Inspec_$($ShortName)_$Stamp"
			$ReportFile = Join-Path $NsxReportPath "$InspecFileName.json"
			$SummaryFile = Join-Path $NsxReportPath "$InspecFileName.summary.yml"

			$Attest = New-AttestStep -ReportFile $ReportFile -AttestTemplate $AttestTemplate `
				-AttestReportFile (Join-Path $NsxReportPath "$InspecFileName.attest.json")

			$TargetItem = New-StigTarget -TargetType "srg-$CompKey" -Kind 'srg' -AttestRequired $Attest.AttestRequired `
				-Name "$ShortName-$ShortComp" -Fqdn $Manager -Ip $IP -Mac "" `
				-ReportFile $ReportFile -AttestReportFile $Attest.AttestReportFile `
				-SummaryFile $SummaryFile -AttestTemplate $AttestTemplate -InspecProfile $CompProfile `
				-InspecArgs "`"$CompProfile`" --input-file `"$GeneratedInputs`" --reporter=json:`"$ReportFile`" --show-progress --enhanced-outcomes$WaiverArg" `
				-SafAttestArgs $Attest.SafAttestArgs
		} else {
			# stig (NSX 4.x) CKL path -- unchanged: attest -> hdf2ckl -> benchmark
			# correction -> STIG Manager watcher.
			$InspecFileName = "VMware_NSX_$($DisplayVersion)_STIG_$($ShortComp)_Inspec_$($ShortName)_$Stamp"
			$ReportFile = Join-Path $NsxReportPath "$InspecFileName.json"

			$CklFileName = "VMware_NSX_$($DisplayVersion)_STIG_$($ShortComp)_CKL_$($ShortName)_$Stamp"
			$CklPath = Join-Path $DirStructure.NsxCklPath.FullName "$CklFileName.ckl"
			$CklStagingPath = Join-Path $NsxReportPath "$CklFileName.ckl"

			$Attest = New-AttestStep -ReportFile $ReportFile -AttestTemplate $AttestTemplate `
				-AttestReportFile (Join-Path $NsxReportPath "$InspecFileName.attest.json")

			# NSX Manager has no single MAC; pass -Mac '' so --mac is omitted.
			$CklCommandArgs = New-CklConvertArgs -ConvertInputFile $Attest.ConvertInputFile -CklStagingPath $CklStagingPath -Hostname $ShortName -Fqdn $Manager -Ip $IP -Mac ''

			$TargetItem = New-StigTarget -TargetType $CompKey -Kind 'stig' -AttestRequired $Attest.AttestRequired `
				-Name "$ShortName-$ShortComp" -Fqdn $Manager -Ip $IP -Mac "" `
				-ReportFile $ReportFile -AttestReportFile $Attest.AttestReportFile `
				-CklPath $CklPath -CklStagingPath $CklStagingPath `
				-AttestTemplate $AttestTemplate -InspecProfile $CompProfile `
				-InspecArgs "`"$CompProfile`" --input-file `"$GeneratedInputs`" --reporter=json:`"$ReportFile`" --show-progress --enhanced-outcomes$WaiverArg" `
				-SafAttestArgs $Attest.SafAttestArgs -SafConvertArgs $CklCommandArgs
		}

		Write-Log "Built NSX Target: $ShortComp for $Manager" -Severity 'Debug' @WriteLogParams
		$TargetsList.Enqueue($TargetItem)
	}
}

#endregion
