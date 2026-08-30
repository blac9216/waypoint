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

# Invented fake for issue #719's regression coverage. Stands in for the sibling
# repo's vcf-download-manager.common.ps1 -- it is dot-sourced by the REAL,
# unmodified WaypointDownload.psm1 / WaypointCatalogIndex.psm1 shims via
# $env:WAYPOINT_VCF_DOWNLOAD_MANAGER_COMMON_PATH (or the -VcfDownloadManagerCommonPath
# parameter), exactly as the real script would be. No network, no real depot --
# everything here is fabricated.
#
# Its Write-Log deliberately REPRODUCES the real script's bug-triggering shape --
# console output filtered by $Global:LogLevel/$Global:SilentMode, file output gated
# on $Global:LogPath, and (unlike the real one) no fallback of any kind for Debug/
# Verbose severities that miss the filter. Neither shim under test sets
# $Global:LogLevel/$Global:SilentMode/$Global:LogPath, so if either shim failed to
# re-define Write-Log after dot-sourcing this file (the #719 fix), every Debug/
# Verbose call below would be silently dropped: no console write (level filtered),
# no file write (no LogPath), no job.log event -- reproducing the exact defect this
# issue tracks. That makes these tests a real regression guard against the override
# being removed or reordered, not just a happy-path smoke test.
function Write-Log {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory, Position = 0)]
		[AllowEmptyString()]
		[string]$Message,

		[Parameter()]
		[ValidateSet('Debug', 'Verbose', 'Info', 'Success', 'Warning', 'Error', 'Critical')]
		[string]$Severity = 'Info',

		[Parameter()]
		[string]$Source
	)

	if ([string]::IsNullOrWhiteSpace($Message)) { return }

	$SeverityOrder = @{ Debug = 0; Verbose = 1; Info = 2; Success = 2; Warning = 3; Error = 4; Critical = 5 }
	$MsgLevel = $SeverityOrder[$Severity]
	$MinLevel = $SeverityOrder[$Global:LogLevel ?? 'Info']
	if (-not $Global:SilentMode -and $MsgLevel -ge $MinLevel) {
		Write-Host "[$Severity] $Message"
	}

	if ($Global:LogPath) {
		Add-Content -Path $Global:LogPath -Value "[$Severity] $Message" -ErrorAction SilentlyContinue
	}
}

# Mirrors Save-WebFile's call shape closely enough to pin the #719 fix at the
# WaypointDownload.psm1 boundary: one call per severity the real script actually
# emits during a download pass (retry/resume is Verbose, permission/crawl detail is
# Debug), then the same result shape Invoke-WaypointDownload passes straight
# through.
function Save-WebFile {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string]$Url,
		[Parameter(Mandatory)][string]$OutFile,
		[Parameter()][long]$ExpectedSize = 0,
		[Parameter()][int]$RetryCount = 3,
		[Parameter()][string]$Source = 'depot'
	)

	Write-Log 'fake debug: resolving resume state' -Severity 'Debug'
	Write-Log 'fake verbose: downloading attempt 1/3' -Severity 'Verbose'
	Write-Log 'fake info: starting download' -Severity 'Info'
	Write-Log 'fake warning: retrying after transient error' -Severity 'Warning'
	Write-Log 'fake success: download complete' -Severity 'Success'

	[pscustomobject]@{
		Url       = $Url
		LocalPath = $OutFile
		Success   = $true
		Skipped   = $false
		Size      = 0
	}
}

# Mirrors Get-FileManifest's call shape for the WaypointCatalogIndex.psm1 boundary.
function Get-FileManifest {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string]$Directory,
		[Parameter()][switch]$IncludeHash,
		[Parameter()][string]$HashAlgorithm = 'SHA256'
	)

	Write-Log 'fake debug: walking manifest directory' -Severity 'Debug'
	Write-Log 'fake verbose: manifest built with 0 files' -Severity 'Verbose'

	return [ordered]@{}
}
