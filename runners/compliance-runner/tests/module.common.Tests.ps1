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

# Issue #1355: direct Pester coverage for module.common.ps1 (imported, owner-authored
# vmware-stig-docker sibling code -- see runners/compliance-runner/powershell/README.md).
# Dot-sources the module directly from the runner tree (not via backend/). module.common.ps1
# calls Get-LogSplat/Write-Log (provided at runtime by the Waypoint shim modules, per
# README) and, in New-ScanDirectoryStructure, Get-CatalogReportGroupMap (module.catalog.ps1,
# not imported) -- both are stubbed here so the file dot-sources and runs standalone; tests
# that need particular behavior from them use Pester Mock.
#
# All hostnames/IPs/paths in this suite are invented (AGENTS.md sanitization): RFC 5737
# addresses and .example.internal / .invalid names only.
#
# Run: pwsh -NoProfile -Command "Invoke-Pester -Path <this file> -CI"

BeforeAll {
	$Script:ModulePath = Join-Path $PSScriptRoot '../powershell/module.common.ps1'

	# Stand-ins for the runtime shims module.common.ps1 depends on but does not define
	# itself (see README "Runtime wiring" / the Waypoint shim modules' own doc comments).
	function global:Get-LogSplat { param([Parameter(Position = 0)][AllowNull()][AllowEmptyString()][string]$Source) if ($Source) { return @{ Source = $Source } }; return @{} }
	function global:Write-Log { param([Parameter(Mandatory, Position = 0)][string]$Message, [string]$Severity = 'Info', [object]$LogQueue, [string]$Source, [datetime]$Timestamp) }
	function global:Get-CatalogReportGroupMap { @{} }

	. $Script:ModulePath
}

AfterAll {
	# Remove the global stand-ins so they do not leak into later test files in the
	# same Pester run (see review note on PR #1717 round 1).
	Remove-Item Function:\global:Get-LogSplat -ErrorAction SilentlyContinue
	Remove-Item Function:\global:Write-Log -ErrorAction SilentlyContinue
	Remove-Item Function:\global:Get-CatalogReportGroupMap -ErrorAction SilentlyContinue
}

Describe 'Add-ScanSkip / Get-ScanSkips / Clear-ScanSkips' {
	BeforeEach { Clear-ScanSkips }

	It 'records a skip and returns it from Get-ScanSkips' {
		Add-ScanSkip -Family 'vcsa' -Reason 'no credential'
		$Skips = Get-ScanSkips
		$Skips.Count | Should -Be 1
		$Skips[0].Family | Should -Be 'vcsa'
		$Skips[0].Reason | Should -Be 'no credential'
	}

	It 'returns an empty array (never $null) when nothing was skipped' {
		$Skips = Get-ScanSkips
		$Skips.GetType().IsArray | Should -BeTrue
		$Skips.Count | Should -Be 0
	}

	It 'Clear-ScanSkips empties a previously populated list' {
		Add-ScanSkip -Family 'esxi-host1' -Reason 'unreachable'
		Clear-ScanSkips
		$Skips = Get-ScanSkips
		$Skips.Count | Should -Be 0
	}
}

Describe 'Get-PSObjectMember' {
	It 'returns the property value when present' {
		$Obj = [pscustomobject]@{ Foo = 'bar' }
		Get-PSObjectMember -InputObject $Obj -Name 'Foo' | Should -Be 'bar'
	}

	It 'returns $null when the member is absent' {
		$Obj = [pscustomobject]@{ Foo = 'bar' }
		Get-PSObjectMember -InputObject $Obj -Name 'Missing' | Should -BeNullOrEmpty
	}

	It 'returns $null when InputObject itself is $null' {
		Get-PSObjectMember -InputObject $null -Name 'Foo' | Should -BeNullOrEmpty
	}
}

Describe 'Resolve-HostIpViaDns' {
	It 'returns the first IPv4 address for a resolvable name (localhost, via /etc/hosts)' {
		Resolve-HostIpViaDns -HostName 'localhost' | Should -Be '127.0.0.1'
	}

	It 'returns an empty string, never throws, for an unresolvable name' {
		$Result = $null
		{ $Result = Resolve-HostIpViaDns -HostName 'definitely-unresolvable.invalid' } | Should -Not -Throw
		$Result | Should -BeNullOrEmpty
	}
}

Describe 'Test-TargetReachable' {
	It 'rejects a blank target host at bind time (Mandatory, no AllowEmptyString)' {
		{ Test-TargetReachable -TargetHost '' -Port 22 } | Should -Throw
	}

	It 'returns $true when a TCP connection succeeds (loopback listener)' {
		$Listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
		$Listener.Start()
		try {
			$Port = $Listener.LocalEndpoint.Port
			Test-TargetReachable -TargetHost '127.0.0.1' -Port $Port -TimeoutSeconds 5 | Should -BeTrue
		} finally {
			$Listener.Stop()
		}
	}

	It 'returns $false on a connect timeout against an unroutable test-net address' {
		# 192.0.2.0/24 (TEST-NET-1, RFC 5737) is reserved documentation space that never
		# routes; a short timeout keeps this deterministic and fast.
		Test-TargetReachable -TargetHost '192.0.2.1' -Port 65000 -TimeoutSeconds 1 | Should -BeFalse
	}
}

Describe 'New-AttestStep' {
	It 'skips attestation when no template is set' {
		$Result = New-AttestStep -ReportFile '/reports/x.json' -AttestTemplate ''
		$Result.AttestRequired | Should -BeFalse
		$Result.ConvertInputFile | Should -Be '/reports/x.json'
		$Result.SafAttestArgs | Should -Be ''
	}

	It 'builds the attest step and saf attest args when a template is set' {
		$Result = New-AttestStep -ReportFile '/reports/x.json' -AttestTemplate '/config/template.yml' -AttestReportFile '/reports/x.attest.json'
		$Result.AttestRequired | Should -BeTrue
		$Result.ConvertInputFile | Should -Be '/reports/x.attest.json'
		$Result.SafAttestArgs | Should -Be 'attest apply -i "/reports/x.json" "/config/template.yml" -o "/reports/x.attest.json"'
	}
}

Describe 'New-CklConvertArgs' {
	It 'builds the base convert command with no optional host metadata' {
		New-CklConvertArgs -ConvertInputFile '/r/x.json' -CklStagingPath '/r/x.ckl' | Should -Be 'convert hdf2ckl -i "/r/x.json" -o "/r/x.ckl"'
	}

	It 'appends hostname/fqdn/ip/mac only when each is non-empty' {
		$Result = New-CklConvertArgs -ConvertInputFile '/r/x.json' -CklStagingPath '/r/x.ckl' -Hostname 'esxi-01' -Fqdn 'esxi-01.example.internal' -Ip '198.51.100.10' -Mac ''
		$Result | Should -Be 'convert hdf2ckl -i "/r/x.json" -o "/r/x.ckl" --hostname "esxi-01" --fqdn "esxi-01.example.internal" --ip "198.51.100.10"'
		$Result | Should -Not -Match '--mac'
	}
}

Describe 'New-StigTarget' {
	It 'builds a stig-kind target with the Config state machine defaulted' {
		$Target = New-StigTarget -TargetType 'esxi' -Kind 'stig' -AttestRequired $false -Name 'esxi-01' `
			-ReportFile '/r/x.json' -InspecProfile '/profiles/esxi' -InspecArgs 'inspec args'
		$Target.TargetType | Should -Be 'esxi'
		$Target.Config.Kind | Should -Be 'stig'
		$Target.Config.Success | Should -BeFalse
		$Target.TargetInfo.Name | Should -Be 'esxi-01'
		$Target.PSObject.Properties.Name | Should -Not -Contain 'Credential'
	}

	It 'adds srg-only Config/Paths fields when -Kind srg' {
		$Target = New-StigTarget -TargetType 'srg-esxi' -Kind 'srg' -AttestRequired $false -Name 'esxi-01' `
			-ReportFile '/r/x.json' -InspecProfile '/profiles/esxi' -InspecArgs 'inspec args' -SummaryFile '/r/x.summary.yml' -Sudo $true
		$Target.Config.SummaryGenerated | Should -BeFalse
		$Target.Config.Sudo | Should -BeTrue
		$Target.Paths.SummaryFile | Should -Be '/r/x.summary.yml'
	}

	It 'attaches a Credential property only when supplied' {
		$Cred = [pscredential]::new('root', (ConvertTo-SecureString 'x' -AsPlainText -Force))
		$Target = New-StigTarget -TargetType 'vcsa-component-sso' -Kind 'stig' -AttestRequired $false -Name 'vcsa' `
			-ReportFile '/r/x.json' -InspecProfile '/profiles/vcsa' -InspecArgs 'inspec args' -Credential $Cred
		$Target.Credential | Should -Be $Cred
	}
}

Describe 'Get-RedactedCommandString' {
	It 'redacts a quoted --password= CLI flag' {
		Get-RedactedCommandString -CommandString '--password="hunter2"' | Should -Be '--password=***REDACTED***'
	}

	It 'redacts a space-separated --sudo-password flag' {
		Get-RedactedCommandString -CommandString '--sudo-password hunter2' | Should -Be '--sudo-password ***REDACTED***'
	}

	It 'redacts a JSON secret-shaped key/value pair' {
		Get-RedactedCommandString -CommandString '{"password":"hunter2"}' | Should -Be '{"password":"***REDACTED***"}'
	}

	It 'redacts an all-caps env-assignment secret' {
		Get-RedactedCommandString -CommandString 'SSHPASS=hunter2 ssh root@host' | Should -Be 'SSHPASS=***REDACTED*** ssh root@host'
	}

	It 'does not redact a lowercase InSpec input that merely contains "password" as a substring' {
		Get-RedactedCommandString -CommandString '--input password_policy=foo' | Should -Be '--input password_policy=foo'
	}

	It 'passes an empty string through unchanged' {
		Get-RedactedCommandString -CommandString '' | Should -Be ''
	}
}

Describe 'New-InspecSecretConfigFile' {
	BeforeEach { $Script:TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "waypoint-test-$([guid]::NewGuid().ToString('N'))"; New-Item -ItemType Directory -Path $Script:TempDir -Force | Out-Null }
	AfterEach { Remove-Item -Path $Script:TempDir -Recurse -Force -ErrorAction SilentlyContinue }

	It 'writes a JSON file with only the password key when no sudo password is supplied' {
		$Path = New-InspecSecretConfigFile -Password 'hunter2' -Directory $Script:TempDir
		Test-Path $Path | Should -BeTrue
		$Content = Get-Content -Path $Path -Raw | ConvertFrom-Json
		$Content.password | Should -Be 'hunter2'
		$Content.PSObject.Properties.Name | Should -Not -Contain 'sudo_password'
	}

	It 'includes sudo_password when supplied' {
		$Path = New-InspecSecretConfigFile -Password 'hunter2' -SudoPassword 'sudohunter2' -Directory $Script:TempDir
		$Content = Get-Content -Path $Path -Raw | ConvertFrom-Json
		$Content.sudo_password | Should -Be 'sudohunter2'
	}
}

Describe 'Write-CapturedOutputWindow' {
	BeforeEach { $Script:LoggedMessages = [System.Collections.Generic.List[string]]::new() }

	It 'logs nothing when both streams are blank' {
		Mock Write-Log { $Script:LoggedMessages.Add($Message) }
		Write-CapturedOutputWindow -StdOut '' -StdErr '' -ContextPrefix 'Test' -WriteLogParams @{}
		$Script:LoggedMessages.Count | Should -Be 0
	}

	It 'logs the full window (redacted) when at or under 20 non-blank lines' {
		Mock Write-Log { $Script:LoggedMessages.Add($Message) }
		Write-CapturedOutputWindow -StdOut "line1`nSSHPASS=hunter2 x" -StdErr '' -ContextPrefix 'Proc' -WriteLogParams @{}
		$Script:LoggedMessages.Count | Should -Be 1
		$Script:LoggedMessages[0] | Should -Match 'SSHPASS=\*\*\*REDACTED\*\*\*'
		$Script:LoggedMessages[0] | Should -Not -Match 'hunter2'
	}

	It 'head/tail-windows output beyond 20 non-blank lines with a suppressed-count marker' {
		Mock Write-Log { $Script:LoggedMessages.Add($Message) }
		$Lines = 1..25 | ForEach-Object { "line$_" }
		Write-CapturedOutputWindow -StdOut ($Lines -join "`n") -StdErr '' -ContextPrefix 'Proc' -WriteLogParams @{}
		$Script:LoggedMessages[0] | Should -Match '5 line\(s\) suppressed'
		$Script:LoggedMessages[0] | Should -Match 'line1'
		$Script:LoggedMessages[0] | Should -Match 'line25'
		$Script:LoggedMessages[0] | Should -Not -Match 'line13'
	}
}

Describe 'Invoke-ExternalCommand' {
	It 'returns exit code 0 and PassThru output for a successful command' {
		$ExitCode = Invoke-ExternalCommand -Executable '/bin/echo' -Arguments 'hello' -SuppressOutput
		$ExitCode | Should -Be 0
	}

	It 'PassThru returns captured stdout' {
		$Output = Invoke-ExternalCommand -Executable '/bin/echo' -Arguments 'hello-world' -PassThru -SuppressOutput
		$Output | Should -Match 'hello-world'
	}

	It 'returns the real exit code for a failing command' {
		$ExitCode = Invoke-ExternalCommand -Executable '/bin/sh' -Arguments '-c "exit 7"' -SuppressOutput
		$ExitCode | Should -Be 7
	}

	It 'treats a non-default AllowedExitCodes entry as success' {
		$ExitCode = Invoke-ExternalCommand -Executable '/bin/sh' -Arguments '-c "exit 100"' -AllowedExitCodes @(0, 100, 101) -SuppressOutput
		$ExitCode | Should -Be 100
	}

	It 'throws on timeout against a process that outlives the timeout window' {
		{ Invoke-ExternalCommand -Executable '/bin/sleep' -Arguments '5' -TimeoutMilliseconds 200 -SuppressOutput } | Should -Throw '*timed out*'
	}

	It 'returns a control object immediately with -Background, without waiting for completion' {
		$Control = Invoke-ExternalCommand -Executable '/bin/sleep' -Arguments '2' -Background -SuppressOutput
		try {
			$Control.Process | Should -Not -BeNullOrEmpty
			$Control.ProcessName | Should -Be 'External Command'
		} finally {
			Stop-ExternalCommand -ControlObject $Control
		}
	}
}

Describe 'Stop-ExternalCommand' {
	It 'stops a running background process gracefully (SIGTERM) and drains its output' {
		$Control = Invoke-ExternalCommand -Executable '/bin/sleep' -Arguments '30' -Background -ProcessName 'sleeper' -SuppressOutput
		{ Stop-ExternalCommand -ControlObject $Control -GracePeriodMs 3000 } | Should -Not -Throw
	}

	It 'handles a process that already exited before Stop-ExternalCommand is called' {
		$Control = Invoke-ExternalCommand -Executable '/bin/echo' -Arguments 'done' -Background -ProcessName 'quick' -SuppressOutput
		$Control.Process.WaitForExit(5000) | Out-Null
		{ Stop-ExternalCommand -ControlObject $Control } | Should -Not -Throw
	}

	It 'is a no-op (does not throw) when ControlObject.Process is $null' {
		{ Stop-ExternalCommand -ControlObject ([pscustomobject]@{ Process = $null; Collector = $null; ProcessName = 'none' }) } | Should -Not -Throw
	}
}

Describe 'Test-EnvironmentDependencies' {
	It 'throws when the saf CLI is not on PATH' {
		Mock Get-Command { $null } -ParameterFilter { $Name -eq 'saf' }
		{ Test-EnvironmentDependencies } | Should -Throw '*SAF CLI not found*'
	}

	It 'throws when inspec is not on PATH' {
		Mock Get-Command { [pscustomobject]@{ Name = 'saf' } } -ParameterFilter { $Name -eq 'saf' }
		Mock Get-Command { $null } -ParameterFilter { $Name -eq 'inspec' }
		{ Test-EnvironmentDependencies } | Should -Throw '*InSpec not found*'
	}
}

Describe 'New-ScanDirectoryStructure' {
	BeforeEach { $Script:TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "waypoint-test-$([guid]::NewGuid().ToString('N'))" }
	AfterEach { Remove-Item -Path $Script:TempDir -Recurse -Force -ErrorAction SilentlyContinue }

	It 'throws when the catalog report-group map is empty (catalog not loaded)' {
		Mock Get-CatalogReportGroupMap { @{} }
		{ New-ScanDirectoryStructure -ReportPath $Script:TempDir } | Should -Throw '*Import-StigCatalog*'
	}

	It 'creates the run root, ckls base, and one subdirectory per non-srg report group; sets WatcherCklPath' {
		Mock Get-CatalogReportGroupMap {
			@{
				esxi = [pscustomobject]@{ Kind = 'stig'; ReportGroup = 'esxi' }
				vm   = [pscustomobject]@{ Kind = 'stig'; ReportGroup = 'vm' }
				srg  = [pscustomobject]@{ Kind = 'srg'; ReportGroup = 'srg' }
			}
		}
		$Result = New-ScanDirectoryStructure -ReportPath $Script:TempDir
		Test-Path $Result.RunRoot.FullName | Should -BeTrue
		Test-Path $Result.CklBase.FullName | Should -BeTrue
		Test-Path $Result.ESXiCklPath.FullName | Should -BeTrue
		Test-Path $Result.VMCklPath.FullName | Should -BeTrue
		Test-Path (Join-Path $Result.CklBase.FullName 'srg') | Should -BeFalse
		$Script:RuntimeConfig.WatcherCklPath | Should -Be $Result.CklBase.FullName
	}
}

Describe 'Get-SanitizedName' {
	It 'removes characters not safe for filenames' {
		Get-SanitizedName -Name 'vcsa-01.example!internal/foo' | Should -Be 'vcsa-01exampleinternalfoo'
	}

	It 'leaves an already-safe name unchanged' {
		Get-SanitizedName -Name 'esxi-01_test' | Should -Be 'esxi-01_test'
	}
}

Describe 'Get-TargetShortName' {
	It 'returns the first dot-separated label for an FQDN' {
		Get-TargetShortName -HostName 'esxi-01.example.internal' | Should -Be 'esxi-01'
	}

	It 'returns an IPv4 literal unchanged (dots are legal in filenames)' {
		Get-TargetShortName -HostName '192.0.2.10' | Should -Be '192.0.2.10'
	}

	It 'replaces colons with hyphens for an IPv6 literal' {
		Get-TargetShortName -HostName '2001:db8::1' | Should -Be '2001-db8--1'
	}

	It 'returns a single-label bare hostname unchanged' {
		Get-TargetShortName -HostName 'esxi-01' | Should -Be 'esxi-01'
	}
}

Describe 'Get-SiteTargetDifferentiator' {
	It 'uses the row name when set' {
		$Target = [pscustomobject]@{ name = 'Site A'; connection = [pscustomobject]@{ host = 'web01.site-a.example.internal' } }
		Get-SiteTargetDifferentiator -Target $Target | Should -Be 'Site-A'
	}

	It 'falls back to the FQDN remainder when no row name is set' {
		$Target = [pscustomobject]@{ connection = [pscustomobject]@{ host = 'web01.site-a.example.internal' } }
		Get-SiteTargetDifferentiator -Target $Target | Should -Be 'site-a-example-internal'
	}

	It 'returns empty for an IP-literal host with no row name' {
		$Target = [pscustomobject]@{ connection = [pscustomobject]@{ host = '203.0.113.10' } }
		Get-SiteTargetDifferentiator -Target $Target | Should -Be ''
	}
}

Describe 'Resolve-SiteTargetShortName' {
	AfterEach { $Script:Site = $null }

	It 'returns the plain short name when no other row collides' {
		$Script:Site = [pscustomobject]@{ targets = @() }
		$Target = [pscustomobject]@{ product = 'vsphere'; kind = 'stig'; connection = [pscustomobject]@{ host = 'esxi-01.example.internal' } }
		Resolve-SiteTargetShortName -Target $Target | Should -Be 'esxi-01'
	}

	It 'disambiguates with the differentiator when two same-product/kind rows collide on short name' {
		$TargetA = [pscustomobject]@{ product = 'vsphere'; kind = 'stig'; connection = [pscustomobject]@{ host = 'web01.site-a.example.internal' } }
		$TargetB = [pscustomobject]@{ product = 'vsphere'; kind = 'stig'; connection = [pscustomobject]@{ host = 'web01.site-b.example.internal' } }
		$Script:Site = [pscustomobject]@{ targets = @($TargetA, $TargetB) }

		Resolve-SiteTargetShortName -Target $TargetA -WarningAction SilentlyContinue | Should -Be 'web01-site-a-example-internal'
		Resolve-SiteTargetShortName -Target $TargetB -WarningAction SilentlyContinue | Should -Be 'web01-site-b-example-internal'
	}

	It 'appends the row position when differentiators also tie (identical hosts, no row names)' {
		$TargetA = [pscustomobject]@{ product = 'vsphere'; kind = 'stig'; connection = [pscustomobject]@{ host = '203.0.113.20' } }
		$TargetB = [pscustomobject]@{ product = 'vsphere'; kind = 'stig'; connection = [pscustomobject]@{ host = '203.0.113.20' } }
		$Script:Site = [pscustomobject]@{ targets = @($TargetA, $TargetB) }

		Resolve-SiteTargetShortName -Target $TargetA -WarningAction SilentlyContinue | Should -Be '203.0.113.20-row1'
		Resolve-SiteTargetShortName -Target $TargetB -WarningAction SilentlyContinue | Should -Be '203.0.113.20-row2'
	}

	It 'does not collide with a row of a different product' {
		$TargetA = [pscustomobject]@{ product = 'vsphere'; kind = 'stig'; connection = [pscustomobject]@{ host = 'esxi-01.example.internal' } }
		$TargetB = [pscustomobject]@{ product = 'nsx'; kind = 'stig'; connection = [pscustomobject]@{ host = 'esxi-01.example.org' } }
		$Script:Site = [pscustomobject]@{ targets = @($TargetA, $TargetB) }
		Resolve-SiteTargetShortName -Target $TargetA | Should -Be 'esxi-01'
	}
}
