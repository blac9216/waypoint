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

# Invented stub module for the epic #6 slice-2 test harness (issue #149). No vendor
# code, no real hostnames or credentials -- everything here is fabricated to exercise
# the executor: object marshaling, all five output streams, parameter binding
# fidelity, terminating errors, and a Stop()-resistant hang.

function Get-StubInventory {
    [CmdletBinding()]
    param([int] $Count = 2)
    1..$Count | ForEach-Object {
        [pscustomobject]@{
            Name    = "esxi-{0:d2}.example.internal" -f $_
            Version = '8.0.3'
            Index   = $_
        }
    }
}

function Write-StubStreams {
    [CmdletBinding()]
    param()
    $InformationPreference = 'Continue'
    $VerbosePreference = 'Continue'
    $DebugPreference = 'Continue'
    Write-Information 'stub information line'
    Write-Warning 'stub warning line'
    Write-Error 'stub non-terminating error line'
    Write-Verbose 'stub verbose line'
    Write-Debug 'stub debug line'
}

function Get-StubEcho {
    [CmdletBinding()]
    param([string] $Value)
    # Returns the bound parameter untouched: if the executor interpolated instead of
    # binding, PowerShell metacharacters in $Value would have been evaluated and the
    # round-trip would not be byte-identical.
    $Value
}

function Invoke-StubFailure {
    [CmdletBinding()]
    param([string] $Message = 'stub terminating failure')
    throw $Message
}

function Invoke-StubHang {
    [CmdletBinding()]
    param([int] $Seconds = 60)
    # A tight native-ish loop that never yields to the pipeline stop machinery the
    # way Start-Sleep does: [Thread]::Sleep in a loop ignores PipelineStopped until
    # the current sleep slice returns, and the slices are long enough to outlive the
    # executor's stop grace period.
    [System.Threading.Thread]::Sleep($Seconds * 1000)
}

function Write-StubSecretLeak {
    [CmdletBinding()]
    param([string] $Secret)
    # Deliberately echoes its parameter to every stream -- the slice-3 canary proves
    # the redaction pipeline catches all of them.
    $InformationPreference = 'Continue'
    Write-Information "info leaks $Secret"
    Write-Warning "warning leaks $Secret"
    Write-Error "error leaks $Secret"
    "output leaks $Secret"
}

function Write-StubDoublyEscapedSecretLeak {
    [CmdletBinding()]
    param([string] $Secret)
    # #156: simulates tool output that is ALREADY JSON (an HTTP error body, a
    # serialized object) quoted into a log line. ConvertTo-Json here is the FIRST
    # serialization layer -- it JSON-escapes $Secret once, e.g. `pa"ss` becomes the
    # text `pa\"ss` inside the returned string. The executor's Emit() then JSON
    # -serializes THIS string as the `line` field of the job_events payload, which is
    # the SECOND layer: the already-escaped backslash-quote is itself escaped,
    # producing `pa\\\"ss` (default encoder). Only the #156 needle set catches that.
    $InformationPreference = 'Continue'
    $inner = [pscustomobject]@{ error = $Secret } | ConvertTo-Json -Compress
    Write-Information "upstream response: $inner"
}

function Invoke-StubHangThenWrite {
    [CmdletBinding()]
    param([int] $Milliseconds = 3000)
    # Ignores Stop() while sleeping, then emits a late record -- the #160 repro: a
    # job that already reported TimedOut must not receive this line in its log.
    [System.Threading.Thread]::Sleep($Milliseconds)
    Write-Warning 'late line after the job already finished'
}

Export-ModuleMember -Function Get-StubInventory, Write-StubStreams, Get-StubEcho, Invoke-StubFailure, Invoke-StubHang, Invoke-StubHangThenWrite, Write-StubSecretLeak, Write-StubDoublyEscapedSecretLeak
