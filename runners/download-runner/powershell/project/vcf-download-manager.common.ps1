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
#
# Project-owned source migrated from the predecessor vcf-docker-download
# repository (same author/copyright holder), preserved unmodified per
# ADR-0013/ADR-0015 and AGENTS.md's License & Borrowing Policy sibling-repo
# carve-out: Waypoint orchestrates this project-owned script via the
# WaypointDownload/WaypointCatalogIndex shim modules, it does not fork it. Only
# the functions this runner's M1 call graph needs (Save-WebFile,
# Get-FileManifest, and their shared helpers) are imported; workflow modules
# for UMDS/VCSA/Photon/VKS/content-library/transfer are out of scope for M1
# catalog-index/download and are not imported here. Owner-authored sibling
# code like this is not "third-party" and needs no root NOTICE entry — NOTICE
# is reserved for genuinely third-party fragments.

<#
.MODULE
    vcf-download-manager.common.ps1

.SYNOPSIS
    Common utilities for VCF Download Manager.

.DESCRIPTION
    Provides shared utilities for logging, web downloads, file system operations,
    and external process management across all workflow modules.

.NOTES
    See design/implementation-plan.md for complete function specifications.
#>

#region Shared Path Defaults

# Centralized path defaults for all workflow modules.
# Individual modules no longer need to duplicate these; they are set once here
# and overridden by Initialize-VcfConfiguration at runtime.
$Script:VcfBasePath = $env:VCF_BASE_PATH ?? '/vcf'
$Script:ContentLibPath = $env:CONTENT_LIB_PATH ?? '/vcf/ContentLibrary'
$Script:UmdsRepoPath = $env:UMDS_REPO_PATH ?? '/vcf/UMDS'
$Script:VcsaDepotPath = $env:VCSA_DEPOT_PATH ?? '/vcf/VCSA'
$Script:VcsaBasePath = $env:VCSA_BASE_PATH ?? '/vcf/VCSA/PROD/COMP/VCENTER'
$Script:VcsaTargetPath = $env:VCSA_TARGET_PATH ?? '/vcf/VCSA/OfflineRepo'
$Script:VksRepoPath = $env:VKS_REPO_PATH ?? '/vcf/VKS'
$Script:VmtoolsBasePath = $env:VMTOOLS_BASE_PATH ?? '/vcf/VMTools'
$Script:PhotonBasePath = $env:PHOTON_BASE_PATH ?? '/vcf/Photon'
$Script:TransferDir = $env:TRANSFER_DIR ?? '/vcf/Transfer'

#endregion

#region Format-ByteSize

function Format-ByteSize {
	<#
	.SYNOPSIS
	    Format a byte count into a human-readable string.

	.DESCRIPTION
	    Converts a byte count to the most appropriate unit (B, KB, MB, GB)
	    with appropriate decimal precision.

	.PARAMETER Bytes
	    The number of bytes to format.

	.OUTPUTS
	    [string] Formatted byte size string.

	.EXAMPLE
	    Format-ByteSize -Bytes 1073741824  # Returns "1.00 GB"
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[long]$Bytes
	)

	if ($Bytes -ge 1GB) { return '{0:N2} GB' -f ($Bytes / 1GB) }
	if ($Bytes -ge 1MB) { return '{0:N1} MB' -f ($Bytes / 1MB) }
	if ($Bytes -ge 1KB) { return '{0:N0} KB' -f ($Bytes / 1KB) }
	return "$Bytes B"
}

#endregion

#region Log Level Configuration

$Script:SeverityOrder = @{
	'Debug' = 0; 'Verbose' = 1; 'Info' = 2; 'Success' = 3
	'Warning' = 4; 'Error' = 5; 'Critical' = 6
}

$Script:ColorMap = @{
	'Debug'    = 'Cyan'
	'Verbose'  = 'Gray'
	'Info'     = 'White'
	'Success'  = 'Green'
	'Warning'  = 'Yellow'
	'Error'    = 'Red'
	'Critical' = 'DarkRed'
}

#endregion

#region Write-Log

function Write-Log {
	<#
	.SYNOPSIS
	    Centralized logging function.

	.DESCRIPTION
	    Writes log messages with timestamp, severity, and optional source component.

	.PARAMETER Message
	    The log message to write.

	.PARAMETER Severity
	    The severity level of the message. Valid values: Debug, Verbose, Info,
	    Success, Warning, Error, Critical. Default is Info.

	.PARAMETER Source
	    Optional component identifier for log tracing (e.g., 'UMDS', 'VKS', 'Pipeline').

	.EXAMPLE
	    Write-Log "Processing started" -Severity Info

	.EXAMPLE
	    Write-Log "Download failed" -Severity Error -Source "VKS"
	#>
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

	$Timestamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
	$SourcePart = if ($Source) { "[$Source] " } else { "" }
	$FormattedMessage = "$Timestamp [$Severity] $SourcePart$Message"

	# Console output: respect log level and silent mode
	$MsgLevel = $Script:SeverityOrder[$Severity]
	$MinLevel = $Script:SeverityOrder[$Global:LogLevel ?? 'Info']
	if (-not $Global:SilentMode -and $MsgLevel -ge $MinLevel) {
		$Color = $Script:ColorMap[$Severity]
		Write-Host $FormattedMessage -ForegroundColor $Color
	}

	# File output: always write (no filtering)
	if ($Global:LogPath) {
		try {
			Add-Content -Path $Global:LogPath -Value $FormattedMessage -ErrorAction Stop
		} catch {
			Write-Host "Failed to write to log file: $_" -ForegroundColor Red
		}
	}
}

#endregion

#region Set-Permissions

<#
.SYNOPSIS
    Set file/directory permissions for web server access.

.DESCRIPTION
    Walks directory tree and sets permissions to allow read access for others.
    Uses chmod on Linux systems. Supports -WhatIf/-Confirm: the chmod call is
    gated by ShouldProcess, so -WhatIf reports the path that would be
    modified without changing any permissions.

.PARAMETER Path
    Path to the file or directory to set permissions on.

.PARAMETER Source
    Component identifier for logging.

.EXAMPLE
    Set-Permissions -Path '/vcf/VKS'
#>
function Set-Permissions {
	[CmdletBinding(SupportsShouldProcess)]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$Path,

		[Parameter()]
		[string]$Source
	)

	$WriteLogParams = @{}
	if ($Source) { $WriteLogParams['Source'] = $Source }

	if (-not (Test-Path -Path $Path)) {
		throw "Path does not exist: $Path"
	}

	Write-Log "Setting permissions on $Path" -Severity 'Verbose' @WriteLogParams

	# Check if running on Linux/macOS
	if ($IsLinux -or $IsMacOS) {
		if ($PSCmdlet.ShouldProcess($Path, 'Set permissions (chmod -R o+rX)')) {
			try {
				& chmod -R o+rX $Path 2>$null
			} catch {
				Write-Log "Failed to set permissions on ${Path}: $_" -Severity 'Warning' @WriteLogParams
			}

			Write-Log "Permissions set on $Path and contents" -Severity 'Verbose' @WriteLogParams
		}
	} else {
		# Windows - no-op with warning
		Write-Log "Set-Permissions is only implemented for Linux/macOS" -Severity 'Warning' @WriteLogParams
	}
}

#endregion

#region Get-FileManifest

<#
.SYNOPSIS
    Build dictionary of file paths with size, mtime, and optional hash.

.DESCRIPTION
    Recursively walks a directory and builds a manifest of all files with
    their metadata. Optionally calculates file hashes.

.PARAMETER Directory
    Path to the directory to scan.

.PARAMETER IncludeHash
    If set, calculates file hash for each file.

.PARAMETER HashAlgorithm
    Hash algorithm to use. Default is MD5.

.PARAMETER OutputFile
    If specified, exports manifest to JSON file.

.PARAMETER Source
    Component identifier for logging.

.OUTPUTS
    [hashtable] Dictionary keyed by relative path with Size, Mtime, and optional Hash.

.EXAMPLE
    $Manifest = Get-FileManifest -Directory '/vcf/UMDS' -IncludeHash -HashAlgorithm 'SHA256'

.NOTES
    Issue #1718: an unreadable subdirectory is never silently treated as
    empty. Walk errors (permission denied) are collected via -ErrorVariable;
    if any occurred, each is logged at Warning and the function throws a
    terminating error naming every unreadable path -- a manifest that may be
    incomplete is worse than no manifest at all (epic #1361, "verify, never
    assume"). This function has exactly one repo caller
    (WaypointCatalogIndex.psm1's Update-WaypointCatalogIndex), which does not
    catch around the call, so the throw propagates as a job failure rather
    than a comparison against a truncated baseline.
#>
function Get-FileManifest {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$Directory,

		[Parameter()]
		[switch]$IncludeHash,

		[Parameter()]
		[ValidateSet('MD5', 'SHA1', 'SHA256')]
		[string]$HashAlgorithm = 'MD5',

		[Parameter()]
		[string]$OutputFile,

		[Parameter()]
		[string]$Source
	)

	$WriteLogParams = @{}
	if ($Source) { $WriteLogParams['Source'] = $Source }

	if (-not (Test-Path -Path $Directory -PathType Container)) {
		throw "Directory does not exist: $Directory"
	}

	Write-Log "Building file manifest for $Directory" -Severity 'Verbose' @WriteLogParams

	$Manifest = @{}
	$DirectoryInfo = [System.IO.DirectoryInfo]::new($Directory)
	$BasePath = $DirectoryInfo.FullName

	$WalkErrors = $null
	$Files = Get-ChildItem -Path $Directory -Recurse -File -Force -ErrorAction SilentlyContinue -ErrorVariable WalkErrors

	if ($WalkErrors) {
		$UnreadablePaths = @($WalkErrors | ForEach-Object {
				if ($_.TargetObject) { $_.TargetObject } else { $_.Exception.Message }
			} | Select-Object -Unique)
		foreach ($UnreadablePath in $UnreadablePaths) {
			Write-Log "Unable to read directory, excluded from manifest: $UnreadablePath" -Severity 'Warning' @WriteLogParams
		}
		throw "Get-FileManifest: $($UnreadablePaths.Count) unreadable path(s) under '$Directory', manifest would be incomplete: $($UnreadablePaths -join ', ')"
	}

	foreach ($File in $Files) {
		$RelativePath = $File.FullName.Substring($BasePath.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar)
		# Normalize to forward slashes for cross-platform consistency
		$RelativePath = $RelativePath -replace '\\', '/'

		$Entry = @{
			Size  = $File.Length
			Mtime = $File.LastWriteTimeUtc
		}

		if ($IncludeHash) {
			try {
				$HashResult = Get-FileHash -LiteralPath $File.FullName -Algorithm $HashAlgorithm
				$Entry['Hash'] = $HashResult.Hash
			} catch {
				Write-Log "Failed to hash $($File.FullName): $_" -Severity 'Warning' @WriteLogParams
				$Entry['Hash'] = $null
			}
		}

		$Manifest[$RelativePath] = $Entry
	}

	Write-Log "Manifest built with $($Manifest.Count) files" -Severity 'Verbose' @WriteLogParams

	if ($OutputFile) {
		$Manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $OutputFile -Encoding UTF8
		Write-Log "Manifest exported to $OutputFile" -Severity 'Info' @WriteLogParams
	}

	return $Manifest
}

#endregion

#region Remove-EmptyDirs

<#
.SYNOPSIS
    Recursively remove empty directories.

.DESCRIPTION
    Walks directory tree bottom-up and removes directories that are empty.
    Non-empty directories are preserved. Supports -WhatIf/-Confirm: each
    removal is individually gated by ShouldProcess, so -WhatIf reports every
    directory that would be removed without deleting anything.

.PARAMETER Directory
    Path to the directory to clean.

.PARAMETER Source
    Component identifier for logging.

.OUTPUTS
    [int] Number of directories removed. Under -WhatIf this is always 0
    since no directories are actually removed.

.EXAMPLE
    Remove-EmptyDirs -Directory '/vcf/UMDS'

.NOTES
    Issue #1718: an unreadable directory is never treated as empty. Both the
    top-level directory enumeration and each directory's child listing collect
    walk errors via -ErrorVariable; any unreadable directory is logged at
    Warning, left untouched (no removal attempted), and its path is included
    in a terminating error thrown after the walk completes -- matching
    Get-FileManifest's fail-closed contract for the same underlying failure.
#>
function Remove-EmptyDirs {
	[CmdletBinding(SupportsShouldProcess)]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$Directory,

		[Parameter()]
		[string]$Source
	)

	$WriteLogParams = @{}
	if ($Source) { $WriteLogParams['Source'] = $Source }

	if (-not (Test-Path -Path $Directory -PathType Container)) {
		Write-Log "Directory does not exist: $Directory" -Severity 'Warning' @WriteLogParams
		return 0
	}

	Write-Log "Removing empty directories under $Directory" -Severity 'Verbose' @WriteLogParams

	$RemovedCount = 0
	$UnreadablePaths = [System.Collections.Generic.List[string]]::new()

	# Get all directories, sorted by depth (deepest first)
	$WalkErrors = $null
	$Directories = Get-ChildItem -Path $Directory -Recurse -Directory -Force -ErrorAction SilentlyContinue -ErrorVariable WalkErrors |
	Sort-Object { $_.FullName.Split([System.IO.Path]::DirectorySeparatorChar).Count } -Descending

	foreach ($WalkError in $WalkErrors) {
		$UnreadablePath = if ($WalkError.TargetObject) { $WalkError.TargetObject } else { $WalkError.Exception.Message }
		$UnreadablePaths.Add($UnreadablePath)
	}

	foreach ($Dir in $Directories) {
		$ChildErrors = $null
		# Check if directory is empty (no files or subdirectories)
		$Children = Get-ChildItem -Path $Dir.FullName -Force -ErrorAction SilentlyContinue -ErrorVariable ChildErrors
		if ($ChildErrors) {
			# Directory could not be listed -- never treat as empty, never attempt removal.
			$UnreadablePaths.Add($Dir.FullName)
			continue
		}
		if ($Children.Count -eq 0) {
			try {
				if ($PSCmdlet.ShouldProcess($Dir.FullName, 'Remove empty directory')) {
					Remove-Item -LiteralPath $Dir.FullName -Force -ErrorAction Stop
					$RemovedCount++
					Write-Log "Removed empty directory: $($Dir.FullName)" -Severity 'Debug' @WriteLogParams
				}
			} catch {
				# Directory not empty (race) or permission error on removal - ignore
				Write-Log "Could not remove $($Dir.FullName): $_" -Severity 'Debug' @WriteLogParams
			}
		}
	}

	if ($UnreadablePaths.Count -gt 0) {
		$DistinctUnreadablePaths = @($UnreadablePaths | Select-Object -Unique)
		foreach ($UnreadablePath in $DistinctUnreadablePaths) {
			Write-Log "Unable to read directory, left untouched: $UnreadablePath" -Severity 'Warning' @WriteLogParams
		}
		Write-Log "Removed $RemovedCount empty directories" -Severity 'Verbose' @WriteLogParams
		throw "Remove-EmptyDirs: $($DistinctUnreadablePaths.Count) unreadable path(s) under '$Directory', not evaluated for removal: $($DistinctUnreadablePaths -join ', ')"
	}

	Write-Log "Removed $RemovedCount empty directories" -Severity 'Verbose' @WriteLogParams
	return $RemovedCount
}

#endregion

#region Test-IsRecent

<#
.SYNOPSIS
    Check if a date is within a specified number of months from now.

.DESCRIPTION
    Uses AddMonths for precise month calculation that handles varying month
    lengths, month-end dates, and leap years correctly.

.PARAMETER Date
    The date to check.

.PARAMETER Months
    Number of months for the retention window.

.OUTPUTS
    [bool] True if Date is within the specified months of current date.

.EXAMPLE
    Test-IsRecent -Date (Get-Date '2025-06-15') -Months 6
#>
function Test-IsRecent {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNull()]
		[datetime]$Date,

		[Parameter(Mandatory)]
		[ValidateRange(1, [int]::MaxValue)]
		[int]$Months
	)

	# Calculate cutoff using AddMonths for precise month arithmetic
	$Cutoff = (Get-Date).ToUniversalTime().AddMonths(-$Months)

	# Compare in UTC
	return $Date.ToUniversalTime() -ge $Cutoff
}

#endregion

#region Test-DirectoryAccess

<#
.SYNOPSIS
    Validate that directories exist and are optionally writable.

.DESCRIPTION
    Checks if directories exist and optionally tests write access by creating
    and deleting a temporary file.

.PARAMETER Directories
    Array of directory paths to check.

.PARAMETER RequireWritable
    If set, also tests that directories are writable.

.PARAMETER ThrowOnFail
    If set, throws on first failure. Otherwise returns status hashtable.

.PARAMETER Source
    Component identifier for logging.

.OUTPUTS
    [hashtable] Status dictionary with Exists and Writable for each path.

.EXAMPLE
    Test-DirectoryAccess -Directories @('/vcf/UMDS', '/vcf/VKS') -RequireWritable -ThrowOnFail
#>
function Test-DirectoryAccess {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[string[]]$Directories,

		[Parameter()]
		[switch]$RequireWritable,

		[Parameter()]
		[switch]$ThrowOnFail,

		[Parameter()]
		[string]$Source
	)

	$WriteLogParams = @{}
	if ($Source) { $WriteLogParams['Source'] = $Source }

	$Results = @{}

	foreach ($Dir in $Directories) {
		$Status = @{
			Exists   = $false
			Writable = $null
		}

		# Check existence
		if (Test-Path -Path $Dir -PathType Container) {
			$Status.Exists = $true

			# Check writability if requested
			if ($RequireWritable) {
				$TestFile = Join-Path $Dir ".write_test_$(Get-Random).tmp"
				try {
					[System.IO.File]::WriteAllText($TestFile, 'test')
					Remove-Item -Path $TestFile -Force -ErrorAction SilentlyContinue
					$Status.Writable = $true
				} catch {
					$Status.Writable = $false
					if ($ThrowOnFail) {
						throw "Directory is not writable: $Dir"
					}
				}
			}
		} else {
			if ($ThrowOnFail) {
				throw "Directory does not exist: $Dir"
			}
		}

		$Results[$Dir] = $Status
	}

	return $Results
}

#endregion

#region Save-WebFile

<#
.SYNOPSIS
    Download a file from a URL with retry and resume support.

.DESCRIPTION
    Downloads a file using Invoke-WebRequest with exponential backoff retry,
    HTTP Range resume for partial downloads, and optional size validation.

.PARAMETER Url
    Source URL to download from.

.PARAMETER OutFile
    Destination file path.

.PARAMETER ExpectedSize
    Expected file size in bytes. If provided, enables resume and size validation.

.PARAMETER RetryCount
    Maximum number of attempts. Default is 3.

.PARAMETER Source
    Component identifier for logging.

.OUTPUTS
    [PSCustomObject] with Url, LocalPath, Success, Skipped, Size properties.

.EXAMPLE
    Save-WebFile -Url 'https://example.com/file.iso' -OutFile '/tmp/file.iso'
#>
function Save-WebFile {
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
		[string]$Source
	)

	$WriteLogParams = @{}
	if ($Source) { $WriteLogParams['Source'] = $Source }

	# Suppress Invoke-WebRequest progress bars
	$ProgressPreference = 'SilentlyContinue'

	# Ensure directory exists
	$Dir = Split-Path -Path $OutFile -Parent
	if (-not (Test-Path -Path $Dir)) {
		New-Item -Path $Dir -ItemType Directory -Force | Out-Null
	}

	# Get expected size from HEAD request if not provided
	#
	# Issue #1800 root cause: PowerShell 7's Invoke-WebRequest (with or without
	# -UseBasicParsing) types a WebResponseObject's .Headers indexer as
	# Dictionary<string, string[]> -- $HeadResponse.Headers['Content-Length'] returns
	# a System.String[] even for a header with exactly one value, and PowerShell's
	# [long] cast never auto-unwraps a single-element array: it throws "Cannot
	# convert the 'System.String[]' value ... to type 'System.Int64'" every time,
	# silently swallowed below by this function's own catch. This is deterministic
	# and reproduces with the real, unmodified script directly (no SDK-hosted
	# runspace, no C# executor involved) -- NOT test-harness-specific, confirmed
	# production-relevant. Coerce to a scalar first, the same pattern the 206-resume
	# branch below already uses for Content-Range (issue #1743 review round 1).
	if ($ExpectedSize -le 0) {
		try {
			$HeadResponse = Invoke-WebRequest -Uri $Url -Method Head -UseBasicParsing -TimeoutSec 30 -ErrorAction Stop
			$ContentLengthRaw = $HeadResponse.Headers['Content-Length']
			$ContentLengthValue = if ($null -eq $ContentLengthRaw) { $null } else { @($ContentLengthRaw) -join '' }
			if (-not [string]::IsNullOrWhiteSpace($ContentLengthValue)) {
				$ExpectedSize = [long]$ContentLengthValue
			}
		} catch {
			Write-Log "Could not get Content-Length for $Url" -Severity 'Debug' @WriteLogParams
		}
	}

	# Merge any leftover .resume.tmp from a previous interrupted download
	$TempPath = "${OutFile}.resume.tmp"
	if ($ExpectedSize -gt 0 -and (Test-Path -LiteralPath $OutFile) -and (Test-Path -LiteralPath $TempPath)) {
		$ExistingTmpSize = (Get-Item -LiteralPath $TempPath).Length
		$CurrentPartialSize = (Get-Item -LiteralPath $OutFile).Length
		if ($ExistingTmpSize -gt 0 -and ($CurrentPartialSize + $ExistingTmpSize) -le $ExpectedSize) {
			Write-Log "Merging previous resume data ($ExistingTmpSize bytes) into partial file" -Severity 'Verbose' @WriteLogParams
			$SrcStream = [System.IO.FileStream]::new($TempPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read)
			$DstStream = [System.IO.FileStream]::new($OutFile, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write)
			try { $SrcStream.CopyTo($DstStream) }
			finally { $SrcStream.Close(); $DstStream.Close() }
		}
		Remove-Item -LiteralPath $TempPath -Force -ErrorAction SilentlyContinue
	}

	# Check if file already exists with correct size
	if ($ExpectedSize -gt 0 -and (Test-Path -LiteralPath $OutFile)) {
		$ExistingSize = (Get-Item -LiteralPath $OutFile).Length
		if ($ExistingSize -eq $ExpectedSize) {
			Write-Log "File already exists with correct size: $OutFile" -Severity 'Verbose' @WriteLogParams
			return [PSCustomObject]@{
				Url       = $Url
				LocalPath = $OutFile
				Success   = $true
				Skipped   = $true
				Size      = $ExistingSize
			}
		}
	}

	$LastError = $null
	$BackoffMs = 1000

	for ($Attempt = 1; $Attempt -le $RetryCount; $Attempt++) {
		try {
			# Attempt resume if partial file exists
			$ResumeCompleted = $false
			if ($ExpectedSize -gt 0 -and (Test-Path -LiteralPath $OutFile)) {
				$PartialSize = (Get-Item -LiteralPath $OutFile).Length
				if ($PartialSize -gt $ExpectedSize) {
					Write-Log "Partial file larger than expected ($PartialSize > $ExpectedSize), deleting: $OutFile" -Severity 'Warning' @WriteLogParams
					Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue
				} elseif ($PartialSize -gt 0) {
					Write-Log "Resuming download from byte $PartialSize of ${ExpectedSize}: $Url (attempt $Attempt/$RetryCount)" -Severity 'Verbose' @WriteLogParams
					$ResumeHeaders = @{ 'Range' = "bytes=$PartialSize-" }
					# -PassThru keeps the -OutFile behavior (body streamed to $TempPath)
					# while also returning the response so the status code and
					# Content-Range header -- not the body size -- drive the
					# append-vs-restart decision (issue #1169: an edge-cached
					# object can answer a ranged GET with 200 + the full body,
					# and a size-only heuristic mis-classifies that when the
					# body happens to be <= the expected remainder).
					$RangeResponse = Invoke-WebRequest -Uri $Url -Headers $ResumeHeaders -OutFile $TempPath -PassThru -UseBasicParsing -ErrorAction Stop
					$RangeStatus = [int]$RangeResponse.StatusCode
					$TempSize = (Get-Item -LiteralPath $TempPath).Length

					if ($RangeStatus -eq 206) {
						# A real PS7 -PassThru response's Headers is a
						# Dictionary<string, IEnumerable<string>>, so this
						# indexer yields a String[], not a String (issue
						# #1743 review round 1, finding 1). -match against a
						# collection filters the collection instead of
						# matching a pattern, leaving $Matches unset while
						# the `if` reads as truthy -- coerce to a scalar and
						# use [regex]::Match() instead of the scope-fragile
						# $Matches automatic variable.
						$ContentRangeRaw = $RangeResponse.Headers['Content-Range']
						$ContentRange = if ($null -eq $ContentRangeRaw) { $null } else { @($ContentRangeRaw) -join '' }
						$RangeStart = $null
						$RangeEnd = $null
						if ($ContentRange) {
							$RangeMatch = [regex]::Match($ContentRange, '^bytes\s+(\d+)-(\d+)/(\d+|\*)$')
							if ($RangeMatch.Success) {
								$RangeStart = [long]$RangeMatch.Groups[1].Value
								$RangeEnd = [long]$RangeMatch.Groups[2].Value
							}
						}
						if ($null -eq $RangeStart) {
							Remove-Item -LiteralPath $TempPath -Force -ErrorAction SilentlyContinue
							throw "Ranged request (bytes=$PartialSize-) to $Url returned 206 without a parseable Content-Range header (got '$ContentRange')"
						}
						if ($RangeStart -ne $PartialSize) {
							Remove-Item -LiteralPath $TempPath -Force -ErrorAction SilentlyContinue
							throw "Ranged request (bytes=$PartialSize-) to $Url returned Content-Range starting at $RangeStart instead of the requested $PartialSize"
						}
						$DeclaredLength = $RangeEnd - $RangeStart + 1
						if ($TempSize -ne $DeclaredLength) {
							Remove-Item -LiteralPath $TempPath -Force -ErrorAction SilentlyContinue
							throw "Ranged response body for $Url is $TempSize bytes but Content-Range (bytes $RangeStart-$RangeEnd) declared $DeclaredLength bytes"
						}

						# Partial content (206), range confirmed - append to existing file
						$SourceStream = [System.IO.FileStream]::new($TempPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read)
						$DestStream = [System.IO.FileStream]::new($OutFile, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write)
						try {
							$SourceStream.CopyTo($DestStream)
						} finally {
							$SourceStream.Close()
							$DestStream.Close()
						}
						Remove-Item -LiteralPath $TempPath -Force -ErrorAction SilentlyContinue
					} elseif ($RangeStatus -eq 200) {
						# Server ignored the Range header and answered with (what it
						# claims is) the full body -- observed live from an edge cache
						# (research #1030). Never append: restart the file from zero
						# regardless of whether the body happens to be shorter or
						# longer than the expected remainder; the size-validation step
						# below still catches a short body honestly.
						Write-Log "Ranged request (bytes=$PartialSize-) to $Url answered with 200 instead of 206; restarting download from zero" -Severity 'Warning' @WriteLogParams
						Move-Item -LiteralPath $TempPath -Destination $OutFile -Force
					} else {
						Remove-Item -LiteralPath $TempPath -Force -ErrorAction SilentlyContinue
						throw "Ranged request (bytes=$PartialSize-) to $Url returned unexpected status $RangeStatus"
					}
					$ResumeCompleted = $true
				}
			}

			if (-not $ResumeCompleted) {
				Write-Log "Downloading $Url to $OutFile (attempt $Attempt/$RetryCount)" -Severity 'Verbose' @WriteLogParams
				Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing -ErrorAction Stop
			}

			# Validate size
			if ($ExpectedSize -gt 0) {
				$DownloadedSize = (Get-Item -LiteralPath $OutFile).Length
				if ($DownloadedSize -ne $ExpectedSize) {
					if ($DownloadedSize -gt $ExpectedSize) {
						Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue
					}
					throw "Size mismatch: expected $ExpectedSize, got $DownloadedSize"
				}
			}

			$FinalSize = (Get-Item -LiteralPath $OutFile).Length
			Write-Log "Downloaded $OutFile ($FinalSize bytes)" -Severity 'Success' @WriteLogParams

			return [PSCustomObject]@{
				Url       = $Url
				LocalPath = $OutFile
				Success   = $true
				Skipped   = $false
				Size      = $FinalSize
			}
		} catch {
			$LastError = $_

			# Auth errors - don't retry
			#
			# Windows PowerShell 5.1's Invoke-WebRequest throws
			# System.Net.WebException for a non-2xx response, with the status
			# code on $WebException.Response. PowerShell 7's HttpClient-backed
			# Invoke-WebRequest (this repo's runtime -- ADR-0013, including the
			# SDK-hosted in-process runspace) never throws that type: it throws
			# Microsoft.PowerShell.Commands.HttpResponseException instead, with
			# its own Response property (an HttpResponseMessage, not a
			# WebResponse). Checking only the WebException shape left this
			# branch dead code against any real pwsh7 HTTP failure -- a real
			# 401/403 fell through to the generic retry-with-backoff bucket
			# below instead of failing immediately (issue #1799).
			$StatusCode = $null
			if ($_.Exception -is [System.Net.WebException]) {
				$WebException = $_.Exception -as [System.Net.WebException]
				if ($WebException.Response) {
					$StatusCode = [int]$WebException.Response.StatusCode
				}
			} elseif ($_.Exception -is [Microsoft.PowerShell.Commands.HttpResponseException]) {
				$HttpResponseException = $_.Exception -as [Microsoft.PowerShell.Commands.HttpResponseException]
				if ($HttpResponseException.Response) {
					$StatusCode = [int]$HttpResponseException.Response.StatusCode
				}
			}
			if ($null -ne $StatusCode) {
				if ($StatusCode -in @(401, 403)) {
					throw "Authentication error ($StatusCode): $Url"
				}
				if ($StatusCode -eq 404) {
					Write-Log "File not found (404): $Url" -Severity 'Warning' @WriteLogParams
					return [PSCustomObject]@{
						Url       = $Url
						LocalPath = $OutFile
						Success   = $false
						Skipped   = $true
						Error     = "404 Not Found"
					}
				}
			}

			# Disk errors - don't retry
			if ($_.Exception -is [System.IO.IOException] -and
				$_.Exception.GetType().FullName -notlike '*Http*') {
				throw "Disk error: $_"
			}

			# Transient error - retry with backoff
			if ($Attempt -lt $RetryCount) {
				$Jitter = Get-Random -Minimum 0 -Maximum 500
				$WaitMs = [Math]::Min($BackoffMs + $Jitter, 60000)
				Write-Log "Download failed (attempt $Attempt/$RetryCount), retrying in ${WaitMs}ms: $_" -Severity 'Warning' @WriteLogParams
				Start-Sleep -Milliseconds $WaitMs
				$BackoffMs = [Math]::Min($BackoffMs * 2, 60000)
			} else {
				Write-Log "Download failed (attempt $Attempt/$RetryCount, no retries left): $_" -Severity 'Warning' @WriteLogParams
			}
		}
	}

	# All retries failed
	throw "Download failed after $RetryCount attempts: $LastError"
}

#endregion

#region Get-RemoteWebState

<#
.SYNOPSIS
    Crawl remote web site to discover files and directories.

.DESCRIPTION
    Performs a queue-based crawl starting from a base URL, parsing HTML for links
    and building a manifest of discovered files and directories.

.PARAMETER BaseUrl
    Starting URL for the crawl.

.PARAMETER AllowedPaths
    Optional list of path prefixes to allow. Uses bi-directional prefix matching:
    a path is allowed if it starts with an AllowedPath (under it) or an AllowedPath
    starts with it (parent leading to it). If empty, all paths are allowed.

.PARAMETER TimeoutSec
    Timeout in seconds for each HTTP request. Default is 30.

.PARAMETER Source
    Component identifier for logging.

.OUTPUTS
    [hashtable] Dictionary where:
    - Key: Relative path (directories end with "/")
    - Value: @{ IsDirectory = bool; Size = long or $null }
    In addition (issue #108), the returned hashtable always carries an
    ETS NoteProperty, FailedPaths ([System.Collections.Generic.List[string]]),
    listing the relative path of every subtree whose fetch failed during
    the crawl (empty when the crawl was clean). This is purely additive --
    a plain hashtable's normal members (Keys, Count, the indexer,
    GetEnumerator) are all unaffected, so existing callers that only use
    those see no change in behavior. A crawl with one or more FailedPaths
    entries returned a *partial* listing -- everything under a failed
    path is silently absent from it, indistinguishable from "upstream
    genuinely does not have these files." Callers that need to tell the
    two apart (for example before deleting local files absent from the
    listing) should inspect $Result.FailedPaths and treat any relative
    path that starts with (or equals) one of its entries as "crawl
    incomplete for this path" rather than "confirmed absent upstream" --
    see the companion Test-PathUnderFailedSubtree helper. A mocked
    Get-RemoteWebState that returns a plain hashtable without this
    property is equivalent to an empty FailedPaths list (nothing failed).

.EXAMPLE
    $State = Get-RemoteWebState -BaseUrl 'https://wp-content.vmware.com/v2/latest/'

.EXAMPLE
    $State = Get-RemoteWebState -BaseUrl $Url
    if ($State.FailedPaths.Count -gt 0) { Write-Log "Partial crawl -- failed: $($State.FailedPaths -join ', ')" -Severity 'Warning' }
#>
function Get-RemoteWebState {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$BaseUrl,

		[Parameter()]
		[string[]]$AllowedPaths = @(),

		[Parameter()]
		[int]$TimeoutSec = 30,

		[Parameter()]
		[string]$Source
	)

	$WriteLogParams = @{}
	if ($Source) { $WriteLogParams['Source'] = $Source }

	# Suppress progress bars from Invoke-WebRequest
	$ProgressPreference = 'SilentlyContinue'

	Write-Log "Crawling $BaseUrl" -Severity 'Info' @WriteLogParams

	# Normalize base URL
	if (-not $BaseUrl.EndsWith('/')) {
		$BaseUrl = "$BaseUrl/"
	}

	$Results = @{}
	$FileCount = 0
	$DirCount = 0
	$FailedPaths = [System.Collections.Generic.List[string]]::new()
	$Queue = [System.Collections.Generic.Queue[string]]::new()
	$Visited = [System.Collections.Generic.HashSet[string]]::new()

	$Queue.Enqueue('')  # Start from root

	while ($Queue.Count -gt 0) {
		$CurrentPath = $Queue.Dequeue()

		if ($Visited.Contains($CurrentPath)) {
			continue
		}
		$Visited.Add($CurrentPath) | Out-Null

		$CurrentUrl = "$BaseUrl$CurrentPath"
		$DisplayPath = if ($CurrentPath) { $CurrentPath } else { '/' }
		Write-Log "Crawling $DisplayPath (queued: $($Queue.Count), found: $FileCount files, $DirCount dirs)" -Severity 'Debug' @WriteLogParams

		try {
			$Response = Invoke-WebRequest -Uri $CurrentUrl -UseBasicParsing -TimeoutSec $TimeoutSec -ErrorAction Stop
			$Content = $Response.Content

			# Parse links from HTML
			$LinkPattern = 'href=["\x27]([^"\x27]+)["\x27]'
			$LinkMatches = [regex]::Matches($Content, $LinkPattern)

			foreach ($Match in $LinkMatches) {
				$Href = $Match.Groups[1].Value

				# Skip parent/self links and query strings
				if ($Href -match '^\.\./' -or $Href -eq './' -or $Href -match '^\?' -or $Href -match '^#') {
					continue
				}

				# Skip absolute URLs to other domains
				if ($Href -match '^https?://' -and -not $Href.StartsWith($BaseUrl)) {
					continue
				}

				# Normalize href
				if ($Href -match '^https?://') {
					$Href = $Href.Substring($BaseUrl.Length)
				}

				# Remove leading ./
				$Href = $Href -replace '^\.\/', ''

				# Build full relative path
				$FullPath = if ($CurrentPath) { "$CurrentPath$Href" } else { $Href }

				# Check against allowed paths (bi-directional prefix match)
				if ($AllowedPaths.Count -gt 0) {
					$Allowed = $false
					foreach ($AllowedPath in $AllowedPaths) {
						if ($FullPath.StartsWith($AllowedPath) -or $AllowedPath.StartsWith($FullPath)) {
							$Allowed = $true
							break
						}
					}
					if (-not $Allowed) {
						continue
					}
				}

				# Determine if this is a directory or file
				if ($Href.EndsWith('/')) {
					# Directory
					if (-not $Results.ContainsKey($FullPath)) {
						$Results[$FullPath] = @{
							IsDirectory = $true
							Size        = $null
						}
						$DirCount++
						$Queue.Enqueue($FullPath)
					}
				} else {
					# File - get size via HEAD request
					if (-not $Results.ContainsKey($FullPath)) {
						$FileSize = $null
						try {
							$HeadUrl = "$BaseUrl$FullPath"
							$Handler = [System.Net.Http.HttpClientHandler]::new()
							$Handler.AutomaticDecompression = [System.Net.DecompressionMethods]::None
							$Client = [System.Net.Http.HttpClient]::new($Handler)
							$Client.Timeout = [TimeSpan]::FromSeconds($TimeoutSec)
							try {
								$Request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Head, $HeadUrl)
								$HeadResponse = $Client.SendAsync($Request).GetAwaiter().GetResult()
								if ($HeadResponse.Content.Headers.ContentLength) {
									$FileSize = $HeadResponse.Content.Headers.ContentLength
								}
							} finally {
								$Client.Dispose()
							}
						} catch {
							Write-Log "Could not get size for $FullPath" -Severity 'Debug' @WriteLogParams
						}

						$Results[$FullPath] = @{
							IsDirectory = $false
							Size        = $FileSize
						}
						$FileCount++
					}
				}
			}
		} catch {
			Write-Log "Failed to crawl $DisplayPath`: $_" -Severity 'Warning' @WriteLogParams
			$FailedPaths.Add($CurrentPath)
		}
	}

	Write-Log "Crawl complete: $FileCount files, $DirCount directories at $BaseUrl" -Severity 'Info' @WriteLogParams

	# Issue #108: attach the failed-subtree list as an ETS NoteProperty on
	# the returned hashtable rather than an output ([ref]/List) parameter.
	# A caller-supplied out-parameter would need to be mutated *through*
	# this function call, but Pester's Mock machinery clones bound
	# argument values before invoking a mock's scriptblock body, silently
	# breaking that mutation for every test that mocks this function (the
	# overwhelming majority of its callers' tests) -- the object the mock
	# receives and the object the caller holds are no longer the same
	# instance. Piggybacking on the return value sidesteps that entirely:
	# return values are never cloned, so this survives being mocked.
	Add-Member -InputObject $Results -NotePropertyName FailedPaths -NotePropertyValue $FailedPaths -Force

	return $Results
}

#endregion

#region Test-PathUnderFailedSubtree

<#
.SYNOPSIS
	Tests whether a relative path falls under a subtree whose remote
	crawl failed.

.DESCRIPTION
	Companion helper to Get-RemoteWebState's returned-state FailedPaths
	property (issue #108). A crawl failure on some path P means
	Get-RemoteWebState's returned state is silently missing everything
	that lives under P -- so a local file under P that is absent from
	the returned state cannot be trusted as "upstream removed it"; the
	crawl of P simply never completed. Callers use this to gate a
	stale-file deletion decision per candidate: a local file whose
	relative path is at or under any failed path must never be deleted
	on the strength of a partial crawl, even though it is missing from
	the (incomplete) remote listing.

.PARAMETER RelativePath
	The local file's path relative to the mirror root, in the same
	forward-slash-normalized form Get-RemoteWebState's returned state
	keys and FailedPaths entries use.

.PARAMETER FailedPaths
	The collection from Get-RemoteWebState's returned state's
	FailedPaths property (or any equivalent [string[]] of paths whose
	crawl failed). $null or empty means nothing failed -- always
	returns $false.

.OUTPUTS
	[bool] $true if RelativePath is at or under a failed subtree.

.EXAMPLE
	Test-PathUnderFailedSubtree -RelativePath 'noarch/pkg.rpm' -FailedPaths @('noarch/')
	Returns $true -- pkg.rpm lives under the noarch/ subtree whose crawl failed.
#>
function Test-PathUnderFailedSubtree {
	[CmdletBinding()]
	[OutputType([bool])]
	param(
		[Parameter(Mandatory)]
		[AllowEmptyString()]
		[string]$RelativePath,

		[Parameter()]
		[AllowNull()]
		[AllowEmptyCollection()]
		[string[]]$FailedPaths
	)

	if (-not $FailedPaths -or $FailedPaths.Count -eq 0) {
		return $false
	}

	foreach ($FailedPath in $FailedPaths) {
		if ($RelativePath.StartsWith($FailedPath, [System.StringComparison]::Ordinal)) {
			return $true
		}
	}

	return $false
}

#endregion
