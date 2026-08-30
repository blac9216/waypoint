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

# Issue #1115: exact `-eq $VCenter` session-name equality (the pre-#1115
# Invoke-WaypointDiscovery match) missed an Enhanced Linked Mode target addressed by
# IP, or any other non-byte-identical form (short name vs FQDN, case difference) --
# with more than one -AllLinked session, that meant no vcenter identity row at all
# even though the appliance WAS one of the linked sessions. This suite pins the
# replacement resolver (ConvertTo-WaypointNormalizedHost /
# Get-WaypointSessionHostCandidates / Resolve-WaypointHostAddresses /
# Resolve-WaypointReverseHostNames / Test-WaypointSessionMatchesVCenter /
# Resolve-WaypointPrimarySession, all in WaypointDiscovery.psm1) against the
# ADR-0023 fail-closed contract: identity is proven, never guessed by position, and
# an ambiguous or absent match withholds the vcenter row with a structured warning
# rather than picking a session.
#
# Every hostname/IP below is invented for this test (AGENTS.md sanitization: no real
# hostnames, IPs, or lab paths) -- addresses come from RFC 5737's reserved
# documentation ranges (TEST-NET-2/3) and names use the .example.internal /
# .invalid reserved-style suffixes, never real infrastructure.
#
# Run: pwsh -NoProfile -Command "Invoke-Pester -Path <this file> -CI"

BeforeAll {
	$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
	$ModulePath = Join-Path $RepoRoot 'backend/Waypoint.Infrastructure.Execution/PowerShell/Modules/WaypointDiscovery/WaypointDiscovery.psm1'
	Import-Module $ModulePath -Force
}

AfterAll {
	Remove-Module WaypointDiscovery -Force -ErrorAction SilentlyContinue
}

Describe 'ConvertTo-WaypointNormalizedHost' {
	It 'lowercases and trims a trailing dot' {
		InModuleScope WaypointDiscovery {
			ConvertTo-WaypointNormalizedHost -Value 'VCSA-01.Example.Internal.' | Should -Be 'vcsa-01.example.internal'
		}
	}

	It 'returns $null for a blank value' {
		InModuleScope WaypointDiscovery {
			ConvertTo-WaypointNormalizedHost -Value '   ' | Should -BeNullOrEmpty
		}
	}
}

Describe 'Test-WaypointSessionMatchesVCenter' {
	It 'matches on Name after case/trailing-dot normalization, with no DNS involved' {
		InModuleScope WaypointDiscovery {
			Mock Resolve-WaypointHostAddresses { @() }
			Mock Resolve-WaypointReverseHostNames { @() }

			$Session = [pscustomobject]@{ Name = 'vcsa-01.example.internal'; ServiceUri = $null }
			Test-WaypointSessionMatchesVCenter -Session $Session -VCenter 'VCSA-01.Example.Internal.' | Should -BeTrue
			Should -Invoke Resolve-WaypointHostAddresses -Times 0
		}
	}

	It 'matches on the ServiceUri host when Name differs' {
		InModuleScope WaypointDiscovery {
			Mock Resolve-WaypointHostAddresses { @() }

			$Session = [pscustomobject]@{ Name = 'some-other-display-name'; ServiceUri = [Uri]'https://vcsa-02.example.internal/sdk' }
			Test-WaypointSessionMatchesVCenter -Session $Session -VCenter 'vcsa-02.example.internal' | Should -BeTrue
		}
	}

	It 'resolves a short-name-vs-FQDN difference through forward DNS' {
		InModuleScope WaypointDiscovery {
			Mock Resolve-WaypointHostAddresses {
				switch ($HostNameOrAddress) {
					'vcsa-03' { @('203.0.113.30') }
					'vcsa-03.example.internal' { @('203.0.113.30') }
					default { @() }
				}
			}

			$Session = [pscustomobject]@{ Name = 'vcsa-03.example.internal'; ServiceUri = $null }
			Test-WaypointSessionMatchesVCenter -Session $Session -VCenter 'vcsa-03' | Should -BeTrue
		}
	}

	It 'resolves an IP-addressed target to the session whose FQDN forward-resolves to that address' {
		InModuleScope WaypointDiscovery {
			Mock Resolve-WaypointHostAddresses {
				switch ($HostNameOrAddress) {
					'203.0.113.10' { @('203.0.113.10') }
					'vcsa-a.example.internal' { @('203.0.113.10') }
					'vcsa-b.example.internal' { @('203.0.113.20') }
					default { @() }
				}
			}

			$SessionA = [pscustomobject]@{ Name = 'vcsa-a.example.internal'; ServiceUri = $null }
			$SessionB = [pscustomobject]@{ Name = 'vcsa-b.example.internal'; ServiceUri = $null }

			Test-WaypointSessionMatchesVCenter -Session $SessionA -VCenter '203.0.113.10' | Should -BeTrue
			Test-WaypointSessionMatchesVCenter -Session $SessionB -VCenter '203.0.113.10' | Should -BeFalse
		}
	}

	It 'resolves an IP-addressed target through reverse DNS when forward resolution of the session name yields nothing' {
		InModuleScope WaypointDiscovery {
			# Forward resolution deliberately returns nothing for every name (as if the
			# forward record were missing/stale) -- only the reverse PTR record for the
			# requested IP identifies the session, proving the reverse-DNS path is
			# exercised independently of the forward-address path above.
			Mock Resolve-WaypointHostAddresses { @() }
			Mock Resolve-WaypointReverseHostNames {
				if ($IpAddress -eq '203.0.113.40') { @('vcsa-04.example.internal') } else { @() }
			}

			$Session = [pscustomobject]@{ Name = 'vcsa-04.example.internal'; ServiceUri = $null }
			Test-WaypointSessionMatchesVCenter -Session $Session -VCenter '203.0.113.40' | Should -BeTrue
		}
	}

	It 'does not throw and reports no match when DNS resolution genuinely fails' {
		InModuleScope WaypointDiscovery {
			# Issue #1252: this used to rely on 'unresolvable.invalid'/.invalid never
			# resolving (RFC 2606) by leaving the call unmocked -- a hijacking/wildcard
			# resolver breaks that assumption, and each failed lookup used to pay a real
			# resolver timeout. The real try/catch in Resolve-WaypointHostAddresses /
			# Resolve-WaypointReverseHostNames is already covered directly, unmocked, by
			# the "unmocked failure paths" Describe block below -- this test only needs
			# to prove Test-WaypointSessionMatchesVCenter's own no-match/no-throw
			# contract, which the Mock below does deterministically and instantly.
			Mock Resolve-WaypointHostAddresses { @() }
			Mock Resolve-WaypointReverseHostNames { @() }

			$Session = [pscustomobject]@{ Name = 'also-unresolvable.invalid'; ServiceUri = $null }

			$Result = $null
			{ $Result = Test-WaypointSessionMatchesVCenter -Session $Session -VCenter 'unresolvable.invalid' } | Should -Not -Throw
			$Result | Should -BeFalse
		}
	}

	It 'routes forward/reverse resolution through an injected -NameResolver, recording every name it is asked to resolve' {
		InModuleScope WaypointDiscovery {
			# Issue #1252: proves the test-hermeticity seam itself -- the C# regression
			# guard (VsphereApiOnlyNoninteractiveTests, CI-enforced per issue #1245,
			# since CI does not run this Pester suite outside ShapeInventory) pins the
			# same contract end-to-end through Invoke-WaypointDiscovery; this pins it at
			# the unit level, directly against Test-WaypointSessionMatchesVCenter.
			$RecordedCalls = [System.Collections.Generic.List[string]]::new()
			$Resolver = {
				param($Mode, $Value)
				$RecordedCalls.Add("${Mode}:${Value}")
				@("stub-address-for-$Value")
			}.GetNewClosure()

			$Session = [pscustomobject]@{ Name = 'vcsa-stub-session.example.internal'; ServiceUri = $null }
			$Result = Test-WaypointSessionMatchesVCenter -Session $Session -VCenter 'vcsa-stub-target.example.internal' -NameResolver $Resolver

			# Per-name-unique stub addresses never collide, so this is a "no match" --
			# the point is that the STUB, not real DNS, produced it.
			$Result | Should -BeFalse
			$RecordedCalls | Should -Contain 'Forward:vcsa-stub-target.example.internal'
			$RecordedCalls | Should -Contain 'Forward:vcsa-stub-session.example.internal'
		}
	}
}

# Issue #1299: these two cases are the ONLY ones in this suite that touch the real
# resolver, and both assert "no answer" -- so they require a non-hijacking resolver
# (a wildcard resolver that synthesizes answers for `.invalid` fails them); run this
# suite on a resolver that does not manufacture records.
#
# Issue #1252: these two cases are the deliberate, documented opt-in real-DNS
# coverage the issue's proposed changes call for -- they exercise the actual
# try/catch in the two lowest-level resolver functions, which cannot be
# meaningfully mocked without testing nothing at all. Both now go through the
# bounded-async implementation (issue #1251), so a hung/blackholed resolver can
# no longer make either case pay more than ~3s (the default -TimeoutMilliseconds)
# instead of the OS resolver's own default timeout.
Describe 'Resolve-WaypointHostAddresses / Resolve-WaypointReverseHostNames (unmocked failure paths)' {
	It 'Resolve-WaypointHostAddresses returns an empty array instead of throwing for an unresolvable name' {
		InModuleScope WaypointDiscovery {
			$Result = $null
			{ $Result = Resolve-WaypointHostAddresses -HostNameOrAddress 'definitely-unresolvable.invalid' } | Should -Not -Throw
			$Result | Should -BeNullOrEmpty
		}
	}

	It 'Resolve-WaypointReverseHostNames returns an empty array instead of throwing for a non-address input' {
		InModuleScope WaypointDiscovery {
			$Result = $null
			{ $Result = Resolve-WaypointReverseHostNames -IpAddress 'not-an-ip-address' } | Should -Not -Throw
			$Result | Should -BeNullOrEmpty
		}
	}
}

Describe 'Resolve-WaypointPrimarySession' {
	It 'returns the lone session when the connection has only one, without requiring a name match' {
		InModuleScope WaypointDiscovery {
			Mock Resolve-WaypointHostAddresses { @() }

			$Session = [pscustomobject]@{ Name = 'vcsa-05.example.internal'; ServiceUri = $null }
			$Result = Resolve-WaypointPrimarySession -Sessions @($Session) -VCenter '203.0.113.50'
			$Result | Should -Be $Session
		}
	}

	It 'resolves the correct session by IP among two linked sessions (Enhanced Linked Mode, issue #1115 core case)' {
		InModuleScope WaypointDiscovery {
			Mock Resolve-WaypointHostAddresses {
				switch ($HostNameOrAddress) {
					'203.0.113.60' { @('203.0.113.60') }
					'vcsa-primary.example.internal' { @('203.0.113.60') }
					'vcsa-sibling.example.internal' { @('203.0.113.61') }
					default { @() }
				}
			}

			$Primary = [pscustomobject]@{ Name = 'vcsa-primary.example.internal'; ServiceUri = $null }
			$Sibling = [pscustomobject]@{ Name = 'vcsa-sibling.example.internal'; ServiceUri = $null }

			$Result = Resolve-WaypointPrimarySession -Sessions @($Primary, $Sibling) -VCenter '203.0.113.60'
			$Result | Should -Be $Primary
		}
	}

	It 'resolves a case/FQDN-vs-short-name difference among two linked sessions' {
		InModuleScope WaypointDiscovery {
			Mock Resolve-WaypointHostAddresses {
				switch ($HostNameOrAddress) {
					'vcsa-primary' { @('203.0.113.70') }
					'vcsa-primary.example.internal' { @('203.0.113.70') }
					'vcsa-sibling.example.internal' { @('203.0.113.71') }
					default { @() }
				}
			}

			$Primary = [pscustomobject]@{ Name = 'VCSA-Primary.Example.Internal'; ServiceUri = $null }
			$Sibling = [pscustomobject]@{ Name = 'vcsa-sibling.example.internal'; ServiceUri = $null }

			$Result = Resolve-WaypointPrimarySession -Sessions @($Primary, $Sibling) -VCenter 'vcsa-primary'
			$Result | Should -Be $Primary
		}
	}

	It 'stays absent and warns with the candidate count when no session matches (never guesses by position)' {
		InModuleScope WaypointDiscovery {
			Mock Resolve-WaypointHostAddresses { @() }
			Mock Resolve-WaypointReverseHostNames { @() }

			$SessionA = [pscustomobject]@{ Name = 'vcsa-c.example.internal'; ServiceUri = $null }
			$SessionB = [pscustomobject]@{ Name = 'vcsa-d.example.internal'; ServiceUri = $null }

			$Warnings = $null
			$Result = Resolve-WaypointPrimarySession -Sessions @($SessionA, $SessionB) -VCenter '203.0.113.80' -WarningVariable Warnings -WarningAction SilentlyContinue
			$Result | Should -BeNullOrEmpty
			$Warnings.Count | Should -Be 1
			$Warnings[0].Message | Should -Match '2 linked session'
			$Warnings[0].Message | Should -Match '0 matched'
		}
	}

	It 'stays absent and warns with the candidate count when more than one session matches (genuinely ambiguous)' {
		InModuleScope WaypointDiscovery {
			# Both sessions forward-resolve to the SAME address as the requested target
			# (e.g. a load-balanced or misreported ServiceUri) -- a real ambiguity
			# -AllLinked can produce, which must never be broken by picking "the first".
			Mock Resolve-WaypointHostAddresses { @('203.0.113.90') }

			$SessionA = [pscustomobject]@{ Name = 'vcsa-e.example.internal'; ServiceUri = $null }
			$SessionB = [pscustomobject]@{ Name = 'vcsa-f.example.internal'; ServiceUri = $null }

			$Warnings = $null
			$Result = Resolve-WaypointPrimarySession -Sessions @($SessionA, $SessionB) -VCenter '203.0.113.90' -WarningVariable Warnings -WarningAction SilentlyContinue
			$Result | Should -BeNullOrEmpty
			$Warnings.Count | Should -Be 1
			$Warnings[0].Message | Should -Match '2 linked session'
			$Warnings[0].Message | Should -Match '2 matched'
		}
	}
}

# Issue #1297: the bounded-lookup branch added for #1251 had no regression guard --
# it can only fire against a resolver that hangs, which no real name reliably does.
# Both cases drive the -*TaskFactory test seam: first with a task that COMPLETES (the
# control -- without it an empty result would prove nothing, since an unresolvable
# name returns empty too), then with one that never completes inside the deliberately
# tiny ceiling, asserting the documented contract -- a lookup that exceeds
# -TimeoutMilliseconds is treated exactly like a resolution failure: empty, no throw.
# No wall-clock assertion (timing assertions are a standing flake source, issue #658).
Describe 'Resolve-WaypointHostAddresses / Resolve-WaypointReverseHostNames (bounded timeout, issue #1251)' {
	It 'Resolve-WaypointHostAddresses returns empty instead of throwing when the lookup exceeds -TimeoutMilliseconds' {
		InModuleScope WaypointDiscovery {
			$Completes = { param($Name) [System.Threading.Tasks.Task]::FromResult([System.Net.IPAddress[]]@([System.Net.IPAddress]::Parse('198.51.100.7'))) }
			$Answered = @(Resolve-WaypointHostAddresses -HostNameOrAddress 'slow-resolver.example.internal' -TimeoutMilliseconds 5000 -AddressTaskFactory $Completes)
			$Answered | Should -Be @('198.51.100.7')

			$NeverCompletesInTime = { param($Name) [System.Threading.Tasks.Task]::Delay(60000) }
			$Result = $null
			$Warnings = $null
			{ $Result = Resolve-WaypointHostAddresses -HostNameOrAddress 'slow-resolver.example.internal' -TimeoutMilliseconds 50 -AddressTaskFactory $NeverCompletesInTime -WarningVariable Warnings -WarningAction SilentlyContinue } | Should -Not -Throw
			$Result | Should -BeNullOrEmpty
		}
	}

	It 'Resolve-WaypointReverseHostNames returns empty instead of throwing when the lookup exceeds -TimeoutMilliseconds' {
		InModuleScope WaypointDiscovery {
			$Entry = [System.Net.IPHostEntry]::new()
			$Entry.HostName = 'slow-resolver.example.internal'
			$Completes = { param($Address) [System.Threading.Tasks.Task]::FromResult($Entry) }.GetNewClosure()
			$Answered = @(Resolve-WaypointReverseHostNames -IpAddress '198.51.100.10' -TimeoutMilliseconds 5000 -HostEntryTaskFactory $Completes)
			$Answered | Should -Be @('slow-resolver.example.internal')

			$NeverCompletesInTime = { param($Address) [System.Threading.Tasks.Task]::Delay(60000) }
			$Result = $null
			$Warnings = $null
			{ $Result = Resolve-WaypointReverseHostNames -IpAddress '198.51.100.10' -TimeoutMilliseconds 50 -HostEntryTaskFactory $NeverCompletesInTime -WarningVariable Warnings -WarningAction SilentlyContinue } | Should -Not -Throw
			$Result | Should -BeNullOrEmpty
		}
	}
}

# Issue #1305: a DNS timeout must be observable, not just fail-closed -- these pin
# the Write-Warning this issue adds to both resolver functions' timeout branch,
# naming the lookup kind (forward/reverse) and the host, never a credential.
Describe 'Resolve-WaypointHostAddresses / Resolve-WaypointReverseHostNames (timeout observability, issue #1305)' {
	It 'Resolve-WaypointHostAddresses warns naming the host and ceiling when the lookup exceeds -TimeoutMilliseconds' {
		InModuleScope WaypointDiscovery {
			$NeverCompletesInTime = { param($Name) [System.Threading.Tasks.Task]::Delay(60000) }
			$Warnings = $null
			Resolve-WaypointHostAddresses -HostNameOrAddress 'slow-resolver.example.internal' -TimeoutMilliseconds 50 -AddressTaskFactory $NeverCompletesInTime -WarningVariable Warnings -WarningAction SilentlyContinue | Out-Null

			$Warnings.Count | Should -Be 1
			$Warnings[0].Message | Should -Match 'forward DNS'
			$Warnings[0].Message | Should -Match 'slow-resolver\.example\.internal'
			$Warnings[0].Message | Should -Match '50ms'
		}
	}

	It 'Resolve-WaypointHostAddresses does not warn when the lookup completes within the ceiling' {
		InModuleScope WaypointDiscovery {
			$Completes = { param($Name) [System.Threading.Tasks.Task]::FromResult([System.Net.IPAddress[]]@([System.Net.IPAddress]::Parse('198.51.100.7'))) }
			$Warnings = $null
			Resolve-WaypointHostAddresses -HostNameOrAddress 'fast-resolver.example.internal' -TimeoutMilliseconds 5000 -AddressTaskFactory $Completes -WarningVariable Warnings -WarningAction SilentlyContinue | Out-Null

			$Warnings.Count | Should -Be 0
		}
	}

	It 'Resolve-WaypointReverseHostNames warns naming the host and ceiling when the lookup exceeds -TimeoutMilliseconds' {
		InModuleScope WaypointDiscovery {
			$NeverCompletesInTime = { param($Address) [System.Threading.Tasks.Task]::Delay(60000) }
			$Warnings = $null
			Resolve-WaypointReverseHostNames -IpAddress '198.51.100.10' -TimeoutMilliseconds 50 -HostEntryTaskFactory $NeverCompletesInTime -WarningVariable Warnings -WarningAction SilentlyContinue | Out-Null

			$Warnings.Count | Should -Be 1
			$Warnings[0].Message | Should -Match 'reverse DNS'
			$Warnings[0].Message | Should -Match '198\.51\.100\.10'
			$Warnings[0].Message | Should -Match '50ms'
		}
	}
}

# Issue #1305: the ceiling is now operator-configurable end to end -- pins that
# Resolve-WaypointPrimarySession/Test-WaypointSessionMatchesVCenter actually forward
# a non-default -TimeoutMilliseconds down to the real resolver call, rather than the
# parameter existing but being silently dropped somewhere in the chain.
Describe 'Resolve-WaypointPrimarySession -TimeoutMilliseconds threading (issue #1305)' {
	# Issue #1322: BOTH legs are mocked, for two reasons. (1) Hermeticity: -VCenter is an
	# IP literal, so Resolve-WaypointPrimarySession also takes the *reverse* leg -- with
	# only the forward resolver mocked this test performed a real PTR lookup for
	# 203.0.113.99 against whatever resolver the test machine has, making it slow and
	# network-dependent. (2) Coverage: the reverse leg is half the threading chain this
	# test exists to pin, and nothing asserted it, so a -TimeoutMilliseconds dropped on
	# the Resolve-WaypointReverseHostNamesCached call would have passed.
	It 'forwards a non-default -TimeoutMilliseconds to both the forward and the reverse DNS resolver calls' {
		InModuleScope WaypointDiscovery {
			Mock Resolve-WaypointHostAddresses { @() }
			Mock Resolve-WaypointReverseHostNames { @() }

			$SessionA = [pscustomobject]@{ Name = 'vcsa-g.example.internal'; ServiceUri = $null }
			$SessionB = [pscustomobject]@{ Name = 'vcsa-h.example.internal'; ServiceUri = $null }

			Resolve-WaypointPrimarySession -Sessions @($SessionA, $SessionB) -VCenter '203.0.113.99' -TimeoutMilliseconds 12345 -WarningAction SilentlyContinue | Out-Null

			Should -Invoke Resolve-WaypointHostAddresses -ParameterFilter { $TimeoutMilliseconds -eq 12345 }
			Should -Invoke Resolve-WaypointReverseHostNames -ParameterFilter { $TimeoutMilliseconds -eq 12345 }
		}
	}
}

# Issue #1305 (review round 1): the ceiling became an operator-supplied number, and the
# values an operator is most likely to reach for defeat the guarantee it exists to
# provide -- silently. -1 is Timeout.Infinite, so Task.Wait(-1) waits for a blackholed
# resolver forever and the timeout branch (and its warning) never runs, reinstating
# exactly the unbounded stall #1251/#1297 removed; -2 and below throw
# ArgumentOutOfRangeException into the resolvers' deliberate fail-open
# `catch { return @() }`, disabling DNS matching for the whole pass with no warning at
# all; 0 makes every lookup time out instantly. [ValidateRange(1, 60000)] turns all of
# them into a loud bind-time failure. These cases pin that at the module boundary --
# the outermost entry point an operator's configuration actually reaches.
Describe 'DNS timeout bounds (issue #1305)' {
	It 'Invoke-WaypointDiscovery rejects a <Value>ms DNS ceiling at bind time' -ForEach @(
		@{ Value = -1 }
		@{ Value = 0 }
		@{ Value = -2 }
		@{ Value = 60001 }
	) {
		# Bind-time validation runs before the function body, so no vCenter, credential
		# or transport path is needed (and none is contacted) to prove the rejection.
		{
			Invoke-WaypointDiscovery -VCenter 'vcsa-bounds.example.internal' -Username 'svc' -Password 'unused' -DnsTimeoutMilliseconds $Value
		} | Should -Throw -ExpectedMessage '*DnsTimeoutMilliseconds*'
	}

	It 'Resolve-WaypointPrimarySession rejects a <Value>ms ceiling at bind time' -ForEach @(
		@{ Value = -1 }
		@{ Value = 0 }
	) {
		InModuleScope WaypointDiscovery -Parameters @{ Value = $Value } {
			param($Value)
			{
				Resolve-WaypointPrimarySession -Sessions @() -VCenter 'vcsa-bounds.example.internal' -TimeoutMilliseconds $Value
			} | Should -Throw -ExpectedMessage '*TimeoutMilliseconds*'
		}
	}

	It 'Resolve-WaypointHostAddresses rejects a <Value>ms ceiling before it can reach Task.Wait' -ForEach @(
		@{ Value = -1 }
		@{ Value = 0 }
	) {
		# The regression this pins concretely: with -1 and no validation, the measured
		# behaviour was a full 4s wait on a 4s task with no warning emitted. The factory
		# below would never be invoked now -- binding fails first.
		InModuleScope WaypointDiscovery -Parameters @{ Value = $Value } {
			param($Value)
			$NeverCompletesInTime = { param($Name) [System.Threading.Tasks.Task]::Delay(60000) }
			{
				Resolve-WaypointHostAddresses -HostNameOrAddress 'slow-resolver.example.internal' -TimeoutMilliseconds $Value -AddressTaskFactory $NeverCompletesInTime
			} | Should -Throw -ExpectedMessage '*TimeoutMilliseconds*'
		}
	}

	It 'Resolve-WaypointReverseHostNames rejects a <Value>ms ceiling before it can reach Task.Wait' -ForEach @(
		@{ Value = -1 }
		@{ Value = 0 }
	) {
		InModuleScope WaypointDiscovery -Parameters @{ Value = $Value } {
			param($Value)
			$NeverCompletesInTime = { param($Ip) [System.Threading.Tasks.Task]::Delay(60000) }
			{
				Resolve-WaypointReverseHostNames -IpAddress '198.51.100.10' -TimeoutMilliseconds $Value -HostEntryTaskFactory $NeverCompletesInTime
			} | Should -Throw -ExpectedMessage '*TimeoutMilliseconds*'
		}
	}

	It 'still accepts the in-range ceilings the rest of this suite and production depend on' {
		InModuleScope WaypointDiscovery {
			$Completes = { param($Name) [System.Threading.Tasks.Task]::FromResult([System.Net.IPAddress[]]@([System.Net.IPAddress]::Parse('198.51.100.20'))) }
			foreach ($InRange in @(1, 50, 3000, 60000)) {
				$Answered = @(Resolve-WaypointHostAddresses -HostNameOrAddress 'fast-resolver.example.internal' -TimeoutMilliseconds $InRange -AddressTaskFactory $Completes)
				$Answered | Should -Be @('198.51.100.20')
			}
		}
	}
}

# Issue #1323: DiscoverJobHandler binds a single fixed parameter dictionary against
# EITHER the real module or the Postgres end-to-end tests' stub, so a parameter added to
# the real module is a silent break of the stub -- the handler fails at bind time, in a
# test that looks unrelated. This PR's -DnsTimeoutMilliseconds was the second such
# drift. Pin the containment directly, by reflecting both surfaces with Get-Command,
# rather than rediscovering it one broken end-to-end test at a time.
Describe 'Stub discovery module parameter-surface contract (issue #1323)' {
	It 'the stub module accepts every parameter the real Invoke-WaypointDiscovery declares' {
		$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
		$StubPath = Join-Path $RepoRoot 'backend/Waypoint.Tests/Assets/WaypointDiscoveryStubModule/WaypointDiscoveryStubModule.psm1'
		Test-Path $StubPath | Should -BeTrue -Because 'the contract test must fail loudly if the stub module moves'

		$Common = [System.Management.Automation.PSCmdlet]::CommonParameters + [System.Management.Automation.PSCmdlet]::OptionalCommonParameters
		$RealParameters = @((Get-Command -Module WaypointDiscovery -Name Invoke-WaypointDiscovery).Parameters.Keys | Where-Object { $_ -notin $Common })

		Import-Module $StubPath -Force
		try {
			$StubParameters = @((Get-Command -Module WaypointDiscoveryStubModule -Name Invoke-WaypointDiscovery).Parameters.Keys | Where-Object { $_ -notin $Common })

			$RealParameters.Count | Should -BeGreaterThan 0
			$Missing = @($RealParameters | Where-Object { $_ -notin $StubParameters })
			$Missing -join ', ' | Should -BeNullOrEmpty -Because 'the stub must accept (it may ignore) every parameter DiscoverJobHandler can pass to the real module'
		} finally {
			Remove-Module WaypointDiscoveryStubModule -Force -ErrorAction SilentlyContinue
			Import-Module $ModulePath -Force
		}
	}
}
