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
# Direct Pester suite for the download-runner's own copy of
# vcf-download-manager.common.ps1 (issue #1356, epic #1361). Dot-sources the
# module straight from the runner tree -- not through backend/, which only
# ever exercises this script indirectly via WaypointDownload/WaypointCatalogIndex
# shims and a hand-written fake (see
# backend/Waypoint.Tests/Infrastructure/PowerShell/WaypointDownloadLoggingTests.cs).
# All fixtures below (URLs, hosts, file names, byte counts) are invented for this
# suite; none of them are real depot/catalog values.

BeforeAll {
	. "$PSScriptRoot/../powershell/project/vcf-download-manager.common.ps1"
}

Describe 'Format-ByteSize' {
	It 'formats <Bytes> bytes as <Expected>' -ForEach @(
		@{ Bytes = 512; Expected = '512 B' }
		@{ Bytes = 2048; Expected = '2 KB' }
		@{ Bytes = 5MB; Expected = '5.0 MB' }
		@{ Bytes = 2GB; Expected = '2.00 GB' }
	) {
		Format-ByteSize -Bytes $Bytes | Should -Be $Expected
	}

	# Unit-boundary regression (issue #1719): all three -ge comparisons in
	# Format-ByteSize are boundary-inclusive by design (exactly 1KB reads as
	# "1 KB", not "1024 B"; exactly 1MB reads as "1.0 MB", not "1,024 KB";
	# exactly 1GB reads as "1.00 GB", not "1,024.0 MB"). A -ge -> -gt mutation
	# at any one of the three thresholds silently shifts that exact value down
	# one unit while every other case in this Describe still passes, so each
	# boundary has to be asserted directly. One case per threshold, so a
	# single-threshold mutation fails exactly one test.
	It 'formats exactly 1KB as 1 KB, not 1024 B' {
		Format-ByteSize -Bytes 1KB | Should -Be '1 KB'
	}

	It 'formats exactly 1MB as 1.0 MB, not 1,024 KB' {
		Format-ByteSize -Bytes 1MB | Should -Be '1.0 MB'
	}

	It 'formats exactly 1GB as 1.00 GB, not 1,024.0 MB' {
		Format-ByteSize -Bytes 1GB | Should -Be '1.00 GB'
	}
}

Describe 'Write-Log' {
	BeforeEach {
		$Global:LogLevel = 'Info'
		$Global:SilentMode = $false
		$Global:LogPath = $null
	}

	AfterEach {
		Remove-Variable -Name LogLevel, SilentMode, LogPath -Scope Global -ErrorAction SilentlyContinue
	}

	It 'writes to the console via Write-Host when not silent and above the level floor' {
		Mock Write-Host {}
		Write-Log 'hello from a test' -Severity 'Info'
		Should -Invoke Write-Host -Times 1 -ParameterFilter { $Object -like '*hello from a test*' }
	}

	It 'suppresses console output below the configured LogLevel' {
		$Global:LogLevel = 'Warning'
		Mock Write-Host {}
		Write-Log 'below the floor' -Severity 'Info'
		Should -Invoke Write-Host -Times 0
	}

	It 'suppresses console output entirely in SilentMode' {
		$Global:SilentMode = $true
		Mock Write-Host {}
		Write-Log 'silent mode message' -Severity 'Critical'
		Should -Invoke Write-Host -Times 0
	}

	It 'always appends to LogPath regardless of severity filtering' {
		$Global:LogLevel = 'Critical'
		$Global:SilentMode = $true
		$LogFile = Join-Path -Path 'TestDrive:' -ChildPath 'run.log'
		$Global:LogPath = $LogFile
		Write-Log 'debug message reaches the file' -Severity 'Debug'
		(Get-Content -Path $LogFile -Raw) | Should -Match 'debug message reaches the file'
	}

	It 'ignores null/empty/whitespace messages entirely' {
		Mock Write-Host {}
		Write-Log '' -Severity 'Info'
		Write-Log '   ' -Severity 'Info'
		Should -Invoke Write-Host -Times 0
	}

	It 'prefixes the Source component when supplied' {
		Mock Write-Host {}
		Write-Log 'sourced message' -Severity 'Info' -Source 'UnitTest'
		Should -Invoke Write-Host -Times 1 -ParameterFilter { $Object -like '*[UnitTest]*sourced message*' }
	}
}

Describe 'Set-Permissions' {
	# Native chmod/stat need a real filesystem path, not the TestDrive: PSDrive
	# literal -- $TestDrive is Pester's automatic variable holding that real path.

	It 'chmods a Linux/macOS path recursively for other-read/execute' {
		if (-not ($IsLinux -or $IsMacOS)) {
			Set-ItResult -Skipped -Because 'this suite runs on Linux CI/devcontainer; the Windows no-op branch is not exercised here'
			return
		}

		$Dir = Join-Path -Path $TestDrive -ChildPath 'perm-target'
		New-Item -Path $Dir -ItemType Directory -Force | Out-Null
		$File = Join-Path -Path $Dir -ChildPath 'file.txt'
		Set-Content -Path $File -Value 'content'
		# Start from a restrictive mode so a real chmod change is observable.
		& chmod 700 $Dir
		& chmod 600 $File

		Set-Permissions -Path $Dir

		$FileMode = (& stat -c '%a' $File)
		# o+rX: file (no exec bit) gains other-read only -> trailing digit gets +4.
		$FileMode | Should -Match '[4567]$'
	}

	It 'throws when the path does not exist' {
		{ Set-Permissions -Path (Join-Path -Path $TestDrive -ChildPath 'does-not-exist') } | Should -Throw
	}

	It 'takes no action under -WhatIf' {
		if (-not ($IsLinux -or $IsMacOS)) {
			Set-ItResult -Skipped -Because 'Linux-only chmod path'
			return
		}
		$Dir = Join-Path -Path $TestDrive -ChildPath 'whatif-target'
		New-Item -Path $Dir -ItemType Directory -Force | Out-Null
		& chmod 700 $Dir
		$Before = (& stat -c '%a' $Dir)

		Set-Permissions -Path $Dir -WhatIf

		(& stat -c '%a' $Dir) | Should -Be $Before
	}
}

Describe 'Get-FileManifest' {
	# Uses [System.IO.DirectoryInfo]/[System.IO.Path] directly, so it needs a
	# real filesystem path -- $TestDrive, not the TestDrive: PSDrive literal.

	It 'throws when the directory does not exist' {
		{ Get-FileManifest -Directory (Join-Path -Path $TestDrive -ChildPath 'missing') } | Should -Throw
	}

	It 'builds a manifest keyed by forward-slash relative path with size and mtime' {
		$Root = Join-Path -Path $TestDrive -ChildPath 'manifest-root'
		New-Item -Path (Join-Path $Root 'sub') -ItemType Directory -Force | Out-Null
		Set-Content -Path (Join-Path $Root 'top.txt') -Value 'abcde' -NoNewline
		Set-Content -Path (Join-Path $Root 'sub/nested.txt') -Value 'xy' -NoNewline

		$Manifest = Get-FileManifest -Directory $Root

		$Manifest.Keys | Should -Contain 'top.txt'
		$Manifest.Keys | Should -Contain 'sub/nested.txt'
		$Manifest['top.txt'].Size | Should -Be 5
	}

	It 'includes a hash per entry when -IncludeHash is set' {
		$Root = Join-Path -Path $TestDrive -ChildPath 'manifest-hash'
		New-Item -Path $Root -ItemType Directory -Force | Out-Null
		Set-Content -Path (Join-Path $Root 'file.bin') -Value 'payload'

		$Manifest = Get-FileManifest -Directory $Root -IncludeHash -HashAlgorithm 'SHA256'

		$Manifest['file.bin'].Hash | Should -Not -BeNullOrEmpty
	}

	It 'exports to -OutputFile as JSON when supplied' {
		$Root = Join-Path -Path $TestDrive -ChildPath 'manifest-export'
		New-Item -Path $Root -ItemType Directory -Force | Out-Null
		Set-Content -Path (Join-Path $Root 'a.txt') -Value 'a'
		$Out = Join-Path -Path $TestDrive -ChildPath 'manifest.json'

		Get-FileManifest -Directory $Root -OutputFile $Out | Out-Null

		Test-Path -Path $Out | Should -BeTrue
		(Get-Content -Path $Out -Raw) | Should -Match 'a\.txt'
	}

	# Issue #1718: an unreadable subdirectory must never be silently treated as
	# empty -- the reviewer's probe on PR #1716 found a `chmod 000` subtree
	# vanished from the manifest with no error at all.
	It 'surfaces an unreadable subdirectory instead of silently omitting it' {
		if (-not ($IsLinux -or $IsMacOS)) {
			Set-ItResult -Skipped -Because 'chmod mode bits are not meaningful on Windows'
			return
		}
		if ((& id -u) -eq '0') {
			Set-ItResult -Skipped -Because 'running as root ignores mode bits, so the directory would remain readable'
			return
		}

		# Round-1 review finding F4: the fixture root must NOT contain the
		# locked child's own name, or a wildcard match against the root name
		# alone (which every thrown message already interpolates via
		# "under '$Directory'") would pass against an implementation that
		# reports nothing useful about which subdirectory failed.
		$Root = Join-Path -Path $TestDrive -ChildPath 'manifest-scan-root'
		$Good = Join-Path $Root 'good'
		$Sealed = Join-Path $Root 'sealed'
		New-Item -Path $Good -ItemType Directory -Force | Out-Null
		New-Item -Path $Sealed -ItemType Directory -Force | Out-Null
		Set-Content -Path (Join-Path $Good 'f.txt') -Value 'abc' -NoNewline
		Set-Content -Path (Join-Path $Sealed 'hidden.txt') -Value 'xyz' -NoNewline

		try {
			& chmod 000 $Sealed
			Mock Write-Log {}

			{ Get-FileManifest -Directory $Root } | Should -Throw -ExpectedMessage "*$Sealed*"
			Should -Invoke Write-Log -ParameterFilter { $Severity -eq 'Warning' -and $Message -like "*$Sealed*" }
		} finally {
			& chmod 700 $Sealed
		}
	}
}

Describe 'Remove-EmptyDirs' {
	It 'returns 0 and warns when the directory does not exist' {
		Remove-EmptyDirs -Directory (Join-Path -Path 'TestDrive:' -ChildPath 'nope') | Should -Be 0
	}

	It 'removes empty directories bottom-up but keeps non-empty ones' {
		$Root = Join-Path -Path 'TestDrive:' -ChildPath 'prune-root'
		$Empty = Join-Path $Root 'empty-child'
		$Kept = Join-Path $Root 'kept-child'
		New-Item -Path $Empty -ItemType Directory -Force | Out-Null
		New-Item -Path $Kept -ItemType Directory -Force | Out-Null
		Set-Content -Path (Join-Path $Kept 'keep.txt') -Value 'x'

		$Removed = Remove-EmptyDirs -Directory $Root

		Test-Path -Path $Empty | Should -BeFalse
		Test-Path -Path $Kept | Should -BeTrue
		$Removed | Should -BeGreaterOrEqual 1
	}

	It 'removes nothing under -WhatIf' {
		$Root = Join-Path -Path 'TestDrive:' -ChildPath 'prune-whatif'
		$Empty = Join-Path $Root 'empty-child'
		New-Item -Path $Empty -ItemType Directory -Force | Out-Null

		Remove-EmptyDirs -Directory $Root -WhatIf | Out-Null

		Test-Path -Path $Empty | Should -BeTrue
	}

	# Issue #1718: an unreadable directory must never be treated as empty --
	# no removal attempt, and the caller must be able to tell the walk was
	# incomplete rather than trusting a smaller-but-healthy-looking result.
	It 'leaves an unreadable directory untouched and surfaces the failure' {
		if (-not ($IsLinux -or $IsMacOS)) {
			Set-ItResult -Skipped -Because 'chmod mode bits are not meaningful on Windows'
			return
		}
		if ((& id -u) -eq '0') {
			Set-ItResult -Skipped -Because 'running as root ignores mode bits, so the directory would remain readable'
			return
		}

		# Round-1 review finding F4: same rationale as Get-FileManifest's case
		# above -- the root's own name must not contain the locked child's
		# name, and the assertion must key off the child's own path.
		$Root = Join-Path -Path $TestDrive -ChildPath 'prune-scan-root'
		$Sealed = Join-Path $Root 'sealed'
		New-Item -Path $Sealed -ItemType Directory -Force | Out-Null
		Set-Content -Path (Join-Path $Sealed 'hidden.txt') -Value 'xyz' -NoNewline

		try {
			& chmod 000 $Sealed
			Mock Write-Log {}

			{ Remove-EmptyDirs -Directory $Root } | Should -Throw -ExpectedMessage "*$Sealed*"
			Should -Invoke Write-Log -ParameterFilter { $Severity -eq 'Warning' -and $Message -like "*$Sealed*" }
			Test-Path -Path $Sealed | Should -BeTrue
		} finally {
			& chmod 700 $Sealed
		}
	}
}

Describe 'Test-IsRecent' {
	It 'returns true for a date within the retention window' {
		Test-IsRecent -Date (Get-Date).ToUniversalTime().AddDays(-1) -Months 6 | Should -BeTrue
	}

	It 'returns false for a date older than the retention window' {
		Test-IsRecent -Date (Get-Date).ToUniversalTime().AddMonths(-7) -Months 6 | Should -BeFalse
	}

	It 'treats a date just inside the cutoff boundary as recent' {
		# A few seconds inside the -6 month cutoff rather than the exact
		# instant: the function re-evaluates "now" internally, so pinning the
		# test to the literal boundary is racy by design.
		Test-IsRecent -Date (Get-Date).ToUniversalTime().AddMonths(-6).AddMinutes(1) -Months 6 | Should -BeTrue
	}
}

Describe 'Test-DirectoryAccess' {
	# Uses [System.IO.File]::WriteAllText directly for the writability probe,
	# so it needs a real filesystem path -- $TestDrive, not the TestDrive:
	# PSDrive literal.

	It 'reports Exists=false and Writable=$null for a missing directory' {
		$Result = Test-DirectoryAccess -Directories @((Join-Path -Path $TestDrive -ChildPath 'missing'))
		$Only = $Result.Values | Select-Object -First 1
		$Only.Exists | Should -BeFalse
		$Only.Writable | Should -BeNullOrEmpty
	}

	It 'reports Exists=true and Writable=true for a writable existing directory' {
		$Dir = Join-Path -Path $TestDrive -ChildPath 'writable'
		New-Item -Path $Dir -ItemType Directory -Force | Out-Null

		$Result = Test-DirectoryAccess -Directories @($Dir) -RequireWritable
		$Result[$Dir].Exists | Should -BeTrue
		$Result[$Dir].Writable | Should -BeTrue
	}

	It 'throws on the first missing directory when -ThrowOnFail is set' {
		{ Test-DirectoryAccess -Directories @((Join-Path -Path $TestDrive -ChildPath 'missing')) -ThrowOnFail } | Should -Throw
	}
}

Describe 'Save-WebFile' {
	BeforeEach {
		Mock Start-Sleep {}
	}

	It 'downloads successfully on the first attempt' {
		Mock Invoke-WebRequest {
			if ($Method -eq 'Head') {
				return [pscustomobject]@{ Headers = @{ 'Content-Length' = '7' } }
			}
			Set-Content -Path $OutFile -Value 'payload' -NoNewline
		}

		$Out = Join-Path -Path 'TestDrive:' -ChildPath 'download/one.bin'
		$Result = Save-WebFile -Url 'https://example.invalid/one.bin' -OutFile $Out

		$Result.Success | Should -BeTrue
		$Result.Skipped | Should -BeFalse
		Test-Path -Path $Out | Should -BeTrue
	}

	It 'skips the download when a correctly-sized file already exists' {
		$Out = Join-Path -Path 'TestDrive:' -ChildPath 'download/existing.bin'
		New-Item -Path (Split-Path $Out -Parent) -ItemType Directory -Force | Out-Null
		Set-Content -Path $Out -Value '1234567' -NoNewline
		Mock Invoke-WebRequest {}

		$Result = Save-WebFile -Url 'https://example.invalid/existing.bin' -OutFile $Out -ExpectedSize 7

		$Result.Skipped | Should -BeTrue
		Should -Invoke Invoke-WebRequest -Times 0
	}

	It 'surfaces a plain WebException as a retried-then-thrown failure' {
		# A bare System.Net.WebException (no populated .Response, which is
		# what this runtime constructs without a real HTTP round-trip) does
		# not match the function's 401/403/404 status-code branch, so it
		# falls through to the generic retry-then-throw path -- this pins
		# that fallback rather than asserting the unreachable-here 404
		# short-circuit.
		Mock Invoke-WebRequest { throw [System.Net.WebException]::new('not found') }

		$Out = Join-Path -Path 'TestDrive:' -ChildPath 'download/missing.bin'
		{ Save-WebFile -Url 'https://example.invalid/missing.bin' -OutFile $Out -RetryCount 2 } | Should -Throw
		Should -Invoke Invoke-WebRequest -Times 2
	}

	It 'throws after exhausting all retries on a persistent transient failure' {
		Mock Invoke-WebRequest { throw 'simulated transient network failure' }

		$Out = Join-Path -Path 'TestDrive:' -ChildPath 'download/flaky.bin'
		{ Save-WebFile -Url 'https://example.invalid/flaky.bin' -OutFile $Out -RetryCount 2 } | Should -Throw
		Should -Invoke Invoke-WebRequest -Times 2
	}

	It 'raises a size-mismatch error when the downloaded file is the wrong size' {
		Mock Invoke-WebRequest {
			if ($Method -eq 'Head') { return }
			Set-Content -Path $OutFile -Value 'short' -NoNewline
		}

		$Out = Join-Path -Path 'TestDrive:' -ChildPath 'download/mismatch.bin'
		{ Save-WebFile -Url 'https://example.invalid/mismatch.bin' -OutFile $Out -ExpectedSize 999 -RetryCount 1 } | Should -Throw
	}

	It 'does not retry on a 401/403 auth error' {
		Mock Invoke-WebRequest {
			$WebEx = [System.Net.WebException]::new('unauthorized')
			$WebEx | Add-Member -NotePropertyName Response -NotePropertyValue ([pscustomobject]@{ StatusCode = 401 }) -Force
			throw $WebEx
		}

		$Out = Join-Path -Path 'TestDrive:' -ChildPath 'download/auth.bin'
		{ Save-WebFile -Url 'https://example.invalid/auth.bin' -OutFile $Out -RetryCount 5 } | Should -Throw '*Authentication error*'
		Should -Invoke Invoke-WebRequest -Times 1
	}

	It 'merges a leftover .resume.tmp fragment into an existing partial file before retrying' {
		# Uses [System.IO.FileStream] directly to merge -- needs a real
		# filesystem path, not the TestDrive: PSDrive literal.
		$Out = Join-Path -Path $TestDrive -ChildPath 'download/resume.bin'
		New-Item -Path (Split-Path $Out -Parent) -ItemType Directory -Force | Out-Null
		Set-Content -Path $Out -Value '1234' -NoNewline
		Set-Content -Path "$Out.resume.tmp" -Value '567' -NoNewline

		Mock Invoke-WebRequest {
			# Range-resume request: server answers with the remaining bytes.
			Set-Content -Path $OutFile -Value '890' -NoNewline
			[pscustomobject]@{ StatusCode = 206; Headers = @{ 'Content-Range' = 'bytes 7-9/10' } }
		}

		$Result = Save-WebFile -Url 'https://example.invalid/resume.bin' -OutFile $Out -ExpectedSize 10

		$Result.Success | Should -BeTrue
		Test-Path -Path "$Out.resume.tmp" | Should -BeFalse
	}

	It 'discards a partial file larger than expected and re-downloads from scratch' {
		$Out = Join-Path -Path 'TestDrive:' -ChildPath 'download/oversized.bin'
		New-Item -Path (Split-Path $Out -Parent) -ItemType Directory -Force | Out-Null
		Set-Content -Path $Out -Value '1234567890EXTRA' -NoNewline

		Mock Invoke-WebRequest {
			if ($Method -eq 'Head') { return }
			Set-Content -Path $OutFile -Value '1234567890' -NoNewline
		}

		$Result = Save-WebFile -Url 'https://example.invalid/oversized.bin' -OutFile $Out -ExpectedSize 10

		$Result.Success | Should -BeTrue
		(Get-Item -LiteralPath $Out).Length | Should -Be 10
	}

	It 'appends a 206 partial-content Range response onto the existing partial file' {
		# Uses [System.IO.FileStream] directly to append -- needs a real
		# filesystem path, not the TestDrive: PSDrive literal.
		$Out = Join-Path -Path $TestDrive -ChildPath 'download/partial.bin'
		New-Item -Path (Split-Path $Out -Parent) -ItemType Directory -Force | Out-Null
		Set-Content -Path $Out -Value '12345' -NoNewline

		Mock Invoke-WebRequest {
			# Server honors Range and returns only the remaining bytes, with a
			# Content-Range confirming the range start matches the partial size.
			Set-Content -Path $OutFile -Value '67890' -NoNewline
			[pscustomobject]@{ StatusCode = 206; Headers = @{ 'Content-Range' = 'bytes 5-9/10' } }
		}

		$Result = Save-WebFile -Url 'https://example.invalid/partial.bin' -OutFile $Out -ExpectedSize 10

		$Result.Success | Should -BeTrue
		[System.IO.File]::ReadAllBytes($Out) | Should -Be ([System.Text.Encoding]::ASCII.GetBytes('1234567890'))
	}

	It 'restarts from zero on a 200 response to a ranged request (edge cache ignored Range), logging a Warning' {
		# issue #1169: an edge-cached vendor object can answer a ranged GET
		# with 200 + the full body instead of 206. The old heuristic compared
		# body size to the expected remainder; here the 200 body is LARGER
		# than the remainder (10 - 3 = 7), which the old heuristic also
		# happened to classify correctly, but the decision must be driven by
		# the status code, not the size, so this pins that.
		$Out = Join-Path -Path $TestDrive -ChildPath 'download/full-on-range.bin'
		New-Item -Path (Split-Path $Out -Parent) -ItemType Directory -Force | Out-Null
		Set-Content -Path $Out -Value '123' -NoNewline

		Mock Invoke-WebRequest {
			# Server ignores Range and returns the whole file in the temp path.
			Set-Content -Path $OutFile -Value '1234567890' -NoNewline
			[pscustomobject]@{ StatusCode = 200; Headers = @{} }
		}
		Mock Write-Log {}

		$Result = Save-WebFile -Url 'https://example.invalid/full-on-range.bin' -OutFile $Out -ExpectedSize 10

		$Result.Success | Should -BeTrue
		[System.IO.File]::ReadAllBytes($Out) | Should -Be ([System.Text.Encoding]::ASCII.GetBytes('1234567890'))
		Should -Invoke Write-Log -ParameterFilter { $Severity -eq 'Warning' -and $Message -like '*answered with 200 instead of 206*' }
	}

	It 'restarts from zero on a 200 response whose body is SHORTER than the remainder, and the size check then fails honestly' {
		# The old size-only heuristic's blind spot: a 200 body that happens to
		# be <= the expected remainder was misread as a legitimate partial
		# response and appended, corrupting the file. It must still be
		# recognized as a restart (full body, wrong offset) and the eventual
		# size mismatch must surface as a real error, not a silently-corrupt
		# "success".
		$Out = Join-Path -Path $TestDrive -ChildPath 'download/short-on-restart.bin'
		New-Item -Path (Split-Path $Out -Parent) -ItemType Directory -Force | Out-Null
		Set-Content -Path $Out -Value '123' -NoNewline

		Mock Invoke-WebRequest {
			# Remainder would be 7 bytes (10 - 3); this 200 body is only 4 bytes,
			# well under the remainder -- the size heuristic would have appended it.
			Set-Content -Path $OutFile -Value 'ABCD' -NoNewline
			[pscustomobject]@{ StatusCode = 200; Headers = @{} }
		}

		{ Save-WebFile -Url 'https://example.invalid/short-on-restart.bin' -OutFile $Out -ExpectedSize 10 -RetryCount 1 } | Should -Throw '*Size mismatch*'

		[System.IO.File]::ReadAllBytes($Out) | Should -Be ([System.Text.Encoding]::ASCII.GetBytes('ABCD'))
	}

	It 'throws when a 206 response Content-Range start does not match the requested offset' {
		$Out = Join-Path -Path $TestDrive -ChildPath 'download/range-mismatch.bin'
		New-Item -Path (Split-Path $Out -Parent) -ItemType Directory -Force | Out-Null
		Set-Content -Path $Out -Value '12345' -NoNewline

		Mock Invoke-WebRequest {
			# Server answers 206 but for a different offset than requested.
			Set-Content -Path $OutFile -Value 'XYZ' -NoNewline
			[pscustomobject]@{ StatusCode = 206; Headers = @{ 'Content-Range' = 'bytes 2-4/10' } }
		}

		{ Save-WebFile -Url 'https://example.invalid/range-mismatch.bin' -OutFile $Out -ExpectedSize 10 -RetryCount 1 } | Should -Throw '*Content-Range starting at 2*'
	}

	It 'throws when a 206 response body length disagrees with its own Content-Range' {
		$Out = Join-Path -Path $TestDrive -ChildPath 'download/range-body-mismatch.bin'
		New-Item -Path (Split-Path $Out -Parent) -ItemType Directory -Force | Out-Null
		Set-Content -Path $Out -Value '12345' -NoNewline

		Mock Invoke-WebRequest {
			# Content-Range claims 5 bytes (5-9) but the body is only 2 bytes.
			Set-Content -Path $OutFile -Value 'XY' -NoNewline
			[pscustomobject]@{ StatusCode = 206; Headers = @{ 'Content-Range' = 'bytes 5-9/10' } }
		}

		{ Save-WebFile -Url 'https://example.invalid/range-body-mismatch.bin' -OutFile $Out -ExpectedSize 10 -RetryCount 1 } | Should -Throw '*declared 5 bytes*'
	}
}

Describe 'Get-RemoteWebState' {
	It 'crawls a single-page site and records no failed paths' {
		Mock Invoke-WebRequest {
			[pscustomobject]@{ Content = '<a href="file-a.txt">file-a.txt</a>' }
		}

		$State = Get-RemoteWebState -BaseUrl 'https://example.invalid/' -TimeoutSec 1

		$State.Keys | Should -Contain 'file-a.txt'
		$State.FailedPaths.Count | Should -Be 0
	}

	It 'records a FailedPaths entry when a subtree fetch throws' {
		Mock Invoke-WebRequest { throw 'simulated crawl failure' }

		$State = Get-RemoteWebState -BaseUrl 'https://example.invalid/' -TimeoutSec 1

		$State.FailedPaths | Should -Contain ''
	}

	It 'enqueues a discovered directory link for its own crawl pass' {
		Mock Invoke-WebRequest {
			if ($Uri -eq 'https://example.invalid/') {
				return [pscustomobject]@{ Content = '<a href="subdir/">subdir/</a>' }
			}
			[pscustomobject]@{ Content = '' }
		}

		$State = Get-RemoteWebState -BaseUrl 'https://example.invalid/' -TimeoutSec 1

		$State['subdir/'].IsDirectory | Should -BeTrue
		Should -Invoke Invoke-WebRequest -Times 2
	}

	It 'skips a discovered path outside of -AllowedPaths' {
		Mock Invoke-WebRequest {
			[pscustomobject]@{ Content = '<a href="excluded/file.txt">file.txt</a><a href="included/file.txt">file.txt</a>' }
		}

		$State = Get-RemoteWebState -BaseUrl 'https://example.invalid/' -AllowedPaths @('included/') -TimeoutSec 1

		$State.Keys | Should -Not -Contain 'excluded/file.txt'
		$State.Keys | Should -Contain 'included/file.txt'
	}
}

Describe 'Test-PathUnderFailedSubtree' {
	It 'returns false when FailedPaths is empty or null' {
		Test-PathUnderFailedSubtree -RelativePath 'a/b.txt' -FailedPaths @() | Should -BeFalse
		Test-PathUnderFailedSubtree -RelativePath 'a/b.txt' -FailedPaths $null | Should -BeFalse
	}

	It 'returns true when the path is under a failed subtree' {
		Test-PathUnderFailedSubtree -RelativePath 'noarch/pkg.rpm' -FailedPaths @('noarch/') | Should -BeTrue
	}

	It 'returns false when the path is not under any failed subtree' {
		Test-PathUnderFailedSubtree -RelativePath 'other/pkg.rpm' -FailedPaths @('noarch/') | Should -BeFalse
	}
}
