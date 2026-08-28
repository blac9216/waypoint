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

# Issue #40 (epic #558), backend slice: the compliance-content working tree manager.
# Clones (first pull) or fetches (subsequent pulls) the VMware DoD compliance-content
# repository into ContentPath, checks out RefValue (a tag or branch), and enumerates
# InSpec profiles found under it. Plain `git` shell-out (no vendor SDK dependency),
# matching the project's existing "PowerShell wraps a well-known CLI tool" pattern
# (WaypointCatalogIndex wraps filesystem walks; a future remediation handler wraps
# PowerCLI/Ansible the same way). No credential is threaded through this slice -- the
# repository is treated as a public/unauthenticated clone target, consistent with
# ADR-0013 Decision 4 "a child pwsh remains permitted where process isolation is
# required"; a private/token-gated content source is out of scope for this PR (see the
# PR body's "remaining scope" note).
#
# Issue #598: each profile object additionally carries a Controls array (one entry per
# InSpec control file under the profile's controls/ directory), feeding
# `GET /profiles/{id}/controls`. Parsing is regex-based, not a Ruby parser -- InSpec
# control files are Ruby DSL, but this only needs three well-known top-level calls
# (`control 'id' do`, `title '...'`, `impact N` / `tag('severity', '...')`/
# `tag severity: '...'`), the same "read only what this needs" discipline
# HdfSeverityCounts/AttestationYaml already establish on the C# side. A control file
# this cannot parse contributes nothing rather than failing the whole pull (issue #598
# AC "malformed control files must not fail the pull").
#
# Issue #729 remainder: `inspec check` bounded runner work. The compliance-runner image
# ships cinc-auditor with an `/usr/local/bin/inspec` alias (runners/compliance-runner/
# Dockerfile), so the REAL binary runs here in-image; InSpec/cinc-auditor is not a
# licensed/account-gated tool like VCFDT, so there is no legal bar on real execution --
# only a staging one (a CI image without the toolchain simply reports Ran=$false, see
# Test-WaypointInspecCheck below). CI/unit tests instead drive an invented stub script
# that mirrors `inspec check`'s publicly documented CLI contract (exit 0 on a
# structurally valid profile, non-zero + JSON diagnostics otherwise), the same
# "faithful argument contract, invented content" discipline docs/testing.md's VCFDT
# section establishes for a different (licensed) tool. Execution is bounded: a wall-
# clock timeout and a captured-output size cap (issue #729 AC "bounded runner work") so
# one hung or pathological profile cannot stall or memory-balloon the whole content-pull
# job attempt.
#
# Issue #984 (live-proven, epic #726 round 3): the ORIGINAL bound used
# `Start-Job`/`Wait-Job`, which depends on PowerShell's background-job subsystem (a
# child-process launcher plus the job/event pump) that is only wired up in a full `pwsh`
# host. The compliance-runner hosts PowerShell in-process via a hand-rolled SMA runspace
# pool (`WaypointRunspacePool`, `InitialSessionState.CreateDefault2()`, ADR-0013/0014),
# which never wires that subsystem up -- `Wait-Job` never observed the job completing, so
# EVERY invocation hit the 60s bound and every valid profile was fail-closed quarantined
# (net promoted content = 0). The bound is now built directly on
# `System.Diagnostics.Process` + `WaitForExit(timeoutMilliseconds)`: `inspec` is always an
# external executable regardless of host, so a plain process start/wait/kill-on-timeout is
# host-agnostic -- it depends on nothing but the .NET process APIs already available
# inside any SMA runspace (embedded or full-`pwsh`), proven end to end by
# ContentPullJobHandlerTests' `Execute_RealExecutor_FixtureContentTree_StagesAndPromotesProfiles`
# (PR #975's real-in-process-host pattern), which now drives this exact function.
#
# Issue #617: the real vmware/dod-compliance-and-automation repo nests its ~385 InSpec
# profiles many directories deep (e.g.
# ".../vidm/3.3.x/v1r3-srg/inspec/vmware-vidm-3.3.x-stig-baseline/postgresql/inspec.yml"),
# and many leaf directory names collide across baselines (multiple "postgresql", "aaa",
# "tomcat" profiles under different parents). ProfileKey is therefore the
# ContentPath-relative profile directory path (forward-slash normalized -- collision-free
# and stable across re-pulls of the same layout), not the bare basename:
# profiles.profile_key is UNIQUE (0035) and ReplaceAllAsync/ContentPullJobHandler
# upsert/dedup by it, so a non-unique key was silently collapsing distinct profiles into
# one stored row (385 -> 91). Name still falls back to the basename for display
# friendliness; inspec.yml title parsing remains deferred (see #595 -- that issue defers
# *title* parsing, not key uniqueness, which must hold today regardless). The profile's
# real, already-known directory (not a path recomputed from the key) is threaded
# straight into control discovery so nested controls/*.rb resolve regardless of depth.
#
# Issue #993 (epic #726, live-proven): #989 made each `inspec check` genuinely complete
# (~20s each), which exposed the NEXT bound -- the whole pull, including every leaf's
# check, used to run as ONE PowerShell invocation under PowerShellExecutor's fixed
# 00:30:00 DefaultInvocationTimeout (PowerShellOptions.cs). The real content tree's
# aggregate check time (hundreds of leaves x ~20s) exceeds that wall clock by a wide
# margin; the pipeline is force-stopped, Stop() is ignored (a WaitForExit/Process.Kill
# in flight cannot be pre-empted instantly), and the runspace is poisoned -- while
# ContentPullJobHandler stages atomically at the end, so the force-stop discards
# everything (0 profiles/benchmarks/baselines).
#
# Fix shape: split the single monolithic invocation into a git-only sync/enumerate call
# (Sync-WaypointComplianceContentTree, cheap and leaf-count-independent) plus N
# per-CHUNK check-and-assemble calls (Get-WaypointComplianceContentEntries) that
# ContentPullJobHandler drives directly. Each chunk call's own PowerShellRequest.Timeout
# is sized as (chunk leaf count x per-check TimeoutSeconds) + a fixed overhead -- NOT a
# bigger magic constant on the whole tree -- so the bound is structurally sufficient for
# any content size: doubling the number of leaves doubles the number of (still small,
# fixed-size) chunk calls, never the size of any single bounded invocation. The
# C#-side loop checks the job's own CancellationToken between chunks (ADR-0008 run-abort
# cancellation), so a stop request is honored between bounded units -- the same
# "ignored Stop() for 5s; poisoned" fallout literally cannot recur, because a stop is
# observed as "do not start the next chunk", not as an in-flight Stop() on a pipeline
# that is blocked inside a native WaitForExit. Atomicity is unchanged: the job handler
# still stages/promotes once, after every chunk has returned, exactly like before.

function Sync-WaypointComplianceContentTree {
	<#
	.SYNOPSIS
	    Clones or updates the compliance-content working tree to RefValue and returns
	    the resolved commit plus the discovered profile inventory (directories only --
	    no `inspec check` is run here; see Get-WaypointComplianceContentEntries).

	.PARAMETER RepositoryUrl
	    The upstream compliance-content repository URL.

	.PARAMETER RefType
	    'tag' or 'branch' -- which ref-set RefValue names.

	.PARAMETER RefValue
	    The tag or branch name to check out.

	.PARAMETER ContentPath
	    Local working-tree root (a compliance-runner-only persistent mount, ADR-0017).

	.OUTPUTS
	    [PSCustomObject] with Commit (string) and Profiles (array of ProfileKey/Name/
	    Version/_ProfileDirectory -- the internal directory field a subsequent
	    Get-WaypointComplianceContentEntries call needs; ContentPullJobHandler never
	    sees this function's raw output directly, only via the job handler's own
	    orchestration, so _ProfileDirectory does not need stripping here).
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string]$RepositoryUrl,
		[Parameter(Mandatory)][ValidateSet('tag', 'branch')][string]$RefType,
		[Parameter(Mandatory)][string]$RefValue,
		[Parameter(Mandatory)][string]$ContentPath
	)

	# Issue #615 (live-verified): with no `git` on PATH, `& git` below fails to
	# launch the process at all and $LASTEXITCODE is left empty/stale, so the
	# thrown message ends in "exit code " with nothing after it -- a
	# non-actionable failure that reads like a transient clone error rather
	# than "git is missing". Fail fast with an unambiguous message instead.
	if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
		throw "git executable not found on PATH -- the compliance-runner image must install the git package"
	}

	$gitDir = Join-Path $ContentPath '.git'
	if (-not (Test-Path $gitDir)) {
		New-Item -ItemType Directory -Path $ContentPath -Force | Out-Null
		& git clone --quiet $RepositoryUrl $ContentPath 2>&1 | Out-Null
		if ($LASTEXITCODE -ne 0) {
			throw "git clone of '$RepositoryUrl' failed with exit code $LASTEXITCODE"
		}
	}
	else {
		& git -C $ContentPath fetch --quiet --tags origin 2>&1 | Out-Null
		if ($LASTEXITCODE -ne 0) {
			throw "git fetch in '$ContentPath' failed with exit code $LASTEXITCODE"
		}
	}

	$checkoutTarget = if ($RefType -eq 'branch') { "origin/$RefValue" } else { $RefValue }
	& git -C $ContentPath checkout --quiet --force $checkoutTarget 2>&1 | Out-Null
	if ($LASTEXITCODE -ne 0) {
		throw "git checkout of '$checkoutTarget' in '$ContentPath' failed with exit code $LASTEXITCODE"
	}

	$commit = (& git -C $ContentPath rev-parse HEAD).Trim()
	$profiles = @(Get-WaypointComplianceContentProfiles -ContentPath $ContentPath -Commit $commit)

	[PSCustomObject]@{
		Commit   = $commit
		Profiles = $profiles
	}
}

function Get-WaypointComplianceContentEntries {
	<#
	.SYNOPSIS
	    Issue #993 bounded chunk unit: builds one ContentEntries row (raw manifest text,
	    controls/files structural facts, bounded `inspec check` result) PLUS the
	    corresponding profile's Controls array, for each profile directory in
	    ProfileDirectories -- the per-chunk work ContentPullJobHandler drives across
	    multiple bounded PowerShellRequest invocations instead of one whole-tree call.

	.PARAMETER ProfileDirectories
	    Absolute directories of the profiles in THIS chunk only (a small slice of the
	    full inventory -- the caller sizes chunks and this invocation's own timeout so
	    that even every leaf in the chunk hitting the per-check bound still finishes
	    within the request's PowerShellRequest.Timeout).

	.PARAMETER ProfileKeysByDirectory
	    A hashtable mapping each directory in ProfileDirectories to its already-computed
	    ProfileKey (issue #617's content-root-relative path) -- computed once by the
	    caller from Sync-WaypointComplianceContentTree's output so this function never
	    needs ContentPath to recompute keys itself.

	.PARAMETER InspecCheckTimeoutSeconds
	    Forwarded to Test-WaypointInspecCheck for every executable-leaf candidate in
	    this chunk -- the per-check bound issue #989 introduced, unchanged by this
	    issue's chunking fix.
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string[]]$ProfileDirectories,
		[Parameter(Mandatory)][hashtable]$ProfileKeysByDirectory,
		[int]$InspecCheckTimeoutSeconds = 60
	)

	@(foreach ($profileDirectory in $ProfileDirectories) {
			$profileKey = $ProfileKeysByDirectory[$profileDirectory]
			$hasControlsDirectory = Test-Path (Join-Path $profileDirectory 'controls')
			# Issue #729: bounded `inspec check` only runs against a profile that already
			# looks like an executable leaf (has a controls/ directory) -- an aggregate
			# profile (no controls/) is never a candidate for structure validation, it is
			# quarantined/rejected by reconciliation on aggregate-vs-leaf grounds alone, so
			# running the (real, non-trivial) inspec binary against it would be wasted
			# bounded runner work on content that can never be promoted regardless.
			$inspecCheck = if ($hasControlsDirectory) {
				Test-WaypointInspecCheck -ProfileDirectory $profileDirectory -TimeoutSeconds $InspecCheckTimeoutSeconds
			}
			else {
				[PSCustomObject]@{ Ran = $false; Passed = $false; Detail = 'no controls/ directory -- not an executable-leaf candidate, inspec check skipped' }
			}

			[PSCustomObject]@{
				ProfileKey           = $profileKey
				RawYaml              = Get-WaypointComplianceContentRawManifest -ProfileDirectory $profileDirectory
				HasControlsDirectory = $hasControlsDirectory
				HasFilesDirectory    = Test-Path (Join-Path $profileDirectory 'files')
				ControlFileNames     = @(Get-WaypointComplianceContentControlFileNames -ProfileDirectory $profileDirectory)
				Controls             = @(Get-WaypointComplianceContentControls -ProfileDirectory $profileDirectory)
				InspecCheckRan       = $inspecCheck.Ran
				InspecCheckPassed    = $inspecCheck.Passed
				InspecCheckDetail    = $inspecCheck.Detail
			}
		})
}

function Get-WaypointComplianceContentRawManifest {
	<#
	.SYNOPSIS
	    Returns one profile's raw, untrusted inspec.yml text (or $null if missing/
	    unreadable) for the C#-side InspecManifestParser -- this module never parses
	    YAML itself (issue #729: YAML parsing is a C#/YamlDotNet concern with a size
	    bound and no custom tag resolution).

	.PARAMETER ProfileDirectory
	    The profile's real, absolute root directory.
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string]$ProfileDirectory
	)

	$manifestPath = Join-Path $ProfileDirectory 'inspec.yml'
	if (-not (Test-Path $manifestPath)) {
		return $null
	}

	try {
		return Get-Content -Path $manifestPath -Raw -ErrorAction Stop
	}
	catch {
		return $null
	}
}

function Get-WaypointComplianceContentControlFileNames {
	<#
	.SYNOPSIS
	    Returns the bare filenames (not full paths) of every controls/*.rb file under
	    one profile, or an empty array if there is no controls/ directory -- the
	    structural fact VendorContentEntry.ControlFileNames/HasControlsDirectory needs
	    for the semantic importer's "executable leaf has no controls" rejection gate.

	.PARAMETER ProfileDirectory
	    The profile's real, absolute root directory.
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string]$ProfileDirectory
	)

	$controlsDir = Join-Path $ProfileDirectory 'controls'
	if (-not (Test-Path $controlsDir)) {
		return @()
	}

	Get-ChildItem -Path $controlsDir -Filter '*.rb' -Recurse -File -ErrorAction SilentlyContinue |
	ForEach-Object { $_.Name }
}

function Test-WaypointInspecCheck {
	<#
	.SYNOPSIS
	    Bounded runner work (issue #729 deliverable 3): runs `inspec check <path>
	    --format json` against one executable-leaf-candidate profile directory and
	    returns whether the real (or CI-stubbed) tool considers the profile
	    structurally valid. This is a thin CLI wrapper, not a parser of InSpec's own
	    internals -- mirrors this repo's VCFDT stub convention (docs/testing.md "CI
	    stubs vs live-lab validation") for the argument-contract-fidelity discipline,
	    though InSpec/cinc-auditor itself is not a licensed/account-gated tool, so the
	    REAL binary is used directly wherever the image provides one
	    (runners/compliance-runner/Dockerfile ships cinc-auditor with the
	    /usr/local/bin/inspec alias).

	.PARAMETER ProfileDirectory
	    The profile's real, absolute root directory.

	.PARAMETER TimeoutSeconds
	    Wall-clock bound on the `inspec check` invocation (issue #729 AC "bounded
	    runner work"). A profile that cannot complete structure validation within this
	    window is treated as a failed check (fail closed), not left to hang the whole
	    content-pull job attempt. Defaults to 60s -- `inspec check` only walks/parses
	    profile metadata, it never executes controls against a target, so this is a
	    generous bound for even a large profile tree.

	.PARAMETER MaxOutputBytes
	    Hard cap on captured stdout+stderr (issue #729 AC "bounded runner work" -- an
	    output-size cap alongside the timeout). Output beyond the cap is truncated,
	    never buffered without bound; a profile that floods output still yields a
	    bounded, actionable diagnostic rather than an unbounded in-memory string.

	.OUTPUTS
	    [PSCustomObject] with Ran (bool -- whether an `inspec` binary was found and the
	    invocation completed within bounds), Passed (bool -- exit code 0), and Detail
	    (string, size-capped stdout+stderr for diagnostics).
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string]$ProfileDirectory,
		[int]$TimeoutSeconds = 60,
		[int]$MaxOutputBytes = 65536
	)

	$inspecCommand = Get-Command inspec -ErrorAction SilentlyContinue
	if (-not $inspecCommand) {
		# No inspec binary on PATH (e.g. a CI image without it staged) -- this is not a
		# profile failure, just unavailable bounded validation; the caller (candidate
		# promotion) must treat "did not run" distinctly from "ran and failed" and fail
		# closed either way (issue #729: a candidate that cannot be proven valid is not
		# promoted).
		return [PSCustomObject]@{ Ran = $false; Passed = $false; Detail = 'inspec executable not found on PATH' }
	}

	# Issue #984: bounded via a direct System.Diagnostics.Process + WaitForExit(timeout)
	# rather than Start-Job/Wait-Job -- `inspec` is an external executable regardless of
	# host, so this bound depends on nothing but the .NET process APIs, which work
	# identically whether this module is loaded into a full `pwsh` host or the
	# compliance-runner's embedded SMA runspace (WaypointRunspacePool,
	# InitialSessionState.CreateDefault2()). Start-Job/Wait-Job depended on PowerShell's
	# background-job subsystem, which the embedded runspace never wires up -- Wait-Job
	# never observed completion and every invocation hit the timeout (issue #984).
	$psi = [System.Diagnostics.ProcessStartInfo]::new()
	$psi.FileName = $inspecCommand.Source
	$psi.ArgumentList.Add('check')
	$psi.ArgumentList.Add($ProfileDirectory)
	$psi.ArgumentList.Add('--format')
	$psi.ArgumentList.Add('json')
	$psi.RedirectStandardOutput = $true
	$psi.RedirectStandardError = $true
	$psi.UseShellExecute = $false

	$process = [System.Diagnostics.Process]::new()
	$process.StartInfo = $psi

	try {
		[void]$process.Start()

		# Task-based async reads (StandardOutput/Error.ReadToEndAsync), NOT
		# Register-ObjectEvent + Begin*ReadLine -- reading a redirected stream
		# synchronously after the process exits can deadlock if the child fills the OS
		# pipe buffer before exiting, so the read must start before WaitForExit either
		# way, but Register-ObjectEvent's callback only runs when PowerShell's own
		# event queue is pumped (Wait-Event/Get-Event/a message loop), which nothing
		# in this synchronous function does -- proven live in issue #984's own fix
		# round: output silently came back empty because the DataReceived event
		# handlers never fired. Task.ReadToEndAsync is pure BCL (no PowerShell engine
		# event pump involved) and completes identically in a full pwsh host or the
		# compliance-runner's embedded SMA runspace.
		$stdoutTask = $process.StandardOutput.ReadToEndAsync()
		$stderrTask = $process.StandardError.ReadToEndAsync()

		$completed = $process.WaitForExit($TimeoutSeconds * 1000)
		if (-not $completed) {
			try {
				$process.Kill($true)
			}
			catch {
				# The process may have exited between WaitForExit's false return and here;
				# either way, there is nothing left to bound -- Write-Verbose only (not an
				# actionable failure) to satisfy PSAvoidUsingEmptyCatchBlock.
				Write-Verbose "inspec check process kill after timeout raised: $_"
			}
			# Give the kill a moment to unblock the read tasks before this function
			# returns -- best-effort only, never itself unbounded; the outer caller
			# already fails closed on the timeout regardless of what this captures.
			[void]$process.WaitForExit(5000)
			[System.Threading.Tasks.Task]::WaitAll(@($stdoutTask, $stderrTask), 5000) | Out-Null

			return [PSCustomObject]@{
				Ran    = $true
				Passed = $false
				Detail = "inspec check did not complete within ${TimeoutSeconds}s -- treated as a failed check (fail closed)"
			}
		}

		[System.Threading.Tasks.Task]::WaitAll(@($stdoutTask, $stderrTask))

		$exitCode = $process.ExitCode
		$detail = $stdoutTask.Result + $stderrTask.Result
		if ($detail.Length -gt $MaxOutputBytes) {
			$detail = $detail.Substring(0, $MaxOutputBytes) + "... [truncated at $MaxOutputBytes bytes]"
		}

		return [PSCustomObject]@{
			Ran    = $true
			Passed = ($exitCode -eq 0)
			Detail = $detail
		}
	}
	finally {
		$process.Dispose()
	}
}

function Get-WaypointComplianceContentProfiles {
	<#
	.SYNOPSIS
	    Enumerates InSpec profiles (directories containing inspec.yml) under
	    ContentPath and returns one PSObject per profile with the base
	    properties the job handler's parser expects: ProfileKey, Name, Version.

	.NOTES
	    inspec.yml's title/version fields are not parsed here -- this slice has no
	    YAML dependency in this module (the C#-side config-doc store already brings in
	    YamlDotNet for a different purpose; duplicating that here for two optional
	    display fields is not worth a second YAML dependency path). Version is left
	    unset until a follow-up parses the manifest for display purposes (#595).

	    Issue #617: ProfileKey is the ContentPath-relative profile directory path
	    (forward-slash normalized so it is stable regardless of the host OS's path
	    separator), NOT the bare directory basename -- the real content repo nests
	    profiles many levels deep and reuses leaf directory names (e.g. "postgresql",
	    "aaa") across different baselines, so the basename alone collides and silently
	    collapses distinct profiles under profiles.profile_key's UNIQUE constraint.
	    Name still falls back to the basename, which stays display-friendly. The
	    profile's real, absolute directory is carried on the returned object as
	    _ProfileDirectory (an internal/private property) -- issue #993:
	    Sync-WaypointComplianceContentTree returns it as-is (no longer stripped here),
	    since ContentPullJobHandler's phase 2 needs each directory to drive its
	    chunked Get-WaypointComplianceContentEntries calls.
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string]$ContentPath,
		[Parameter(Mandatory)][string]$Commit
	)

	$root = (Resolve-Path -LiteralPath $ContentPath).ProviderPath.TrimEnd('/', '\')

	Get-ChildItem -Path $ContentPath -Filter 'inspec.yml' -Recurse -File -ErrorAction SilentlyContinue |
	ForEach-Object {
		$profileDir = $_.Directory
		$fullPath = $profileDir.FullName
		$relative = $fullPath.Substring($root.Length).TrimStart('/', '\')
		$profileKey = $relative.Replace('\', '/')

		[PSCustomObject]@{
			ProfileKey        = $profileKey
			Name              = $profileDir.Name
			Version           = $null
			_ProfileDirectory = $fullPath
		}
	}
}

function Get-WaypointComplianceContentControls {
	<#
	.SYNOPSIS
	    Parses one profile's controls/*.rb InSpec control files and returns one
	    PSObject per control with ControlId, Title, Severity -- the properties
	    ContentPullJobHandler's control parser expects.

	.PARAMETER ProfileDirectory
	    The profile's real, absolute root directory (issue #617: NOT recomputed as
	    ContentPath/<profile_key> -- profile_key is a nested relative path today, but
	    this function takes the caller's already-known directory regardless).

	.NOTES
	    Regex-based, not a Ruby parser (see module doc comment). A file this cannot
	    match a `control 'id' do` line in is silently skipped -- it contributes no
	    row, but never throws and never aborts the rest of the enumeration; this is
	    the same "one malformed row must not fail the whole pull" discipline
	    ContentPullJobHandler.TryParseProfile already applies one level up.
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string]$ProfileDirectory
	)

	$controlsDir = Join-Path $ProfileDirectory 'controls'
	if (-not (Test-Path $controlsDir)) {
		return @()
	}

	Get-ChildItem -Path $controlsDir -Filter '*.rb' -Recurse -File -ErrorAction SilentlyContinue |
	ForEach-Object {
		$text = $null
		try {
			$text = Get-Content -Path $_.FullName -Raw -ErrorAction Stop
		}
		catch {
			# Unreadable file (permissions, encoding, etc.) -- skip, don't fail the pull.
			return
		}

		if ([string]::IsNullOrEmpty($text)) {
			return
		}

		$controlMatch = [regex]::Match($text, "control\s+['""]([^'""]+)['""]\s+do")
		if (-not $controlMatch.Success) {
			# No recognizable `control 'id' do` in this file -- not a control file
			# (or malformed beyond what this parser reads); skip silently.
			return
		}

		$controlId = $controlMatch.Groups[1].Value

		$title = $null
		$titleMatch = [regex]::Match($text, "title\s+['""]([^'""]*)['""]")
		if ($titleMatch.Success) {
			$title = $titleMatch.Groups[1].Value
		}

		$severity = $null
		$tagSeverityMatch = [regex]::Match($text, "tag\s*\(?\s*['""]?severity['""]?\s*[:=]>?\s*['""]([^'""]+)['""]")
		if ($tagSeverityMatch.Success) {
			$severity = $tagSeverityMatch.Groups[1].Value
		}

		[PSCustomObject]@{
			ControlId = $controlId
			Title     = $title
			Severity  = $severity
		}
	}
}

Export-ModuleMember -Function Sync-WaypointComplianceContentTree, Get-WaypointComplianceContentEntries, Get-WaypointComplianceContentProfiles, Get-WaypointComplianceContentControls, Get-WaypointComplianceContentRawManifest, Get-WaypointComplianceContentControlFileNames, Test-WaypointInspecCheck
