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
# clock timeout (Start-Job, hard-stopped past the limit) and a captured-output size cap
# (issue #729 AC "bounded runner work") so one hung or pathological profile cannot stall
# or memory-balloon the whole content-pull job attempt.
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

function Invoke-WaypointComplianceContentPull {
	<#
	.SYNOPSIS
	    Clones or updates the compliance-content working tree to RefValue and returns
	    the resolved commit plus the discovered profile inventory.

	.PARAMETER RepositoryUrl
	    The upstream compliance-content repository URL.

	.PARAMETER RefType
	    'tag' or 'branch' -- which ref-set RefValue names.

	.PARAMETER RefValue
	    The tag or branch name to check out.

	.PARAMETER ContentPath
	    Local working-tree root (a compliance-runner-only persistent mount, ADR-0017).
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
	# Issue #729: capture the raw inspec.yml text and controls/*.rb filenames per
	# profile BEFORE _ProfileDirectory is stripped below -- ContentPullJobHandler's
	# semantic-import pass (VendorContentEntry) needs the untrusted manifest text and
	# structural facts the C#-side InspecManifestParser/VendorHierarchyInterpreter
	# reconcile against the closed catalog vocabulary; this module only reads and
	# forwards bytes, it never interprets them.
	$entries = @(foreach ($p in $profiles) {
			$hasControlsDirectory = Test-Path (Join-Path $p._ProfileDirectory 'controls')
			# Issue #729: bounded `inspec check` only runs against a profile that already
			# looks like an executable leaf (has a controls/ directory) -- an aggregate
			# profile (no controls/) is never a candidate for structure validation, it is
			# quarantined/rejected by reconciliation on aggregate-vs-leaf grounds alone, so
			# running the (real, non-trivial) inspec binary against it would be wasted
			# bounded runner work on content that can never be promoted regardless.
			$inspecCheck = if ($hasControlsDirectory) {
				Test-WaypointInspecCheck -ProfileDirectory $p._ProfileDirectory
			}
			else {
				[PSCustomObject]@{ Ran = $false; Passed = $false; Detail = 'no controls/ directory -- not an executable-leaf candidate, inspec check skipped' }
			}

			[PSCustomObject]@{
				ProfileKey            = $p.ProfileKey
				RawYaml               = Get-WaypointComplianceContentRawManifest -ProfileDirectory $p._ProfileDirectory
				HasControlsDirectory  = $hasControlsDirectory
				HasFilesDirectory     = Test-Path (Join-Path $p._ProfileDirectory 'files')
				ControlFileNames      = @(Get-WaypointComplianceContentControlFileNames -ProfileDirectory $p._ProfileDirectory)
				InspecCheckRan        = $inspecCheck.Ran
				InspecCheckPassed     = $inspecCheck.Passed
				InspecCheckDetail     = $inspecCheck.Detail
			}
		})

	foreach ($p in $profiles) {
		# Issue #617: use the profile's real directory (carried through as
		# _ProfileDirectory by Get-WaypointComplianceContentProfiles), not a path
		# reconstructed from ProfileKey/basename -- the real repo's profiles are nested
		# many levels below ContentPath, so Join-Path $ContentPath $p.ProfileKey would
		# only work by coincidence at depth 1.
		$p | Add-Member -NotePropertyName Controls -NotePropertyValue @(Get-WaypointComplianceContentControls -ProfileDirectory $p._ProfileDirectory)
		$p.PSObject.Properties.Remove('_ProfileDirectory')
	}

	[PSCustomObject]@{
		Commit          = $commit
		Profiles        = $profiles
		ContentEntries  = $entries
	}
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

	if (-not (Get-Command inspec -ErrorAction SilentlyContinue)) {
		# No inspec binary on PATH (e.g. a CI image without it staged) -- this is not a
		# profile failure, just unavailable bounded validation; the caller (candidate
		# promotion) must treat "did not run" distinctly from "ran and failed" and fail
		# closed either way (issue #729: a candidate that cannot be proven valid is not
		# promoted).
		return [PSCustomObject]@{ Ran = $false; Passed = $false; Detail = 'inspec executable not found on PATH' }
	}

	# Bounded via Start-Job rather than the module's own thread so a hung `inspec`
	# process (e.g. a pathological profile.yml symlink loop) cannot block the pull job
	# attempt past TimeoutSeconds -- Wait-Job returns even if the job's own child
	# process never exits on its own, and Remove-Job -Force below reaps it.
	$job = Start-Job -ScriptBlock {
		param($dir)
		$output = & inspec check $dir --format json 2>&1 | Out-String
		[PSCustomObject]@{ ExitCode = $LASTEXITCODE; Output = $output }
	} -ArgumentList $ProfileDirectory

	$completed = Wait-Job -Job $job -Timeout $TimeoutSeconds
	if (-not $completed) {
		Stop-Job -Job $job -ErrorAction SilentlyContinue
		Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
		return [PSCustomObject]@{
			Ran     = $true
			Passed  = $false
			Detail  = "inspec check did not complete within ${TimeoutSeconds}s -- treated as a failed check (fail closed)"
		}
	}

	$result = Receive-Job -Job $job
	Remove-Job -Job $job -Force -ErrorAction SilentlyContinue

	$detail = if ($result.Output) { $result.Output } else { '' }
	if ($detail.Length -gt $MaxOutputBytes) {
		$detail = $detail.Substring(0, $MaxOutputBytes) + "... [truncated at $MaxOutputBytes bytes]"
	}

	return [PSCustomObject]@{
		Ran    = $true
		Passed = ($result.ExitCode -eq 0)
		Detail = $detail
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
	    _ProfileDirectory (an internal/private property, stripped by
	    Invoke-WaypointComplianceContentPull before the object is handed to the job
	    handler) so control discovery never has to reconstruct that path from the key.
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

Export-ModuleMember -Function Invoke-WaypointComplianceContentPull, Get-WaypointComplianceContentProfiles, Get-WaypointComplianceContentControls, Get-WaypointComplianceContentRawManifest, Get-WaypointComplianceContentControlFileNames, Test-WaypointInspecCheck
