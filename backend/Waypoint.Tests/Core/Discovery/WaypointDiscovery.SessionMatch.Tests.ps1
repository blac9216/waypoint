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
			{ $Result = Resolve-WaypointHostAddresses -HostNameOrAddress 'slow-resolver.example.internal' -TimeoutMilliseconds 50 -AddressTaskFactory $NeverCompletesInTime } | Should -Not -Throw
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
			{ $Result = Resolve-WaypointReverseHostNames -IpAddress '198.51.100.10' -TimeoutMilliseconds 50 -HostEntryTaskFactory $NeverCompletesInTime } | Should -Not -Throw
			$Result | Should -BeNullOrEmpty
		}
	}
}
