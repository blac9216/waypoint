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
	foreach ($p in $profiles) {
		$profileDirectory = Join-Path $ContentPath $p.ProfileKey
		$p | Add-Member -NotePropertyName Controls -NotePropertyValue @(Get-WaypointComplianceContentControls -ProfileDirectory $profileDirectory)
	}

	[PSCustomObject]@{
		Commit   = $commit
		Profiles = $profiles
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
	    display fields is not worth a second YAML dependency path). ProfileKey (the
	    directory name) is the only value the handler treats as load-bearing; Name
	    falls back to it and Version is left unset until a follow-up parses the
	    manifest for display purposes.
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string]$ContentPath,
		[Parameter(Mandatory)][string]$Commit
	)

	Get-ChildItem -Path $ContentPath -Filter 'inspec.yml' -Recurse -File -ErrorAction SilentlyContinue |
	ForEach-Object {
		$profileDir = $_.Directory
		[PSCustomObject]@{
			ProfileKey = $profileDir.Name
			Name       = $profileDir.Name
			Version    = $null
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
	    The profile's root directory (ContentPath/<profile_key>).

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

Export-ModuleMember -Function Invoke-WaypointComplianceContentPull, Get-WaypointComplianceContentProfiles, Get-WaypointComplianceContentControls
