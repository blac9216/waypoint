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

# Invented stub module for issue #579's WaypointLogging adapter test coverage. Calls
# Get-LogSplat/Write-Log exactly the way the imported vmware-stig-docker transport
# files do (build a -Source splat once, splat it into every call) so these tests
# exercise the shared adapter through its real, documented calling convention rather
# than by invoking Write-Log directly with hand-picked parameters.

function Invoke-LoggingCallerStubAllSeverities {
	[CmdletBinding()]
	param([string]$Source = 'LoggingCallerStub')

	$InformationPreference = 'Continue'
	$VerbosePreference = 'Continue'
	$DebugPreference = 'Continue'

	$WriteLogParams = Get-LogSplat $Source
	Write-Log 'debug line' -Severity 'Debug' @WriteLogParams
	Write-Log 'verbose line' -Severity 'Verbose' @WriteLogParams
	Write-Log 'info line' -Severity 'Info' @WriteLogParams
	Write-Log 'success line' -Severity 'Success' @WriteLogParams
	Write-Log 'warning line' -Severity 'Warning' @WriteLogParams
	Write-Log 'error line' -Severity 'Error' @WriteLogParams -ErrorAction Continue
	Write-Log 'critical line' -Severity 'Critical' @WriteLogParams -ErrorAction Continue
}

function Invoke-LoggingCallerStubWithoutSource {
	[CmdletBinding()]
	param()

	$InformationPreference = 'Continue'
	$WriteLogParams = Get-LogSplat $null
	Write-Log 'sourceless line' -Severity 'Info' @WriteLogParams
}

# Pins that an Error-severity Write-Log call is presentation only: it must never
# throw, never stop the pipeline, and must never make the job fail on its own --
# only the explicit `throw` below (a real terminating failure) may do that. If
# Write-Log's Error path is ever changed to something terminating, this function
# would never reach its own throw and the distinguishing assertion would fail.
function Invoke-LoggingCallerStubErrorThenTerminatingFailure {
	[CmdletBinding()]
	param([string]$Source = 'LoggingCallerStub')

	$WriteLogParams = Get-LogSplat $Source
	Write-Log 'an error-severity log line, not a job failure' -Severity 'Error' @WriteLogParams -ErrorAction Continue
	throw 'the real terminating failure'
}

# Pins that Write-Log never has a path to leak secret material even when a caller
# (incorrectly) tries to log one: nothing in this module or WaypointLogging writes to
# a file, console, or queue outside the native streams PowerShellExecutor already
# captures and IJobLogBuffer already redacts.
function Invoke-LoggingCallerStubWithSecret {
	[CmdletBinding()]
	param([string]$Secret, [string]$Source = 'LoggingCallerStub')

	$InformationPreference = 'Continue'
	$WriteLogParams = Get-LogSplat $Source
	Write-Log "connecting with token $Secret" -Severity 'Info' @WriteLogParams
}

Export-ModuleMember -Function Invoke-LoggingCallerStubAllSeverities, Invoke-LoggingCallerStubWithoutSource, Invoke-LoggingCallerStubErrorThenTerminatingFailure, Invoke-LoggingCallerStubWithSecret
