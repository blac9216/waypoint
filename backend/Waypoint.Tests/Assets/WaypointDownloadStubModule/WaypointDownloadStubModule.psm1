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

# Invented stub for the download job full-loop integration test (issue #10). Mirrors
# Invoke-WaypointDownload's real signature and output shape without touching the
# sibling repo, a real depot, or any real URL/credential -- everything here is
# fabricated. $Url is interpreted as a local file path (an invented fixture asset
# under Waypoint.Tests/Assets, never a real network location) so the test stays fully
# offline. Appending "#corrupt" to $Url flips one byte after copy, letting a test
# drive the sha256-mismatch/quarantine path deterministically.

function Invoke-WaypointDownload {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$Url,

		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$OutFile,

		[Parameter()]
		[long]$ExpectedSize = 0,

		[Parameter()]
		[int]$RetryCount = 3,

		[Parameter()]
		[string]$Source = 'stub',

		[Parameter()]
		[string]$VcfDownloadManagerCommonPath
	)

	$InformationPreference = 'Continue'

	$Corrupt = $false
	$SourcePath = $Url
	if ($Url.EndsWith('#corrupt')) {
		$Corrupt = $true
		$SourcePath = $Url.Substring(0, $Url.Length - '#corrupt'.Length)
	}

	if (-not (Test-Path -Path $SourcePath -PathType Leaf)) {
		return [pscustomobject]@{
			Url       = $Url
			LocalPath = $OutFile
			Success   = $false
			Skipped   = $false
			Size      = 0
		}
	}

	$OutDir = Split-Path -Path $OutFile -Parent
	if (-not (Test-Path -Path $OutDir)) {
		New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
	}

	Write-Information "Downloading stub source: $SourcePath -> $OutFile"
	Copy-Item -Path $SourcePath -Destination $OutFile -Force

	if ($Corrupt) {
		# Flip one byte so the caller's sha256 verification deterministically fails,
		# simulating the "corrupted download" acceptance-criterion fixture -- no real
		# network flakiness required to exercise the failed/quarantine path.
		$Bytes = [System.IO.File]::ReadAllBytes($OutFile)
		if ($Bytes.Length -gt 0) {
			$Bytes[0] = $Bytes[0] -bxor 0xFF
		}

		[System.IO.File]::WriteAllBytes($OutFile, $Bytes)
	}

	$Size = (Get-Item -Path $OutFile).Length
	Write-Information "Downloaded $Size bytes."

	[pscustomobject]@{
		Url       = $Url
		LocalPath = $OutFile
		Success   = $true
		Skipped   = $false
		Size      = $Size
	}
}

Export-ModuleMember -Function Invoke-WaypointDownload
