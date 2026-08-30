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

# Waypoint-owned shim (issue #21, epic #13), following WaypointCatalogIndex's pattern
# (issue #194): this is the thin, Waypoint-authored seam that dot-sources the
# project-owned sibling repository's unmodified vmware-stig-docker
# module.transport.vmware.ps1 and
# adapts its Connect-StigVIServer/Get-StigTargets output to the flat
# cluster/host/vm shape DiscoverJobHandler needs.
#
# Only the discovery half of the sibling-repository module is used here (Connect-StigVIServer +
# a direct Get-View/Get-Cluster/Get-VMHost/Get-VM walk) -- Get-StigTargets itself
# builds InSpec-scan targets (profile paths, report dirs), which a pure inventory
# enumeration does not require. Re-implementing the cluster/host/VM walk directly
# against PowerCLI here (rather than trying to coerce Get-StigTargets' scan-target
# shape into an inventory tree) keeps this module honest about what it actually needs
# from PowerCLI: object identity (MoRef), name, parent, build, and (for hosts)
# maintenance mode.

$Script:VmwareStigDockerModulePath = $env:WAYPOINT_VMWARE_STIG_DOCKER_TRANSPORT_PATH

# Issue #1115: session-identity resolution helpers for the -AllLinked session set
# Connect-StigVIServer returns. Broken out as standalone module functions (rather
# than inlined in Invoke-WaypointDiscovery) so Pester can Mock the DNS-touching ones
# directly -- forward/reverse DNS resolution cannot be made deterministic any other
# way in a unit test, and a real lookup in CI would be both slow and flaky.
#
# ADR-0023 fail-closed rule: identity is never guessed. Every function below either
# proves an unambiguous match or reports "no match"/"empty" -- it never falls back to
# position (first/last session) and a DNS failure is swallowed (empty result), not
# thrown, so a resolver outage degrades to "no match" rather than failing the job.

# Issue #1251 (#1297): the single ceiling every real DNS lookup in this module is
# bounded by, named once here rather than duplicated as a literal in each resolver's
# -TimeoutMilliseconds default. Long enough that a healthy resolver always answers
# within it, short enough that a blackholed one cannot stall a discovery pass.
#
# Issue #1305 (decision recorded per #1297's carried-forward "decide" bullet): YES,
# the ceiling is operator-configurable -- Invoke-WaypointDiscovery accepts
# -DnsTimeoutMilliseconds (default this constant) and threads it through
# Resolve-WaypointPrimarySession/Test-WaypointSessionMatchesVCenter down to the two
# resolvers below. DiscoverJobHandler feeds it from
# PowerShellOptions.DiscoveryDnsTimeoutMilliseconds (appsettings/env, the same
# configuration path every other runner option in that class uses) rather than
# inventing a discovery-specific settings surface. A lookup that actually exceeds the
# ceiling now also emits a Write-Warning naming the lookup kind and host (never
# credentials -- a hostname/IP is non-secret inventory) so job.log can distinguish
# "resolver too slow" from "NXDOMAIN", both of which degrade to the same fail-closed
# "no match" outcome.
$script:WaypointDnsTimeoutMillisecondsDefault = 3000

# Normalizes a hostname/IP string for case- and trailing-dot-insensitive comparison
# (e.g. 'VCSA-01.Example.Internal.' and 'vcsa-01.example.internal' are the same
# identity). Returns $null for a blank/whitespace input so callers can filter it out
# without a separate null check.
function ConvertTo-WaypointNormalizedHost {
	param(
		[Parameter()]
		[AllowNull()]
		[string]$Value
	)

	if ([string]::IsNullOrWhiteSpace($Value)) {
		return $null
	}

	return $Value.Trim().TrimEnd('.').ToLowerInvariant()
}

# Collects the raw (un-normalized) hostname/IP candidates a single VI session
# exposes for identity comparison: its reported Name, plus the host component of its
# ServiceUri when present. PowerCLI's real VIServer session type reports ServiceUri
# as a System.Uri; test doubles may supply a plain string, so both are accepted.
function Get-WaypointSessionHostCandidates {
	param(
		[Parameter()]
		$Session
	)

	$Candidates = [System.Collections.Generic.List[string]]::new()

	if ($Session -and -not [string]::IsNullOrWhiteSpace($Session.Name)) {
		$Candidates.Add($Session.Name)
	}

	if ($Session -and $Session.ServiceUri) {
		try {
			$ParsedUri = $Session.ServiceUri -as [Uri]
			if ($ParsedUri -and -not [string]::IsNullOrWhiteSpace($ParsedUri.Host)) {
				$Candidates.Add($ParsedUri.Host)
			}
		} catch {
			# Not a parseable URI -- ignore this candidate source, Name is still tried.
		}
	}

	return $Candidates
}

# Forward-resolves a hostname (or parses an IP literal) to its address strings.
# Never throws: an unresolvable name, or a resolver outage, is reported as "no
# addresses" so callers degrade to "no match" rather than failing the discovery job.
#
# Issue #1251: uses the async overload with a bounded Wait() rather than the
# synchronous GetHostAddresses -- neither synchronous DNS overload accepts a
# timeout, so an unresponsive/blackholed resolver would otherwise run to the OS
# default before failing. A lookup that exceeds -TimeoutMilliseconds is treated
# exactly like a resolution failure: empty result, no throw.
function Resolve-WaypointHostAddresses {
	param(
		[Parameter(Mandatory)]
		[string]$HostNameOrAddress,

		[Parameter()]
		[int]$TimeoutMilliseconds = $script:WaypointDnsTimeoutMillisecondsDefault,

		# Issue #1297: test-only seam over the one .NET call in this function, so the
		# timeout branch below has a regression guard. Production callers never pass it.
		[Parameter()]
		[AllowNull()]
		[scriptblock]$AddressTaskFactory
	)

	try {
		$Task = if ($AddressTaskFactory) {
			& $AddressTaskFactory $HostNameOrAddress
		} else {
			[System.Net.Dns]::GetHostAddressesAsync($HostNameOrAddress)
		}
		if (-not $Task.Wait($TimeoutMilliseconds)) {
			# Issue #1305: the host name/IP is non-secret inventory (never a
			# credential), so it is safe to name here -- this is what lets an
			# operator distinguish "resolver too slow" from "NXDOMAIN" in job.log,
			# both of which otherwise degrade to the identical silent "no match".
			Write-Warning "WaypointDiscovery: forward DNS lookup for '$HostNameOrAddress' exceeded the ${TimeoutMilliseconds}ms timeout -- treating as unresolved."
			return @()
		}
		return @($Task.GetAwaiter().GetResult() | ForEach-Object { $_.ToString() })
	} catch {
		return @()
	}
}

# Reverse-resolves an IP address literal to the hostname(s) it maps to. Never
# throws, for the same reason as Resolve-WaypointHostAddresses above. Only
# meaningful when $IpAddress is actually an IP literal -- callers gate on that.
# Issue #1251: same bounded-async pattern as Resolve-WaypointHostAddresses.
function Resolve-WaypointReverseHostNames {
	param(
		[Parameter(Mandatory)]
		[string]$IpAddress,

		[Parameter()]
		[int]$TimeoutMilliseconds = $script:WaypointDnsTimeoutMillisecondsDefault,

		# Issue #1297: the same test-only seam as Resolve-WaypointHostAddresses above.
		[Parameter()]
		[AllowNull()]
		[scriptblock]$HostEntryTaskFactory
	)

	try {
		$Task = if ($HostEntryTaskFactory) {
			& $HostEntryTaskFactory $IpAddress
		} else {
			[System.Net.Dns]::GetHostEntryAsync($IpAddress)
		}
		if (-not $Task.Wait($TimeoutMilliseconds)) {
			# Issue #1305: same non-secret-host rationale as Resolve-WaypointHostAddresses.
			Write-Warning "WaypointDiscovery: reverse DNS lookup for '$IpAddress' exceeded the ${TimeoutMilliseconds}ms timeout -- treating as unresolved."
			return @()
		}
		$Entry = $Task.GetAwaiter().GetResult()
		if ($Entry -and -not [string]::IsNullOrWhiteSpace($Entry.HostName)) {
			return @($Entry.HostName)
		}
		return @()
	} catch {
		return @()
	}
}

# Issue #1251 (memoization) + #1252 (test hermeticity seam): resolves a
# hostname/IP's forward addresses through, in priority order, an injected
# -NameResolver stub, a per-Resolve-WaypointPrimarySession-call -Cache, or (only
# when neither is available) the real DNS-touching Resolve-WaypointHostAddresses.
# -NameResolver is a scriptblock invoked as `& $NameResolver 'Forward' $Value`;
# tests use it to keep Test-WaypointSessionMatchesVCenter/Resolve-WaypointPrimarySession
# fully hermetic without ever touching the network. Production callers never pass
# -NameResolver, so the real-DNS path (with its own memoized cache, bounded by
# -Cache to the lifetime of one Resolve-WaypointPrimarySession call) is unchanged.
function Resolve-WaypointHostAddressesCached {
	param(
		[Parameter(Mandatory)]
		[string]$HostNameOrAddress,

		[Parameter()]
		[AllowNull()]
		[scriptblock]$NameResolver,

		[Parameter()]
		[AllowNull()]
		[hashtable]$Cache,

		# Issue #1305: forwarded to the real resolver only; ignored (as with every
		# other real-DNS parameter here) when -NameResolver or -Cache already answers.
		[Parameter()]
		[int]$TimeoutMilliseconds = $script:WaypointDnsTimeoutMillisecondsDefault
	)

	if ($NameResolver) {
		return @(& $NameResolver 'Forward' $HostNameOrAddress)
	}

	if ($Cache -and $Cache.ContainsKey($HostNameOrAddress)) {
		return $Cache[$HostNameOrAddress]
	}

	$Result = @(Resolve-WaypointHostAddresses -HostNameOrAddress $HostNameOrAddress -TimeoutMilliseconds $TimeoutMilliseconds)
	if ($Cache) {
		$Cache[$HostNameOrAddress] = $Result
	}
	return $Result
}

# The reverse-resolution counterpart to Resolve-WaypointHostAddressesCached --
# same -NameResolver/-Cache precedence, invoked as `& $NameResolver 'Reverse' $Value`.
function Resolve-WaypointReverseHostNamesCached {
	param(
		[Parameter(Mandatory)]
		[string]$IpAddress,

		[Parameter()]
		[AllowNull()]
		[scriptblock]$NameResolver,

		[Parameter()]
		[AllowNull()]
		[hashtable]$Cache,

		# Issue #1305: same forwarding rule as Resolve-WaypointHostAddressesCached.
		[Parameter()]
		[int]$TimeoutMilliseconds = $script:WaypointDnsTimeoutMillisecondsDefault
	)

	if ($NameResolver) {
		return @(& $NameResolver 'Reverse' $IpAddress)
	}

	if ($Cache -and $Cache.ContainsKey($IpAddress)) {
		return $Cache[$IpAddress]
	}

	$Result = @(Resolve-WaypointReverseHostNames -IpAddress $IpAddress -TimeoutMilliseconds $TimeoutMilliseconds)
	if ($Cache) {
		$Cache[$IpAddress] = $Result
	}
	return $Result
}

# Issue #1115: does $Session identify the appliance the caller addressed as
# -VCenter? Tries, in order, entirely without ever guessing by position:
#   1. Direct compare: normalized $VCenter against normalized Name/ServiceUri host.
#   2. Forward DNS: resolve both $VCenter and each session candidate to addresses;
#      match if the address sets intersect (this is what lets an IP-addressed
#      target match a session reporting only its FQDN, and vice versa).
#   3. Reverse DNS: when $VCenter is itself an IP literal, resolve it to a
#      hostname and compare against the session's normalized candidates.
# Each step degrades to "no match" (never throws) on a DNS failure, per the two
# resolver functions above.
function Test-WaypointSessionMatchesVCenter {
	param(
		[Parameter()]
		$Session,

		[Parameter(Mandatory)]
		[string]$VCenter,

		# Issue #1252: optional test seam. When set, forward/reverse resolution goes
		# through this scriptblock (`& $NameResolver 'Forward'|'Reverse' $Value`)
		# instead of real DNS -- production callers never set it. Issue #1251: when
		# unset, -ForwardCache/-ReverseCache memoize real DNS lookups for the
		# duration of one Resolve-WaypointPrimarySession sweep.
		[Parameter()]
		[AllowNull()]
		[scriptblock]$NameResolver = $null,

		[Parameter()]
		[AllowNull()]
		[hashtable]$ForwardCache = $null,

		[Parameter()]
		[AllowNull()]
		[hashtable]$ReverseCache = $null,

		# Issue #1305: forwarded to the real resolvers via the Cached wrappers above.
		[Parameter()]
		[int]$TimeoutMilliseconds = $script:WaypointDnsTimeoutMillisecondsDefault
	)

	$RequestedNormalized = ConvertTo-WaypointNormalizedHost -Value $VCenter
	if (-not $RequestedNormalized) {
		return $false
	}

	$SessionCandidatesRaw = @(Get-WaypointSessionHostCandidates -Session $Session)
	$SessionCandidatesNormalized = @($SessionCandidatesRaw | ForEach-Object { ConvertTo-WaypointNormalizedHost -Value $_ } | Where-Object { $_ })

	if ($SessionCandidatesNormalized -contains $RequestedNormalized) {
		return $true
	}

	if ($SessionCandidatesNormalized.Count -eq 0) {
		return $false
	}

	$RequestedAddresses = @(Resolve-WaypointHostAddressesCached -HostNameOrAddress $VCenter -NameResolver $NameResolver -Cache $ForwardCache -TimeoutMilliseconds $TimeoutMilliseconds)
	if ($RequestedAddresses.Count -gt 0) {
		foreach ($Candidate in $SessionCandidatesRaw) {
			$CandidateAddresses = @(Resolve-WaypointHostAddressesCached -HostNameOrAddress $Candidate -NameResolver $NameResolver -Cache $ForwardCache -TimeoutMilliseconds $TimeoutMilliseconds)
			foreach ($Address in $CandidateAddresses) {
				if ($RequestedAddresses -contains $Address) {
					return $true
				}
			}
		}
	}

	$ParsedIp = $null
	if ([System.Net.IPAddress]::TryParse($VCenter, [ref]$ParsedIp)) {
		$ReverseNames = @(Resolve-WaypointReverseHostNamesCached -IpAddress $VCenter -NameResolver $NameResolver -Cache $ReverseCache -TimeoutMilliseconds $TimeoutMilliseconds | ForEach-Object { ConvertTo-WaypointNormalizedHost -Value $_ } | Where-Object { $_ })
		if ($ReverseNames.Count -gt 0) {
			foreach ($Name in $ReverseNames) {
				if ($SessionCandidatesNormalized -contains $Name) {
					return $true
				}
			}
		}
	}

	return $false
}

# Issue #1115: resolves the requested -VCenter to exactly one -AllLinked session, or
# to none at all -- the fail-closed rule ADR-0023 and epic #726 section 3 require.
# Never picks by position: the only fallback is "there was only one session in the
# entire connection", which is unambiguous by construction rather than a guess.
# When resolution fails (zero or more than one identity match among 2+ sessions), a
# structured warning names the candidate count so an operator reading the job log
# can see why the vcenter identity was withheld, without this function itself
# deciding whether that is fatal to the run -- Invoke-WaypointDiscovery already
# treats an absent vcenter row as a normal, non-failing outcome.
function Resolve-WaypointPrimarySession {
	[CmdletBinding()]
	param(
		[Parameter()]
		[AllowNull()]
		$Sessions,

		[Parameter(Mandatory)]
		[string]$VCenter,

		# Issue #1252: propagated to Test-WaypointSessionMatchesVCenter -- see its
		# comment. $null (the default) means "use real DNS", unchanged from before.
		[Parameter()]
		[AllowNull()]
		[scriptblock]$NameResolver = $null,

		# Issue #1305: propagated to Test-WaypointSessionMatchesVCenter/the real
		# resolvers. Default is the module-wide ceiling; Invoke-WaypointDiscovery is
		# the only production caller that overrides it, from operator configuration.
		[Parameter()]
		[int]$TimeoutMilliseconds = $script:WaypointDnsTimeoutMillisecondsDefault
	)

	$AllSessions = @($Sessions)

	if ($AllSessions.Count -eq 1) {
		return $AllSessions[0]
	}

	# Issue #1251: one forward/reverse cache per call, shared across every session
	# in the sweep below, so $VCenter and any repeated candidate name/address is
	# resolved at most once instead of once per session.
	$ForwardCache = @{}
	$ReverseCache = @{}
	$MatchedSessions = @($AllSessions | Where-Object {
		Test-WaypointSessionMatchesVCenter -Session $_ -VCenter $VCenter -NameResolver $NameResolver -ForwardCache $ForwardCache -ReverseCache $ReverseCache -TimeoutMilliseconds $TimeoutMilliseconds
	})

	if ($MatchedSessions.Count -eq 1) {
		return $MatchedSessions[0]
	}

	Write-Warning "WaypointDiscovery: could not uniquely identify the vCenter session for '$VCenter' among $($AllSessions.Count) linked session(s) ($($MatchedSessions.Count) matched by name/address) -- withholding vcenter identity for this target rather than guessing."
	return $null
}

function Invoke-WaypointDiscovery {
	<#
	.SYNOPSIS
	    Connects to a vCenter (AllLinked) and enumerates clusters/hosts/VMs into the
	    flat item list DiscoverJobHandler upserts into inventory_items.

	.PARAMETER VCenter
	    FQDN or IP of the vCenter Server (target.connection.host).

	.PARAMETER Username
	    vSphere SSO username, decrypted credential username half.

	.PARAMETER Password
	    vSphere SSO password, bound as a typed parameter -- never interpolated into
	    script text (security.md controls 1/2).

	.PARAMETER VmwareStigDockerTransportPath
	    Path to the sibling repo's unmodified module.transport.vmware.ps1, dot-sourced
	    to bring Connect-StigVIServer into scope.

	.PARAMETER DnsTimeoutMilliseconds
	    Issue #1305: ceiling for every DNS lookup performed while resolving the
	    vcenter session identity (see Resolve-WaypointPrimarySession). Defaults to
	    $script:WaypointDnsTimeoutMillisecondsDefault (3000ms); DiscoverJobHandler
	    overrides it from PowerShellOptions.DiscoveryDnsTimeoutMilliseconds
	    (docs/architecture.md) so an operator on a genuinely slow resolver can raise
	    it without editing the shipped module. A lookup that exceeds the ceiling
	    emits a Write-Warning naming the lookup kind and host (never credentials)
	    before degrading to the same fail-closed "no match" outcome a genuine
	    resolution failure produces.

	.OUTPUTS
	    One [pscustomobject] per discovered item: Type ('vcenter'|'cluster'|'host'|'vm'),
	    MoRef, Name, ParentMoRef (nullable), Build (nullable), Version (nullable),
	    MaintenanceMode (nullable bool), InstanceUuid (nullable, issue #1063, 'vm' rows
	    only). The 'vcenter' row (issue #1081) is always first; clusters and hosts are
	    emitted before their children.

	    Issue #974: Version is the host's semantic vSphere product version
	    ($VMHost.Version, e.g. "8.0.3") -- populated only for 'host' rows, alongside
	    (never instead of) Build, which continues to be captured/reported exactly as
	    before. 'cluster'/'vm' rows always report Version = $null.

	    Issue #1081: the 'vcenter' row reports the appliance's own identity/version --
	    MoRef is $Session.InstanceUuid (vSphere's `content.about.instanceUuid`, the
	    only authoritative, stable identifier the appliance itself exposes -- the same
	    property module.targets.ps1 already keys linked-vCenter claims on), Version/
	    Build are $Session.Version/.Build (`content.about.version`/`.build`). Emitted
	    for the ONE session matching the requested -VCenter (never every AllLinked
	    sibling, which would misattribute another vCenter's identity to this target's
	    root), and ONLY when that session actually supplies a non-blank InstanceUuid
	    and Name. Issue #1115: matching is normalized name/ServiceUri comparison plus
	    forward/reverse DNS resolution (see Resolve-WaypointPrimarySession), not exact
	    string equality, so an IP-addressed or FQDN-vs-short-name target still
	    resolves its own appliance. When the match cannot be made unique -- or the
	    matched session cannot supply both fields -- no 'vcenter' row is emitted at
	    all: the root stays honestly identity- and version-absent (with a
	    Write-Warning naming the candidate count for an ambiguous/absent match) and
	    the rest of the pass is unaffected, rather than a blank MoRef/Name failing the
	    whole discovery job through TryParseItem/ParseDiscoveredItems (#618).
	    A 'vm' row's Build is always $null -- it used to carry the VMware Tools
	    version, which is not a product-version fact for the VM itself and would
	    mislead anything reading Build as a platform fact; the VM's own platform
	    version fact is derived from its parent vcenter row by
	    DiscoverJobHandler.MapToComponents (issue #1063), never reported here.

	    Issue #1063: a 'vm' row's InstanceUuid is vSphere's
	    `Config.InstanceUuid` -- the appliance-assigned, migration-stable identifier
	    that deconflicts identically named VMs across discovery passes. $null for
	    every non-'vm' row.

	    Issue #865 (ADR-0023 completeness gap): the default $ErrorActionPreference is
	    'Continue', so a non-terminating PowerCLI error on one subtree (an unreachable
	    ESXi host, a permission-denied cluster) does not stop the pipeline -- it emits
	    whatever it COULD enumerate and returns, which used to look identical to a
	    genuinely complete pass. This function now wraps each subtree walk (per-cluster
	    and the standalone-host sweep) in its own try/catch with
	    -ErrorAction Stop scoped to just that walk, so a subtree failure is caught
	    locally (its objects are simply skipped) rather than left as an ambient
	    non-terminating error the caller can only infer from HadErrors. The walk emits
	    exactly one trailing completeness-marker record --
	    Type = 'discovery-meta', Complete (bool), Errors (string[]) -- as the LAST
	    output object, giving the C# side an explicit, structured completeness signal
	    instead of a heuristic over HadErrors/streams.
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

		[Parameter()]
		[string]$VmwareStigDockerTransportPath = $Script:VmwareStigDockerModulePath,

		# Issue #1252: test-only seam, never set by the real compliance runner. When
		# supplied, Resolve-WaypointPrimarySession's forward/reverse DNS resolution
		# is routed through this scriptblock instead of real DNS, so a test driving
		# this function with fake sessions never touches the network. Signature:
		# `& $NameResolver 'Forward'|'Reverse' $Value` returning a string[] (empty
		# array for "no match", matching the real resolvers' fail-closed contract).
		[Parameter()]
		[AllowNull()]
		[scriptblock]$NameResolver = $null,

		# Issue #1305: operator-configurable ceiling for every DNS lookup
		# Resolve-WaypointPrimarySession performs while identifying the vcenter
		# session. DiscoverJobHandler feeds this from
		# PowerShellOptions.DiscoveryDnsTimeoutMilliseconds; unset, it is the
		# module's own $script:WaypointDnsTimeoutMillisecondsDefault (3000ms).
		[Parameter()]
		[int]$DnsTimeoutMilliseconds = $script:WaypointDnsTimeoutMillisecondsDefault
	)

	if ([string]::IsNullOrWhiteSpace($VmwareStigDockerTransportPath)) {
		throw 'WaypointDiscovery: no module.transport.vmware.ps1 path configured (WAYPOINT_VMWARE_STIG_DOCKER_TRANSPORT_PATH or -VmwareStigDockerTransportPath).'
	}

	if (-not (Test-Path -Path $VmwareStigDockerTransportPath -PathType Leaf)) {
		throw "WaypointDiscovery: module.transport.vmware.ps1 not found at '$VmwareStigDockerTransportPath'."
	}

	# Dot-source the unmodified sibling-repository script to bring Connect-StigVIServer into scope.
	. $VmwareStigDockerTransportPath

	$SecurePassword = ConvertTo-SecureString -String $Password -AsPlainText -Force
	$Credential = [pscredential]::new($Username, $SecurePassword)

	Write-Information "Connecting to vCenter '$VCenter' for discovery..."
	# Issue #580: discovery is an API-only operation -- it never walks VCSA over SSH,
	# so it must never resolve or prompt for a VCSA credential. -SkipVCSACredential
	# tells the shared Connect-StigVIServer (module.transport.vmware.ps1, which
	# carries this one small parameterization -- see its README/NOTICE) to skip that
	# resolution/prompt entirely rather than falling into Get-Credential, which can
	# never succeed in the noninteractive compliance runner.
	$Connection = Connect-StigVIServer -VCenter $VCenter -VSphereCredential $Credential -SkipVCSACredential -Source 'Discovery'

	# Issue #1081: the appliance's own identity/version fact. $Session.InstanceUuid is
	# vSphere's authoritative, stable instance identifier (content.about.instanceUuid)
	# -- the same property module.targets.ps1 already keys linked-vCenter claims on --
	# and $Session.Version/.Build are the appliance's own semantic version/build
	# (content.about.version/.build), never the ESXi hosts' values. Only the ONE
	# session matching the requested -VCenter is used: -AllLinked can return sibling
	# vCenters too, and attributing one of THEIR identities to THIS target's root would
	# be exactly the guessed-identity failure ADR-0023 forbids.
	#
	# Issue #1081 (round-1 review, major 3): the row is emitted ONLY when the identity
	# and name are both actually present, and it is the LAST thing decided rather than
	# an unconditional emission. Two reasons, both fail-closed:
	#
	#   * TryParseItem rejects a blank MoRef/Name and ParseDiscoveredItems counts that
	#     as malformed, which DiscoverJobHandler turns into a FAILED discovery job
	#     (issue #618's no-silent-success rule). Emitting the row unconditionally
	#     therefore meant an appliance that reported a blank instanceUuid would take
	#     the WHOLE pass down -- cluster/host/VM inventory included -- rather than
	#     leaving the root honestly identity-less. Epic #726 section 3 wants an absent
	#     fact to be absent and the component skipped, never a failed pass, so a
	#     $PrimarySession that cannot supply both fields yields NO vcenter row at all
	#     (which the mapper already handles: the root simply stays identity- and
	#     version-absent, and everything else about the pass is unaffected).
	#
	#   * Issue #1115: exact `-eq $VCenter` name equality missed an Enhanced Linked
	#     Mode target addressed by IP (or any non-byte-identical form: short name vs
	#     FQDN, case difference) -- with more than one linked session, no name match
	#     meant no vcenter row at all even though the appliance WAS one of the
	#     sessions. Resolve-WaypointPrimarySession replaces exact-name matching with
	#     normalized name/ServiceUri comparison plus forward/reverse DNS (see its own
	#     comment above), while keeping the same fail-closed contract: it never picks
	#     a session by position, and an ambiguous or absent match returns $null (a
	#     structured Write-Warning explains why) rather than guessing.
	$PrimarySession = Resolve-WaypointPrimarySession -Sessions $Connection.Sessions -VCenter $VCenter -NameResolver $NameResolver -TimeoutMilliseconds $DnsTimeoutMilliseconds

	if ($PrimarySession -and -not [string]::IsNullOrWhiteSpace($PrimarySession.InstanceUuid) -and -not [string]::IsNullOrWhiteSpace($PrimarySession.Name)) {
		[pscustomobject]@{
			Type            = 'vcenter'
			MoRef           = $PrimarySession.InstanceUuid
			Name            = $PrimarySession.Name
			ParentMoRef     = $null
			Build           = $PrimarySession.Build
			Version         = $PrimarySession.Version
			MaintenanceMode = $null
		}
	}

	# Issue #865: collected per-subtree failure messages, surfaced verbatim (already
	# PowerCLI's own text, no secrets ever flow through this path) in the trailing
	# discovery-meta record so DiscoverJobHandler can raise them as the pass's
	# partial-failure diagnostic rather than the caller having to re-derive "why" from
	# HadErrors/streams alone.
	$EnumerationErrors = [System.Collections.Generic.List[string]]::new()

	try {
		foreach ($Session in $Connection.Sessions) {
			foreach ($Cluster in (Get-Cluster -Server $Session)) {
				[pscustomobject]@{
					Type            = 'cluster'
					MoRef           = $Cluster.ExtensionData.MoRef.Value
					Name            = $Cluster.Name
					ParentMoRef     = $null
					Build           = $null
					Version         = $null
					MaintenanceMode = $null
				}

				# Each cluster's host/VM walk is its own completeness boundary: a
				# permission-denied or unreachable subtree under ONE cluster must not
				# silently look identical to a fully-enumerated pass. -ErrorAction Stop
				# turns what PowerCLI would otherwise report as a non-terminating error
				# (pipeline keeps going, HadErrors set) into a terminating one scoped to
				# just this try, caught here and recorded -- the rest of the vCenter is
				# still walked.
				try {
					foreach ($VMHost in (Get-VMHost -Server $Session -Location $Cluster -ErrorAction Stop)) {
						[pscustomobject]@{
							Type            = 'host'
							MoRef           = $VMHost.ExtensionData.MoRef.Value
							Name            = $VMHost.Name
							ParentMoRef     = $Cluster.ExtensionData.MoRef.Value
							Build           = $VMHost.Build
							Version         = $VMHost.Version
							MaintenanceMode = ($VMHost.ConnectionState -eq 'Maintenance')
						}

						try {
							foreach ($VM in (Get-VM -Server $Session -Location $VMHost -ErrorAction Stop)) {
								[pscustomobject]@{
									Type            = 'vm'
									MoRef           = $VM.ExtensionData.MoRef.Value
									Name            = $VM.Name
									ParentMoRef     = $VMHost.ExtensionData.MoRef.Value
									# Issue #1081: never the VMware Tools version -- not a product-version
									# fact for the VM itself; would mislead anything reading Build as a
									# platform fact. The VM's own platform version is derived from its
									# parent vcenter's fact by DiscoverJobHandler.MapToComponents (#1063),
									# never reported here.
									Build           = $null
									Version         = $null
									# Issue #1063: the VM's authoritative, migration-stable vSphere instance
									# UUID -- deconflicts identically named VMs across discovery passes.
									# $VM.ExtensionData.Config can legitimately be $null for some VM states
									# (e.g. an invalid/orphaned object); property access on $null returns
									# $null here rather than throwing (no Set-StrictMode in this module).
									InstanceUuid    = $VM.ExtensionData.Config.InstanceUuid
									MaintenanceMode = $null
								}
							}
						} catch {
							$EnumerationErrors.Add("VM enumeration failed under host '$($VMHost.Name)' (cluster '$($Cluster.Name)'): $($_.Exception.Message)")
						}
					}
				} catch {
					$EnumerationErrors.Add("Host enumeration failed under cluster '$($Cluster.Name)': $($_.Exception.Message)")
				}
			}

			# Hosts with no cluster (standalone) belong directly to the datacenter --
			# emitted with a null ParentMoRef so they appear at the tree's top level,
			# same as a cluster does. Same per-subtree completeness boundary as above.
			try {
				foreach ($VMHost in (Get-VMHost -Server $Session -ErrorAction Stop | Where-Object { -not $_.Parent -or $_.Parent -isnot [VMware.VimAutomation.ViCore.Types.V1.Inventory.Cluster] })) {
					[pscustomobject]@{
						Type            = 'host'
						MoRef           = $VMHost.ExtensionData.MoRef.Value
						Name            = $VMHost.Name
						ParentMoRef     = $null
						Build           = $VMHost.Build
						Version         = $VMHost.Version
						MaintenanceMode = ($VMHost.ConnectionState -eq 'Maintenance')
					}

					try {
						foreach ($VM in (Get-VM -Server $Session -Location $VMHost -ErrorAction Stop)) {
							[pscustomobject]@{
								Type            = 'vm'
								MoRef           = $VM.ExtensionData.MoRef.Value
								Name            = $VM.Name
								ParentMoRef     = $VMHost.ExtensionData.MoRef.Value
								# Issue #1081: never the VMware Tools version -- see comment above.
								Build           = $null
								Version         = $null
								# Issue #1063: see comment above.
								InstanceUuid    = $VM.ExtensionData.Config.InstanceUuid
								MaintenanceMode = $null
							}
						}
					} catch {
						$EnumerationErrors.Add("VM enumeration failed under standalone host '$($VMHost.Name)': $($_.Exception.Message)")
					}
				}
			} catch {
				$EnumerationErrors.Add("Standalone host enumeration failed: $($_.Exception.Message)")
			}
		}
	} finally {
		if ($Connection.DisconnectAtCleanup) {
			Write-Information "Disconnecting from vCenter '$VCenter' after discovery."
			Disconnect-VIServer -Server $Connection.Sessions -Confirm:$false -ErrorAction SilentlyContinue
		}
	}

	# Issue #865: the explicit completeness signal. Always the LAST object emitted, so
	# DiscoverJobHandler can pull it off the end of the output list without scanning:
	# Complete = $false whenever ANY subtree above was skipped due to a caught error --
	# a genuinely empty vCenter (no clusters, no standalone hosts, zero errors) still
	# reports Complete = $true, matching the "genuinely empty" success case #618
	# already established for the item list itself.
	[pscustomobject]@{
		Type     = 'discovery-meta'
		Complete = ($EnumerationErrors.Count -eq 0)
		Errors   = $EnumerationErrors.ToArray()
	}
}

Export-ModuleMember -Function Invoke-WaypointDiscovery
