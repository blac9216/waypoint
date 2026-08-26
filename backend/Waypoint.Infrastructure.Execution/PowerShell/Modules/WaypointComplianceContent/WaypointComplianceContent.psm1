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
			[PSCustomObject]@{
				ProfileKey          = $p.ProfileKey
				RawYaml             = Get-WaypointComplianceContentRawManifest -ProfileDirectory $p._ProfileDirectory
				HasControlsDirectory = Test-Path (Join-Path $p._ProfileDirectory 'controls')
				HasFilesDirectory    = Test-Path (Join-Path $p._ProfileDirectory 'files')
				ControlFileNames     = @(Get-WaypointComplianceContentControlFileNames -ProfileDirectory $p._ProfileDirectory)
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
	    --format json` against one executable-leaf profile directory and returns
	    whether the real (or CI-stubbed) tool considers the profile structurally
	    valid. This is a thin CLI wrapper, not a parser of InSpec's own internals --
	    mirrors this repo's VCFDT convention (docs/testing.md "CI stubs vs live-lab
	    validation"): CI/unit tests drive an invented stub mirroring `inspec check`'s
	    publicly documented CLI contract (subcommand + --format json, exit 0 on a
	    structurally valid profile, non-zero otherwise); the real, open-source `inspec`
	    binary is used directly wherever the image provides it (InSpec is not a
	    licensed/account-gated tool like VCFDT, so a real invocation path is fine).

	.PARAMETER ProfileDirectory
	    The profile's real, absolute root directory.

	.OUTPUTS
	    [PSCustomObject] with Ran (bool -- whether an `inspec` binary was found at all),
	    Passed (bool), and Detail (string, stdout+stderr tail for diagnostics).
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string]$ProfileDirectory
	)

	if (-not (Get-Command inspec -ErrorAction SilentlyContinue)) {
		# No inspec binary on PATH (e.g. a CI image without it staged) -- this is not a
		# profile failure, just unavailable bounded validation; the caller records this
		# distinctly from an actual `inspec check` failure.
		return [PSCustomObject]@{ Ran = $false; Passed = $false; Detail = 'inspec executable not found on PATH' }
	}

	$output = & inspec check $ProfileDirectory --format json 2>&1
	$passed = $LASTEXITCODE -eq 0
	return [PSCustomObject]@{ Ran = $true; Passed = $passed; Detail = ($output | Out-String) }
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
