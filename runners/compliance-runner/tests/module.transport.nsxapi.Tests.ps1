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

# Issue #1355: direct Pester coverage for module.transport.nsxapi.ps1 (imported,
# owner-authored vmware-stig-docker sibling code -- see
# runners/compliance-runner/powershell/README.md). Dot-sources the module directly from
# the runner tree. This file's DEPENDENCIES doc comment names module.common.ps1,
# module.catalog.ps1, module.config.ps1, module.attestation.ps1 and
# module.parallelism.ps1 -- none of those (besides module.common.ps1, dot-sourced too)
# are imported into this runner tree, so their functions are stubbed here and Mocked
# per test; Invoke-WebRequest (the real network call) is likewise stubbed/mocked so no
# test ever makes a live HTTP request.
#
# All hostnames/IPs/tokens in this suite are invented (AGENTS.md sanitization): RFC 5737
# addresses and .example.internal names, never real infrastructure or credentials.
#
# Run: pwsh -NoProfile -Command "Invoke-Pester -Path <this file> -CI"

BeforeAll {
	function global:Get-LogSplat { param([Parameter(Position = 0)][AllowNull()][AllowEmptyString()][string]$Source) if ($Source) { return @{ Source = $Source } }; return @{} }
	function global:Write-Log { param([Parameter(Mandatory, Position = 0)][string]$Message, [string]$Severity = 'Info', [object]$LogQueue, [string]$Source, [datetime]$Timestamp) }
	function global:Invoke-WebRequest { param([string]$Uri, [string]$Method, $Body, [string]$ContentType, [switch]$SkipCertificateCheck, [int]$TimeoutSec, [string]$ErrorAction) throw 'Invoke-WebRequest stub called without a Mock' }

	# module.catalog.ps1 / module.config.ps1 / module.attestation.ps1 surfaces
	# Build-NsxTransportTargets depends on but that are not imported into this runner tree.
	function global:Get-CatalogKind { param($Product, $Version, $Kind) }
	function global:Get-CatalogComponent { param($Product, $Version, $Kind) }
	function global:Get-CatalogRelease { param($Product, $Version) }
	function global:Resolve-CatalogProfilePath { param($ProfileBase, $Version, $ProfileSubpath) }
	function global:Resolve-ScanInputFile { param($SiteInput, $ExampleSubpath, $ProfileBase, $Version) }
	function global:Get-SiteTargetName { param($Target) }
	function global:Resolve-Credential { param($Ref) }
	function global:Resolve-AttestationFile { param($SiteAttestation, $Label, $Source) }

	. (Join-Path $PSScriptRoot '../powershell/module.common.ps1')
	. (Join-Path $PSScriptRoot '../powershell/module.transport.nsxapi.ps1')
}

AfterAll {
	# Remove the global stand-ins so they do not leak into later test files in the
	# same Pester run (see review note on PR #1717 round 1).
	Remove-Item Function:\global:Get-LogSplat -ErrorAction SilentlyContinue
	Remove-Item Function:\global:Write-Log -ErrorAction SilentlyContinue
	Remove-Item Function:\global:Invoke-WebRequest -ErrorAction SilentlyContinue
	Remove-Item Function:\global:Get-CatalogKind -ErrorAction SilentlyContinue
	Remove-Item Function:\global:Get-CatalogComponent -ErrorAction SilentlyContinue
	Remove-Item Function:\global:Get-CatalogRelease -ErrorAction SilentlyContinue
	Remove-Item Function:\global:Resolve-CatalogProfilePath -ErrorAction SilentlyContinue
	Remove-Item Function:\global:Resolve-ScanInputFile -ErrorAction SilentlyContinue
	Remove-Item Function:\global:Get-SiteTargetName -ErrorAction SilentlyContinue
	Remove-Item Function:\global:Resolve-Credential -ErrorAction SilentlyContinue
	Remove-Item Function:\global:Resolve-AttestationFile -ErrorAction SilentlyContinue
}

Describe 'Get-NsxSessionToken' {
	It 'returns the token and cookie parsed from the response headers' {
		Mock Invoke-WebRequest {
			[pscustomobject]@{
				Headers = @{
					'X-XSRF-TOKEN' = 'abc123token'
					'Set-Cookie'   = 'JSESSIONID=deadbeef1234; Path=/; Secure'
				}
			}
		}
		$Cred = [pscredential]::new('admin', (ConvertTo-SecureString 'hunter2' -AsPlainText -Force))
		$Result = Get-NsxSessionToken -Manager 'nsxmgr-01.example.internal' -Credential $Cred
		$Result.Token | Should -Be 'abc123token'
		$Result.Cookie | Should -Be 'JSESSIONID=deadbeef1234'
	}

	It 'throws when no X-XSRF-TOKEN header comes back' {
		Mock Invoke-WebRequest { [pscustomobject]@{ Headers = @{ 'Set-Cookie' = 'JSESSIONID=x' } } }
		$Cred = [pscredential]::new('admin', (ConvertTo-SecureString 'hunter2' -AsPlainText -Force))
		{ Get-NsxSessionToken -Manager 'nsxmgr-01.example.internal' -Credential $Cred } | Should -Throw '*X-XSRF-TOKEN*'
	}

	It 'throws when no JSESSIONID cookie comes back' {
		Mock Invoke-WebRequest { [pscustomobject]@{ Headers = @{ 'X-XSRF-TOKEN' = 'abc123token' } } }
		$Cred = [pscredential]::new('admin', (ConvertTo-SecureString 'hunter2' -AsPlainText -Force))
		{ Get-NsxSessionToken -Manager 'nsxmgr-01.example.internal' -Credential $Cred } | Should -Throw '*JSESSIONID*'
	}
}

Describe 'Get-NsxYamlTopLevelValue' {
	It 'returns $null for blank YAML text' {
		Get-NsxYamlTopLevelValue -YamlText '' -KeyName 'nsxManager' | Should -BeNullOrEmpty
	}

	It 'reads an unquoted top-level scalar value' {
		Get-NsxYamlTopLevelValue -YamlText "nsxManager: nsxmgr-01.example.internal`nother: x" -KeyName 'nsxManager' | Should -Be 'nsxmgr-01.example.internal'
	}

	It 'strips one layer of surrounding quotes' {
		Get-NsxYamlTopLevelValue -YamlText "nsxManager: 'nsxmgr-01.example.internal'" -KeyName 'nsxManager' | Should -Be 'nsxmgr-01.example.internal'
	}

	It 'does not match an indented (nested) key of the same name' {
		Get-NsxYamlTopLevelValue -YamlText "otherBlock:`n  nsxManager: nested-value" -KeyName 'nsxManager' | Should -BeNullOrEmpty
	}
}

Describe 'Remove-NsxAuthInputKeys' {
	It 'returns the input unchanged when it is blank' {
		Remove-NsxAuthInputKeys -InputsText '' -KeyNames @('nsxManager') | Should -Be ''
	}

	It 'removes a top-level scalar key and keeps everything else' {
		$Result = Remove-NsxAuthInputKeys -InputsText "nsxManager: old-host`nother: kept" -KeyNames @('nsxManager', 'sessionToken', 'sessionCookieId')
		$Result | Should -Not -Match 'nsxManager'
		$Result | Should -Match 'other: kept'
	}

	It 'removes indented continuation lines of a stripped block value' {
		$Text = "sessionToken:`n  block-line-1`n  block-line-2`nother: kept"
		$Result = Remove-NsxAuthInputKeys -InputsText $Text -KeyNames @('sessionToken')
		$Result | Should -Not -Match 'block-line'
		$Result | Should -Match 'other: kept'
	}

	It 'treats a blank line inside a stripped block as part of the block, not the end of it' {
		$Text = "sessionToken:`n  block-line-1`n`n  block-line-2`nother: kept"
		$Result = Remove-NsxAuthInputKeys -InputsText $Text -KeyNames @('sessionToken')
		$Result | Should -Not -Match 'block-line'
		$Result | Should -Match 'other: kept'
	}

	It 'does not touch a same-named key nested under another mapping' {
		$Text = "otherBlock:`n  sessionToken: nested`nkept: yes"
		$Result = Remove-NsxAuthInputKeys -InputsText $Text -KeyNames @('sessionToken')
		$Result | Should -Match 'nested'
	}
}

Describe 'Build-NsxTransportTargets' {
	BeforeEach {
		$Script:TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "waypoint-test-$([guid]::NewGuid().ToString('N'))"
		New-Item -ItemType Directory -Path $Script:TempDir -Force | Out-Null
		$Script:ProfileDir = Join-Path $Script:TempDir 'profile'
		New-Item -ItemType Directory -Path $Script:ProfileDir -Force | Out-Null
		Clear-ScanSkips
		$Script:RuntimeConfig.ConfigBasePath = $Script:TempDir
	}
	AfterEach { Remove-Item -Path $Script:TempDir -Recurse -Force -ErrorAction SilentlyContinue }

	It 'skips and records the row when the catalog has no matching product/version/kind' {
		Mock Get-CatalogComponent { $null }
		$Target = [pscustomobject]@{ product = 'nsx'; version = '4-x'; kind = 'stig'; connection = [pscustomobject]@{ host = 'nsxmgr-01.example.internal' } }
		$Queue = [System.Collections.Generic.Queue[PSObject]]::new()
		Build-NsxTransportTargets -Target $Target -DirStructure ([pscustomobject]@{}) -ReportPath $Script:TempDir -TargetsList $Queue -WarningAction SilentlyContinue
		$Queue.Count | Should -Be 0
	}

	It 'skips and records the row when connection.host is blank' {
		Mock Get-CatalogComponent { [pscustomobject]@{ 'nsx-component-mgmt' = [pscustomobject]@{ profileSubpath = 'mgmt' } } }
		Mock Get-SiteTargetName { 'row1' }
		$Target = [pscustomobject]@{ product = 'nsx'; version = '4-x'; kind = 'stig'; connection = [pscustomobject]@{ host = '' } }
		$Queue = [System.Collections.Generic.Queue[PSObject]]::new()
		Build-NsxTransportTargets -Target $Target -DirStructure ([pscustomobject]@{}) -ReportPath $Script:TempDir -TargetsList $Queue -WarningAction SilentlyContinue
		$Queue.Count | Should -Be 0
		(Get-ScanSkips)[0].Family | Should -Be 'nsx-row1'
	}

	It 'skips and records the row when the manager is unreachable' {
		Mock Get-CatalogComponent { [pscustomobject]@{ 'nsx-component-mgmt' = [pscustomobject]@{ profileSubpath = 'mgmt' } } }
		Mock Get-SiteTargetName { 'row1' }
		Mock Test-TargetReachable { $false }
		$Target = [pscustomobject]@{ product = 'nsx'; version = '4-x'; kind = 'stig'; connection = [pscustomobject]@{ host = 'nsxmgr-01.example.internal' } }
		$Queue = [System.Collections.Generic.Queue[PSObject]]::new()
		Build-NsxTransportTargets -Target $Target -DirStructure ([pscustomobject]@{}) -ReportPath $Script:TempDir -TargetsList $Queue -WarningAction SilentlyContinue
		$Queue.Count | Should -Be 0
		(Get-ScanSkips)[0].Reason | Should -Match 'not reachable'
	}

	It 'skips and records the row when the session token call fails' {
		Mock Get-CatalogComponent { [pscustomobject]@{ 'nsx-component-mgmt' = [pscustomobject]@{ profileSubpath = 'mgmt' } } }
		Mock Get-SiteTargetName { 'row1' }
		Mock Test-TargetReachable { $true }
		Mock Resolve-Credential { [pscredential]::new('admin', (ConvertTo-SecureString 'x' -AsPlainText -Force)) }
		Mock Get-NsxSessionToken { throw 'auth failed' }
		$Target = [pscustomobject]@{ product = 'nsx'; version = '4-x'; kind = 'stig'; connection = [pscustomobject]@{ host = 'nsxmgr-01.example.internal' } }
		$Queue = [System.Collections.Generic.Queue[PSObject]]::new()
		Build-NsxTransportTargets -Target $Target -DirStructure ([pscustomobject]@{}) -ReportPath $Script:TempDir -TargetsList $Queue -WarningAction SilentlyContinue
		$Queue.Count | Should -Be 0
		(Get-ScanSkips)[0].Reason | Should -Match 'session token failed'
	}

	It 'builds one stig-kind target per catalog component, with the generated inputs auth block written to disk' {
		Mock Get-CatalogKind { [pscustomobject]@{ profileBase = 'nsx-base' } }
		Mock Get-CatalogComponent { [pscustomobject]@{ 'nsx-component-mgmt' = [pscustomobject]@{ profileSubpath = 'mgmt'; exampleInput = $null } } }
		Mock Get-CatalogRelease { 'v1r1-stig' }
		Mock Get-SiteTargetName { 'row1' }
		Mock Test-TargetReachable { $true }
		Mock Get-NsxSessionToken { [pscustomobject]@{ Token = 'tok123'; Cookie = 'JSESSIONID=abc' } }
		Mock Resolve-Credential { [pscredential]::new('admin', (ConvertTo-SecureString 'x' -AsPlainText -Force)) }
		Mock Resolve-CatalogProfilePath { $Script:ProfileDir }
		Mock Resolve-ScanInputFile { '' }
		Mock Resolve-AttestationFile { $null }

		$Target = [pscustomobject]@{ product = 'nsx'; version = '4-x'; kind = 'stig'; connection = [pscustomobject]@{ host = 'nsxmgr-01.example.internal' } }
		$Queue = [System.Collections.Generic.Queue[PSObject]]::new()
		$DirStructure = [pscustomobject]@{ NsxCklPath = [pscustomobject]@{ FullName = $Script:TempDir } }

		Build-NsxTransportTargets -Target $Target -DirStructure $DirStructure -ReportPath $Script:TempDir -TargetsList $Queue -WarningAction SilentlyContinue

		$Queue.Count | Should -Be 1
		$Built = $Queue.Dequeue()
		$Built.TargetType | Should -Be 'nsx-component-mgmt'
		$Built.Config.Kind | Should -Be 'stig'
		$Built.Commands.InspecArgs | Should -Match 'nsx-inputs-mgmt\.generated\.yml'

		$GeneratedFile = Get-ChildItem -Path (Join-Path $Script:TempDir 'nsx') -Recurse -Filter '*.generated.yml' | Select-Object -First 1
		$GeneratedContent = Get-Content -Path $GeneratedFile.FullName -Raw
		$GeneratedContent | Should -Match "nsxManager: 'nsxmgr-01.example.internal'"
		$GeneratedContent | Should -Match "sessionToken: 'tok123'"
	}

	It 'builds an HDF-only srg target with a summary file (no CKL) for the srg kind' {
		Mock Get-CatalogKind { [pscustomobject]@{ profileBase = 'nsx-base' } }
		Mock Get-CatalogComponent { [pscustomobject]@{ 'nsx-component-manager' = [pscustomobject]@{ profileSubpath = 'manager'; exampleInput = $null } } }
		Mock Get-CatalogRelease { 'Y26M01-srg' }
		Mock Get-SiteTargetName { 'row1' }
		Mock Test-TargetReachable { $true }
		Mock Get-NsxSessionToken { [pscustomobject]@{ Token = 'tok123'; Cookie = 'JSESSIONID=abc' } }
		Mock Resolve-Credential { [pscredential]::new('admin', (ConvertTo-SecureString 'x' -AsPlainText -Force)) }
		Mock Resolve-CatalogProfilePath { $Script:ProfileDir }
		Mock Resolve-ScanInputFile { '' }
		Mock Resolve-AttestationFile { $null }

		$Target = [pscustomobject]@{ product = 'nsx.9-x'; version = '9-x'; kind = 'srg'; connection = [pscustomobject]@{ host = 'nsxmgr-02.example.internal' } }
		$Queue = [System.Collections.Generic.Queue[PSObject]]::new()
		$DirStructure = [pscustomobject]@{ NsxCklPath = [pscustomobject]@{ FullName = $Script:TempDir } }

		Build-NsxTransportTargets -Target $Target -DirStructure $DirStructure -ReportPath $Script:TempDir -TargetsList $Queue -WarningAction SilentlyContinue

		$Built = $Queue.Dequeue()
		$Built.TargetType | Should -Be 'srg-nsx-component-manager'
		$Built.Config.Kind | Should -Be 'srg'
		$Built.Paths.SummaryFile | Should -Match '\.summary\.yml$'
		$Built.PSObject.Properties.Name | Should -Not -Contain 'CklPath'
	}

	It 'skips a component whose resolved profile directory does not exist' {
		Mock Get-CatalogKind { [pscustomobject]@{ profileBase = 'nsx-base' } }
		Mock Get-CatalogComponent { [pscustomobject]@{ 'nsx-component-missing' = [pscustomobject]@{ profileSubpath = 'missing'; exampleInput = $null } } }
		Mock Get-CatalogRelease { 'v1r1-stig' }
		Mock Get-SiteTargetName { 'row1' }
		Mock Test-TargetReachable { $true }
		Mock Get-NsxSessionToken { [pscustomobject]@{ Token = 'tok123'; Cookie = 'JSESSIONID=abc' } }
		Mock Resolve-Credential { [pscredential]::new('admin', (ConvertTo-SecureString 'x' -AsPlainText -Force)) }
		Mock Resolve-CatalogProfilePath { Join-Path $Script:TempDir 'does-not-exist' }

		$Target = [pscustomobject]@{ product = 'nsx'; version = '4-x'; kind = 'stig'; connection = [pscustomobject]@{ host = 'nsxmgr-03.example.internal' } }
		$Queue = [System.Collections.Generic.Queue[PSObject]]::new()
		$DirStructure = [pscustomobject]@{ NsxCklPath = [pscustomobject]@{ FullName = $Script:TempDir } }

		Build-NsxTransportTargets -Target $Target -DirStructure $DirStructure -ReportPath $Script:TempDir -TargetsList $Queue -WarningAction SilentlyContinue
		$Queue.Count | Should -Be 0
	}
}
