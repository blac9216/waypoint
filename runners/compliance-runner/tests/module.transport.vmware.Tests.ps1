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

# Issue #1355: direct Pester coverage for module.transport.vmware.ps1 (imported,
# owner-authored vmware-stig-docker sibling code -- see
# runners/compliance-runner/powershell/README.md). Dot-sources the module directly from
# the runner tree. This file's DEPENDENCIES doc comment names module.common.ps1 (dot-
# sourced too), module.catalog.ps1, module.config.ps1 and module.attestation.ps1 -- none
# of the latter three are imported into this runner tree, so their functions are stubbed
# here and Mocked per test. PowerCLI (VMware.VimAutomation.Core) is not installed in this
# test environment, so every PowerCLI cmdlet these functions call (Connect-VIServer,
# Get-VM, Get-VMHost, Get-VMHostNetworkAdapter, Get-NetworkAdapter, Get-Credential) is
# likewise stubbed here before Pester can Mock it -- Pester can only mock a command that
# already exists.
#
# All hostnames/IPs/credentials in this suite are invented (AGENTS.md sanitization): RFC
# 5737 addresses and .example.internal names, never real infrastructure.
#
# Run: pwsh -NoProfile -Command "Invoke-Pester -Path <this file> -CI"

BeforeAll {
	function global:Get-LogSplat { param([Parameter(Position = 0)][AllowNull()][AllowEmptyString()][string]$Source) if ($Source) { return @{ Source = $Source } }; return @{} }
	function global:Write-Log { param([Parameter(Mandatory, Position = 0)][string]$Message, [string]$Severity = 'Info', [object]$LogQueue, [string]$Source, [datetime]$Timestamp) }

	# module.catalog.ps1 / module.config.ps1 / module.attestation.ps1 surfaces these
	# functions depend on but that are not imported into this runner tree.
	function global:Get-CatalogKind { param($Product, $Version, $Kind) }
	function global:Get-CatalogComponent { param($Product, $Version, $Kind) }
	function global:Get-CatalogRelease { param($Product, $Version) }
	function global:Resolve-CatalogProfilePath { param($ProfileBase, $Version, $ProfileSubpath, $ProfilesBase) }
	function global:Resolve-ScanInputFile { param($SiteInput, $ExampleSubpath, $ProfileBase, $Version, $ProfilesBase) '' }
	function global:Resolve-Credential { param($Ref) }
	function global:Get-AttestationPaths { param($Attestation) [pscustomobject]@{} }

	# PowerCLI cmdlet stand-ins (VMware.VimAutomation.Core is not installed here) -- Pester
	# can only Mock a command that already exists.
	function global:Connect-VIServer { param($Server, [switch]$AllLinked, $Credential, $Protocol) }
	function global:Get-Credential { param([string]$Message, [string]$UserName) }
	function global:Get-VM { param($Server, [string]$ErrorAction) }
	function global:Get-VMHost { param($Server, [string]$ErrorAction) }
	function global:Get-VMHostNetworkAdapter { param($VMHost, [string]$Name, [string]$ErrorAction) }
	function global:Get-NetworkAdapter { param($VM, [string]$ErrorAction) }

	. (Join-Path $PSScriptRoot '../powershell/module.common.ps1')
	. (Join-Path $PSScriptRoot '../powershell/module.transport.vmware.ps1')
}

Describe 'Connect-StigVIServer' {
	# Connect-StigVIServer's session-reuse check reads this PowerCLI global directly;
	# reset it every test so no test's fake sessions leak into the next.
	BeforeEach { $Global:DefaultVIServers = @() }

	It 'connects fresh (AllLinked) and returns the session/credential/cleanup handle' {
		$VSCred = [pscredential]::new('svc-vsphere-admin', (ConvertTo-SecureString 'x' -AsPlainText -Force))
		$VCSACred = [pscredential]::new('root', (ConvertTo-SecureString 'y' -AsPlainText -Force))
		Mock Connect-VIServer { @([pscustomobject]@{ Name = 'vcsa-01.example.internal' }) }

		$Result = Connect-StigVIServer -VCenter 'vcsa-01.example.internal' -VSphereCredential $VSCred -VCSACredential $VCSACred
		$Result.DisconnectAtCleanup | Should -BeTrue
		$Result.Sessions.Count | Should -Be 1
		$Result.VCSACredential | Should -Be $VCSACred
		Should -Invoke Connect-VIServer -Times 1
	}

	It 'reuses an existing session by exact name match instead of reconnecting' {
		$Global:DefaultVIServers = @([pscustomobject]@{ Name = 'vcsa-02.example.internal' })
		Mock Connect-VIServer { throw 'should not reconnect' }

		$Result = Connect-StigVIServer -VCenter 'vcsa-02.example.internal'
		$Result.Sessions.Count | Should -Be 1
		$Result.DisconnectAtCleanup | Should -BeFalse
		Should -Invoke Connect-VIServer -Times 0
	}

	It 'never resolves or prompts for a VCSA credential when -SkipVCSACredential is set' {
		Mock Connect-VIServer { @([pscustomobject]@{ Name = 'vcsa-03.example.internal' }) }
		Mock Get-Credential { throw 'must not prompt' }
		$VSCred = [pscredential]::new('svc-vsphere-admin', (ConvertTo-SecureString 'x' -AsPlainText -Force))

		$Result = Connect-StigVIServer -VCenter 'vcsa-03.example.internal' -VSphereCredential $VSCred -SkipVCSACredential
		$Result.VCSACredential | Should -BeNullOrEmpty
	}

	It 'throws a wrapped error when Connect-VIServer itself throws' {
		Mock Connect-VIServer { throw 'connection refused' }
		$VSCred = [pscredential]::new('svc-vsphere-admin', (ConvertTo-SecureString 'x' -AsPlainText -Force))
		{ Connect-StigVIServer -VCenter 'vcsa-04.example.internal' -VSphereCredential $VSCred -SkipVCSACredential } | Should -Throw '*Failed to establish vCenter connection*'
	}

	It 'throws when Connect-VIServer returns no sessions at all' {
		Mock Connect-VIServer { @() }
		$VSCred = [pscredential]::new('svc-vsphere-admin', (ConvertTo-SecureString 'x' -AsPlainText -Force))
		{ Connect-StigVIServer -VCenter 'vcsa-05.example.internal' -VSphereCredential $VSCred -SkipVCSACredential } | Should -Throw '*No vCenter sessions could be established*'
	}
}

Describe 'Get-InspecProfilePaths' {
	BeforeEach {
		$Script:ProfilesBase = Join-Path ([System.IO.Path]::GetTempPath()) "waypoint-test-$([guid]::NewGuid().ToString('N'))"
		New-Item -ItemType Directory -Path $Script:ProfilesBase -Force | Out-Null
		foreach ($Dir in @('vm', 'esxi', 'vcenter', 'sso')) { New-Item -ItemType Directory -Path (Join-Path $Script:ProfilesBase $Dir) -Force | Out-Null }
	}
	AfterEach { Remove-Item -Path $Script:ProfilesBase -Recurse -Force -ErrorAction SilentlyContinue }

	It 'throws when the profiles base path does not exist' {
		{ Get-InspecProfilePaths -ProfilesBase '/does/not/exist' -Version '8-0' } | Should -Throw '*does not exist*'
	}

	It 'throws when the catalog has no components for the requested product/version/kind' {
		Mock Get-CatalogComponent { $null }
		{ Get-InspecProfilePaths -ProfilesBase $Script:ProfilesBase -Version '8-0' } | Should -Throw '*No catalog entry*'
	}

	It 'resolves and validates every profile path, returning the Kind it was asked for' {
		Mock Get-CatalogKind { [pscustomobject]@{ profileBase = 'vsphere-base' } }
		Mock Get-CatalogComponent {
			[pscustomobject]@{
				vm      = [pscustomobject]@{ profileSubpath = 'vm'; exampleInput = $null }
				esxi    = [pscustomobject]@{ profileSubpath = 'esxi'; exampleInput = $null }
				vcenter = [pscustomobject]@{ profileSubpath = 'vcenter'; exampleInput = $null }
				'vcsa-component-sso' = [pscustomobject]@{ profileSubpath = 'sso' }
			}
		}
		Mock Get-CatalogRelease { 'v2r3-stig' }
		Mock Resolve-CatalogProfilePath {
			param($ProfileBase, $Version, $ProfileSubpath, $ProfilesBase)
			Join-Path $ProfilesBase $ProfileSubpath
		}

		$Result = Get-InspecProfilePaths -ProfilesBase $Script:ProfilesBase -Version '8-0'
		$Result.Kind | Should -Be 'stig'
		$Result.VMProfile | Should -Be (Join-Path $Script:ProfilesBase 'vm')
		$Result.VCSAStigs['sso'] | Should -Be (Join-Path $Script:ProfilesBase 'sso')
	}

	It 'throws when a resolved profile path does not exist on disk' {
		Mock Get-CatalogKind { [pscustomobject]@{ profileBase = 'vsphere-base' } }
		Mock Get-CatalogComponent {
			[pscustomobject]@{
				vm      = [pscustomobject]@{ profileSubpath = 'vm'; exampleInput = $null }
				esxi    = [pscustomobject]@{ profileSubpath = 'esxi'; exampleInput = $null }
				vcenter = [pscustomobject]@{ profileSubpath = 'missing'; exampleInput = $null }
			}
		}
		Mock Get-CatalogRelease { 'v2r3-stig' }
		Mock Resolve-CatalogProfilePath { param($ProfileBase, $Version, $ProfileSubpath, $ProfilesBase) Join-Path $ProfilesBase $ProfileSubpath }

		{ Get-InspecProfilePaths -ProfilesBase $Script:ProfilesBase -Version '8-0' } | Should -Throw '*Required InSpec profile directory not found*'
	}
}

Describe 'Connect-VsphereTransportRow' {
	It 'resolves the vCenter from the CLI override over the site row, and connects' {
		Mock Get-InspecProfilePaths { [pscustomobject]@{ Kind = 'stig' } }
		Mock Get-AttestationPaths { [pscustomobject]@{} }
		Mock Connect-StigVIServer { [pscustomobject]@{ Sessions = @('fake-session'); VSphereCredential = $null; VCSACredential = $null; DisconnectAtCleanup = $true } }

		$Row = [pscustomobject]@{ connection = [pscustomobject]@{ host = 'row-vcenter.example.internal' } }
		$Result = Connect-VsphereTransportRow -SiteTargetRow $Row -TargetVCenter 'override-vcenter.example.internal'

		Should -Invoke Connect-StigVIServer -Times 1 -ParameterFilter { $VCenter -eq 'override-vcenter.example.internal' }
		$Result.DisplayVersion | Should -Be $Script:RuntimeConfig.VSphereVersion
	}

	It 'falls back to the site row connection.host when no CLI override is given' {
		Mock Get-InspecProfilePaths { [pscustomobject]@{ Kind = 'stig' } }
		Mock Get-AttestationPaths { [pscustomobject]@{} }
		Mock Connect-StigVIServer { [pscustomobject]@{ Sessions = @('fake-session'); VSphereCredential = $null; VCSACredential = $null; DisconnectAtCleanup = $true } }

		$Row = [pscustomobject]@{ connection = [pscustomobject]@{ host = 'row-vcenter.example.internal' } }
		Connect-VsphereTransportRow -SiteTargetRow $Row | Out-Null

		Should -Invoke Connect-StigVIServer -Times 1 -ParameterFilter { $VCenter -eq 'row-vcenter.example.internal' }
	}

	It 'resolves credentials from the row vault refs when no CLI override is supplied' {
		Mock Get-InspecProfilePaths { [pscustomobject]@{ Kind = 'stig' } }
		Mock Get-AttestationPaths { [pscustomobject]@{} }
		Mock Connect-StigVIServer { [pscustomobject]@{ Sessions = @('fake-session'); VSphereCredential = $null; VCSACredential = $null; DisconnectAtCleanup = $true } }
		Mock Resolve-Credential { [pscredential]::new('resolved', (ConvertTo-SecureString 'x' -AsPlainText -Force)) }

		$Row = [pscustomobject]@{ connection = [pscustomobject]@{ host = 'row-vcenter.example.internal' }; credentialRef = 'vault://vsphere' }
		Connect-VsphereTransportRow -SiteTargetRow $Row | Out-Null

		Should -Invoke Connect-StigVIServer -Times 1 -ParameterFilter { $VSphereCredential.UserName -eq 'resolved' }
	}
}

Describe 'Get-VCenterApplianceHostInfo' {
	BeforeEach { $Global:DefaultVIServers = @() }

	It 'accepts an identity-verified candidate matched by Guest.HostName' {
		$VCSession = [pscustomobject]@{ Name = 'vcsa-06.example.internal' }
		$Candidate = [pscustomobject]@{
			Name  = 'vcsa-06-appliance'
			Guest = [pscustomobject]@{ HostName = 'vcsa-06.example.internal'; IPAddress = @('198.51.100.6'); Nics = $null }
		}
		Mock Get-VM { @($Candidate) }
		Mock Resolve-HostIpViaDns { '' }

		$Result = Get-VCenterApplianceHostInfo -VCSession $VCSession -VISessions @($VCSession)
		$Result.Name | Should -Be 'vcsa-06-appliance'
		$Result.IP | Should -Be '198.51.100.6'
	}

	It 'rejects an unverified name-only match and falls back to the vCenter session name' {
		$VCSession = [pscustomobject]@{ Name = 'vcsa-07.example.internal' }
		$Candidate = [pscustomobject]@{
			Name  = 'vcsa-07-decoy'
			Guest = [pscustomobject]@{ HostName = 'unrelated-host.example.internal'; IPAddress = @('198.51.100.99'); Nics = $null }
		}
		Mock Get-VM { @($Candidate) }
		Mock Resolve-HostIpViaDns { '198.51.100.7' }

		$Result = Get-VCenterApplianceHostInfo -VCSession $VCSession -VISessions @($VCSession) -WarningAction SilentlyContinue
		$Result.Name | Should -Be 'vcsa-07.example.internal'
		$Result.IP | Should -Be '198.51.100.7'
		$Result.FQDN | Should -Be 'vcsa-07.example.internal'
	}

	It 'skips a candidate with no Guest data and falls back cleanly' {
		$VCSession = [pscustomobject]@{ Name = 'vcsa-08.example.internal' }
		$Candidate = [pscustomobject]@{ Name = 'vcsa-08-off'; Guest = $null }
		Mock Get-VM { @($Candidate) }
		Mock Resolve-HostIpViaDns { '' }

		$Result = Get-VCenterApplianceHostInfo -VCSession $VCSession -VISessions @($VCSession) -WarningAction SilentlyContinue
		$Result.Name | Should -Be 'vcsa-08.example.internal'
	}
}

Describe 'Build-VCenterTarget' {
	BeforeEach {
		$Global:DefaultVIServers = @()
		$Script:ReportRoot = Join-Path ([System.IO.Path]::GetTempPath()) "waypoint-test-$([guid]::NewGuid().ToString('N'))"
		New-Item -ItemType Directory -Path $Script:ReportRoot -Force | Out-Null
	}
	AfterEach { Remove-Item -Path $Script:ReportRoot -Recurse -Force -ErrorAction SilentlyContinue }

	It 'builds a stig-kind vCenter target with CKL paths' {
		Mock Get-VM { @() }
		Mock Resolve-HostIpViaDns { '198.51.100.20' }
		$VCSession = [pscustomobject]@{ Name = 'vcsa-09.example.internal' }
		$Profiles = [pscustomobject]@{ Kind = 'stig'; VCenterProfile = '/profiles/vcenter'; VCenterInputsFile = '' }
		$Attestations = [pscustomobject]@{ VCenterTemplate = $null }
		$DirStructure = [pscustomobject]@{ VCenterCklPath = [pscustomobject]@{ FullName = $Script:ReportRoot } }
		$Queue = [System.Collections.Generic.Queue[PSObject]]::new()

		Build-VCenterTarget -VCSession $VCSession -VISessions @($VCSession) -Profiles $Profiles -Attestations $Attestations -DirStructure $DirStructure -ReportPath $Script:ReportRoot -VSphereVersion '8.0' -TargetsList $Queue -WarningAction SilentlyContinue

		$Queue.Count | Should -Be 1
		$Built = $Queue.Dequeue()
		$Built.TargetType | Should -Be 'vcenter'
		$Built.Paths.CklPath | Should -Match '\.ckl$'
	}

	It 'builds an HDF-only srg-vcenter target with a summary file when Profiles.Kind is srg' {
		Mock Get-VM { @() }
		Mock Resolve-HostIpViaDns { '198.51.100.21' }
		$VCSession = [pscustomobject]@{ Name = 'vcsa-10.example.internal' }
		$Profiles = [pscustomobject]@{ Kind = 'srg'; VCenterProfile = '/profiles/vcenter'; VCenterInputsFile = '' }
		$Attestations = [pscustomobject]@{ VCenterTemplate = $null }
		$DirStructure = [pscustomobject]@{}
		$Queue = [System.Collections.Generic.Queue[PSObject]]::new()

		Build-VCenterTarget -VCSession $VCSession -VISessions @($VCSession) -Profiles $Profiles -Attestations $Attestations -DirStructure $DirStructure -ReportPath $Script:ReportRoot -VSphereVersion '9.0' -TargetsList $Queue -WarningAction SilentlyContinue

		$Built = $Queue.Dequeue()
		$Built.TargetType | Should -Be 'srg-vcenter'
		$Built.Paths.SummaryFile | Should -Match '\.summary\.yml$'
	}
}

Describe 'Build-VCSATarget' {
	BeforeEach {
		$Global:DefaultVIServers = @()
		$Script:ReportRoot = Join-Path ([System.IO.Path]::GetTempPath()) "waypoint-test-$([guid]::NewGuid().ToString('N'))"
		New-Item -ItemType Directory -Path $Script:ReportRoot -Force | Out-Null
		Clear-ScanSkips
	}
	AfterEach { Remove-Item -Path $Script:ReportRoot -Recurse -Force -ErrorAction SilentlyContinue }

	It 'throws when no VCSA credential is resolved' {
		$VCSession = [pscustomobject]@{ Name = 'vcsa-11.example.internal' }
		{ Build-VCSATarget -VCSession $VCSession -Profiles ([pscustomobject]@{ VCSAStigs = [ordered]@{} }) -Attestations ([pscustomobject]@{}) -DirStructure ([pscustomobject]@{}) -ReportPath $Script:ReportRoot -TargetsList ([System.Collections.Generic.Queue[PSObject]]::new()) } | Should -Throw '*no VCSA credential resolved*'
	}

	It 'skips and records the family when the VCSA appliance is unreachable on ssh' {
		Mock Test-TargetReachable { $false }
		$VCSession = [pscustomobject]@{ Name = 'vcsa-12.example.internal' }
		$Cred = [pscredential]::new('root', (ConvertTo-SecureString 'x' -AsPlainText -Force))
		$Queue = [System.Collections.Generic.Queue[PSObject]]::new()

		Build-VCSATarget -VCSession $VCSession -Profiles ([pscustomobject]@{ VCSAStigs = [ordered]@{} }) -Attestations ([pscustomobject]@{}) -DirStructure ([pscustomobject]@{}) -ReportPath $Script:ReportRoot -TargetsList $Queue -VCSACredential $Cred -WarningAction SilentlyContinue

		$Queue.Count | Should -Be 0
		(Get-ScanSkips)[0].Family | Should -Be 'vcsa-vcsa-12.example.internal'
	}

	It 'builds one target per VCSA component, ssh-URI-authenticated as the resolved credential username' {
		Mock Test-TargetReachable { $true }
		Mock Get-VM { @() }
		Mock Resolve-HostIpViaDns { '198.51.100.30' }
		$VCSession = [pscustomobject]@{ Name = 'vcsa-13.example.internal' }
		$Cred = [pscredential]::new('root', (ConvertTo-SecureString 'x' -AsPlainText -Force))
		$Profiles = [pscustomobject]@{ Kind = 'stig'; VCSAStigs = [ordered]@{ sso = '/profiles/sso' }; VCSAInputsFile = '' }
		$DirStructure = [pscustomobject]@{ VCSACklPath = [pscustomobject]@{ FullName = $Script:ReportRoot } }
		$Queue = [System.Collections.Generic.Queue[PSObject]]::new()

		Build-VCSATarget -VCSession $VCSession -VISessions @($VCSession) -Profiles $Profiles -Attestations ([pscustomobject]@{}) -DirStructure $DirStructure -ReportPath $Script:ReportRoot -VSphereVersion '8.0' -TargetsList $Queue -VCSACredential $Cred -WarningAction SilentlyContinue

		$Queue.Count | Should -Be 1
		$Built = $Queue.Dequeue()
		$Built.TargetType | Should -Be 'vcsa-component-sso'
		$Built.Commands.InspecArgs | Should -Match 'ssh://root@vcsa-13\.example\.internal'
		$Built.Credential | Should -Be $Cred
	}
}

Describe 'Build-ESXiTarget' {
	BeforeEach {
		$Script:ReportRoot = Join-Path ([System.IO.Path]::GetTempPath()) "waypoint-test-$([guid]::NewGuid().ToString('N'))"
		New-Item -ItemType Directory -Path $Script:ReportRoot -Force | Out-Null
		Clear-ScanSkips
	}
	AfterEach { Remove-Item -Path $Script:ReportRoot -Recurse -Force -ErrorAction SilentlyContinue }

	It 'builds a target only for a Connected, non-Maintenance host and records the rest as skips' {
		Mock Get-VMHost {
			@(
				[pscustomobject]@{ Name = 'esxi-01.example.internal'; ConnectionState = 'Connected'; State = 'Connected' }
				[pscustomobject]@{ Name = 'esxi-02.example.internal'; ConnectionState = 'Connected'; State = 'Maintenance' }
				[pscustomobject]@{ Name = 'esxi-03.example.internal'; ConnectionState = 'Disconnected'; State = 'Disconnected' }
			)
		}
		Mock Get-VMHostNetworkAdapter { [pscustomobject]@{ IP = '198.51.100.40'; Mac = '00:00:00:00:00:01' } }

		$VCSession = [pscustomobject]@{ Name = 'vcsa-14.example.internal' }
		$Profiles = [pscustomobject]@{ Kind = 'stig'; ESXiProfile = '/profiles/esxi'; EsxiInputsFile = '' }
		$DirStructure = [pscustomobject]@{ ESXiCklPath = [pscustomobject]@{ FullName = $Script:ReportRoot } }
		$Queue = [System.Collections.Generic.Queue[PSObject]]::new()

		Build-ESXiTarget -VCSession $VCSession -Profiles $Profiles -Attestations ([pscustomobject]@{}) -DirStructure $DirStructure -ReportPath $Script:ReportRoot -VSphereVersion '8.0' -TargetsList $Queue -WarningAction SilentlyContinue

		$Queue.Count | Should -Be 1
		$Built = $Queue.Dequeue()
		$Built.TargetInfo.Name | Should -Be 'esxi-01.example.internal'
		$Built.TargetInfo.IP | Should -Be '198.51.100.40'

		$Skips = Get-ScanSkips
		$Skips.Count | Should -Be 2
		($Skips | Where-Object Family -eq 'esxi-esxi-02.example.internal').Reason | Should -Match 'maintenance mode'
		($Skips | Where-Object Family -eq 'esxi-esxi-03.example.internal').Reason | Should -Match "connection state 'Disconnected'"
	}

	It 'uses the srg host-input name and produces a summary file (no CKL) for the srg kind' {
		Mock Get-VMHost { @([pscustomobject]@{ Name = 'esxi-04.example.internal'; ConnectionState = 'Connected'; State = 'Connected' }) }
		Mock Get-VMHostNetworkAdapter { $null }

		$VCSession = [pscustomobject]@{ Name = 'vcsa-15.example.internal' }
		$Profiles = [pscustomobject]@{ Kind = 'srg'; ESXiProfile = '/profiles/esxi'; EsxiInputsFile = '' }
		$DirStructure = [pscustomobject]@{}
		$Queue = [System.Collections.Generic.Queue[PSObject]]::new()

		Build-ESXiTarget -VCSession $VCSession -Profiles $Profiles -Attestations ([pscustomobject]@{}) -DirStructure $DirStructure -ReportPath $Script:ReportRoot -VSphereVersion '9.0' -TargetsList $Queue -WarningAction SilentlyContinue

		$Built = $Queue.Dequeue()
		$Built.TargetType | Should -Be 'srg-esxi'
		$Built.Commands.InspecArgs | Should -Match 'esx_vmhostName'
		$Built.Paths.SummaryFile | Should -Match '\.summary\.yml$'
	}
}

Describe 'Build-VMTarget' {
	BeforeEach {
		$Script:ReportRoot = Join-Path ([System.IO.Path]::GetTempPath()) "waypoint-test-$([guid]::NewGuid().ToString('N'))"
		New-Item -ItemType Directory -Path $Script:ReportRoot -Force | Out-Null
	}
	AfterEach { Remove-Item -Path $Script:ReportRoot -Recurse -Force -ErrorAction SilentlyContinue }

	It 'excludes vCLS system VMs and builds a target per remaining VM' {
		Mock Get-VM {
			@(
				[pscustomobject]@{ Name = 'vCLS-1234'; Guest = $null }
				[pscustomobject]@{ Name = 'app-vm-01'; Guest = [pscustomobject]@{ IPAddress = @('198.51.100.50'); HostName = 'app-vm-01.example.internal'; Nics = $null } }
			)
		}
		Mock Get-NetworkAdapter { [pscustomobject]@{ MacAddress = '00:00:00:00:00:02' } }

		$VCSession = [pscustomobject]@{ Name = 'vcsa-16.example.internal' }
		$Profiles = [pscustomobject]@{ Kind = 'stig'; VMProfile = '/profiles/vm' }
		$DirStructure = [pscustomobject]@{ VMCklPath = [pscustomobject]@{ FullName = $Script:ReportRoot } }
		$Queue = [System.Collections.Generic.Queue[PSObject]]::new()

		Build-VMTarget -VCSession $VCSession -Profiles $Profiles -Attestations ([pscustomobject]@{}) -DirStructure $DirStructure -ReportPath $Script:ReportRoot -VSphereVersion '8.0' -FqdnRegex '.*' -TargetsList $Queue -WarningAction SilentlyContinue

		$Queue.Count | Should -Be 1
		$Built = $Queue.Dequeue()
		$Built.TargetInfo.Name | Should -Be 'app-vm-01'
		$Built.TargetInfo.IP | Should -Be '198.51.100.50'
		$Built.TargetInfo.Mac | Should -Be '00:00:00:00:00:02'
	}

	It 'falls back to the virtual NIC hardware MAC when guest tools report none' {
		Mock Get-VM { @([pscustomobject]@{ Name = 'app-vm-02'; Guest = [pscustomobject]@{ IPAddress = @('198.51.100.51'); HostName = $null; Nics = $null } }) }
		Mock Get-NetworkAdapter { [pscustomobject]@{ MacAddress = '00:00:00:00:00:03' } }

		$VCSession = [pscustomobject]@{ Name = 'vcsa-17.example.internal' }
		$Profiles = [pscustomobject]@{ Kind = 'srg'; VMProfile = '/profiles/vm' }
		$DirStructure = [pscustomobject]@{}
		$Queue = [System.Collections.Generic.Queue[PSObject]]::new()

		Build-VMTarget -VCSession $VCSession -Profiles $Profiles -Attestations ([pscustomobject]@{}) -DirStructure $DirStructure -ReportPath $Script:ReportRoot -VSphereVersion '9.0' -FqdnRegex '.*' -TargetsList $Queue -WarningAction SilentlyContinue

		$Built = $Queue.Dequeue()
		$Built.TargetType | Should -Be 'srg-vm'
		$Built.TargetInfo.Mac | Should -Be '00:00:00:00:00:03'
		$Built.Commands.InspecArgs | Should -Match 'vm_Name'
	}
}

Describe 'Get-StigTargets' {
	BeforeEach {
		$Global:DefaultVIServers = @()
		$Script:ReportRoot = Join-Path ([System.IO.Path]::GetTempPath()) "waypoint-test-$([guid]::NewGuid().ToString('N'))"
		New-Item -ItemType Directory -Path $Script:ReportRoot -Force | Out-Null
		Clear-ScanSkips
	}
	AfterEach { Remove-Item -Path $Script:ReportRoot -Recurse -Force -ErrorAction SilentlyContinue }

	It 'dispatches to every family builder for -ScanFilter all and returns the combined queue' {
		Mock Get-VM { @() }
		Mock Get-VMHost { @() }
		Mock Resolve-HostIpViaDns { '198.51.100.60' }
		$VCSession = [pscustomobject]@{ Name = 'vcsa-18.example.internal' }
		$Profiles = [pscustomobject]@{ Kind = 'stig'; VCenterProfile = '/p/vcenter'; VCenterInputsFile = ''; ESXiProfile = '/p/esxi'; EsxiInputsFile = ''; VMProfile = '/p/vm'; VCSAStigs = [ordered]@{}; VCSAInputsFile = '' }
		$DirStructure = [pscustomobject]@{ RunRoot = [pscustomobject]@{ FullName = $Script:ReportRoot }; VCenterCklPath = [pscustomobject]@{ FullName = $Script:ReportRoot }; ESXiCklPath = [pscustomobject]@{ FullName = $Script:ReportRoot }; VMCklPath = [pscustomobject]@{ FullName = $Script:ReportRoot } }
		$Cred = [pscredential]::new('root', (ConvertTo-SecureString 'x' -AsPlainText -Force))

		$Result = Get-StigTargets -VISessions @($VCSession) -Profiles $Profiles -Attestations ([pscustomobject]@{}) -DirStructure $DirStructure -VSphereVersion '8.0' -ScanFilter 'all' -VCSACredential $Cred -WarningAction SilentlyContinue

		# vCenter target always built; VCSA has zero components so none are enqueued;
		# ESXi/VM discover zero hosts/VMs -- total is just the one vCenter target.
		$Result.TargetsList.Count | Should -Be 1
	}

	It 'throws for a VCSA-only scan filter when no VCSA credential is resolved' {
		$VCSession = [pscustomobject]@{ Name = 'vcsa-19.example.internal' }
		$Profiles = [pscustomobject]@{ Kind = 'stig' }
		$DirStructure = [pscustomobject]@{ RunRoot = [pscustomobject]@{ FullName = $Script:ReportRoot } }

		{ Get-StigTargets -VISessions @($VCSession) -Profiles $Profiles -Attestations ([pscustomobject]@{}) -DirStructure $DirStructure -ScanFilter 'vcsa' -WarningAction SilentlyContinue } | Should -Throw '*no other target family*'
	}

	It 'records a Warning-level skip (not a throw) for a mixed scan filter missing a VCSA credential' {
		Mock Get-VM { @() }
		Mock Get-VMHost { @() }
		Mock Resolve-HostIpViaDns { '198.51.100.61' }
		$VCSession = [pscustomobject]@{ Name = 'vcsa-20.example.internal' }
		$Profiles = [pscustomobject]@{ Kind = 'stig'; VCenterProfile = '/p/vcenter'; VCenterInputsFile = ''; ESXiProfile = '/p/esxi'; EsxiInputsFile = ''; VMProfile = '/p/vm' }
		$DirStructure = [pscustomobject]@{ RunRoot = [pscustomobject]@{ FullName = $Script:ReportRoot }; VCenterCklPath = [pscustomobject]@{ FullName = $Script:ReportRoot }; ESXiCklPath = [pscustomobject]@{ FullName = $Script:ReportRoot }; VMCklPath = [pscustomobject]@{ FullName = $Script:ReportRoot } }

		{ Get-StigTargets -VISessions @($VCSession) -Profiles $Profiles -Attestations ([pscustomobject]@{}) -DirStructure $DirStructure -ScanFilter 'all' -WarningAction SilentlyContinue } | Should -Not -Throw
		(Get-ScanSkips)[0].Family | Should -Be 'vcsa'
	}
}

Describe 'Build-VsphereTransportTargets' {
	BeforeEach {
		$Global:DefaultVIServers = @()
		$Script:ReportRoot = Join-Path ([System.IO.Path]::GetTempPath()) "waypoint-test-$([guid]::NewGuid().ToString('N'))"
		New-Item -ItemType Directory -Path $Script:ReportRoot -Force | Out-Null
	}
	AfterEach { Remove-Item -Path $Script:ReportRoot -Recurse -Force -ErrorAction SilentlyContinue }

	It 'connects inline (bare-CLI/single-row path), builds targets, and attaches the right credential per target type' {
		$VSCred = [pscredential]::new('svc-vsphere-admin', (ConvertTo-SecureString 'x' -AsPlainText -Force))
		$VCSACred = [pscredential]::new('root', (ConvertTo-SecureString 'y' -AsPlainText -Force))
		Mock Connect-VsphereTransportRow {
			[pscustomobject]@{
				Connection     = [pscustomobject]@{ Sessions = @([pscustomobject]@{ Name = 'vcsa-21.example.internal' }); VSphereCredential = $VSCred; VCSACredential = $VCSACred; DisconnectAtCleanup = $true }
				Profiles       = [pscustomobject]@{ Kind = 'stig'; VCenterProfile = '/p/vcenter'; VCenterInputsFile = ''; ESXiProfile = '/p/esxi'; EsxiInputsFile = ''; VMProfile = '/p/vm'; VCSAStigs = [ordered]@{ sso = '/p/sso' }; VCSAInputsFile = '' }
				Attestations   = [pscustomobject]@{}
				DisplayVersion = '8.0'
			}
		}
		Mock Get-VM { @() }
		Mock Get-VMHost { @() }
		Mock Resolve-HostIpViaDns { '198.51.100.70' }
		Mock Test-TargetReachable { $true }

		$DirStructure = [pscustomobject]@{ RunRoot = [pscustomobject]@{ FullName = $Script:ReportRoot }; VCenterCklPath = [pscustomobject]@{ FullName = $Script:ReportRoot }; ESXiCklPath = [pscustomobject]@{ FullName = $Script:ReportRoot }; VMCklPath = [pscustomobject]@{ FullName = $Script:ReportRoot }; VCSACklPath = [pscustomobject]@{ FullName = $Script:ReportRoot } }
		$RouterQueue = [System.Collections.Generic.Queue[PSObject]]::new()

		$Result = Build-VsphereTransportTargets -DirStructure $DirStructure -TargetsList $RouterQueue -WarningAction SilentlyContinue

		$Result.BenchmarkVersion | Should -Be '8.0'
		$RouterQueue.Count | Should -Be 2
		$ByType = @{}
		while ($RouterQueue.Count -gt 0) { $T = $RouterQueue.Dequeue(); $ByType[$T.TargetType] = $T }
		$ByType['vcenter'].Credential | Should -Be $VSCred
		$ByType['vcsa-component-sso'].Credential | Should -Be $VCSACred
	}

	It 'skips the connect phase and reuses a pre-connected RowContext (multi-row claim-before-build)' {
		$VSCred = [pscredential]::new('svc-vsphere-admin', (ConvertTo-SecureString 'x' -AsPlainText -Force))
		Mock Connect-VsphereTransportRow { throw 'must not run the connect phase again' }
		Mock Get-VM { @() }
		Mock Get-VMHost { @() }
		Mock Resolve-HostIpViaDns { '198.51.100.71' }

		$RowContext = [pscustomobject]@{
			Connection     = [pscustomobject]@{ Sessions = @([pscustomobject]@{ Name = 'vcsa-22.example.internal' }); VSphereCredential = $VSCred; VCSACredential = $null; DisconnectAtCleanup = $true }
			Profiles       = [pscustomobject]@{ Kind = 'stig'; VCenterProfile = '/p/vcenter'; VCenterInputsFile = ''; ESXiProfile = '/p/esxi'; EsxiInputsFile = ''; VMProfile = '/p/vm'; VCSAStigs = [ordered]@{}; VCSAInputsFile = '' }
			Attestations   = [pscustomobject]@{}
			DisplayVersion = '8.0'
		}
		$DirStructure = [pscustomobject]@{ RunRoot = [pscustomobject]@{ FullName = $Script:ReportRoot }; VCenterCklPath = [pscustomobject]@{ FullName = $Script:ReportRoot }; ESXiCklPath = [pscustomobject]@{ FullName = $Script:ReportRoot }; VMCklPath = [pscustomobject]@{ FullName = $Script:ReportRoot } }
		$RouterQueue = [System.Collections.Generic.Queue[PSObject]]::new()

		Build-VsphereTransportTargets -DirStructure $DirStructure -TargetsList $RouterQueue -RowContext $RowContext -ScanFilter 'vcenter' -WarningAction SilentlyContinue | Out-Null

		$RouterQueue.Count | Should -Be 1
		Should -Invoke Connect-VsphereTransportRow -Times 0
	}
}
