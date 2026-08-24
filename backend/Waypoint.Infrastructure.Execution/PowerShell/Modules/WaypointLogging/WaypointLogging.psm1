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

# Waypoint-owned logging adapter (issue #579). The imported vmware-stig-docker
# transport files (module.transport.vmware.ps1, module.transport.nsxapi.ps1,
# module.common.ps1) are dot-sourced unmodified into Waypoint wrappers
# (WaypointDiscovery/WaypointScan/WaypointCredentialTest) and expect two functions
# from that sibling repo's module.logging.ps1 to already be in scope: Get-LogSplat
# and Write-Log. That sibling module also pulls in a whole parallel-engine logging
# stack (an async LogQueue thread, Write-LogDirect's console color map, a file
# writer) this single-request-per-runspace host does not have and does not need --
# and the sibling repo carries no LICENSE, so CLAUDE.md's Borrowing Policy bars
# copying any of its code, queue included. This module is a from-scratch,
# Waypoint-authored implementation of just the two functions' call contract
# (signature/parameter names only -- no sibling function bodies are reproduced),
# translating each call into the native PowerShell output streams
# PowerShellExecutor.WireStreamCapture already forwards into the redacting
# IJobLogBuffer as job.log events. One adapter, preloaded before any imported
# transport module, replaces the per-wrapper Get-LogSplat/Write-Log stand-ins that
# WaypointScan.psm1 and WaypointCredentialTest.psm1 each carried, and supplies the
# one WaypointDiscovery.psm1 was missing entirely (the bug this issue tracks).
#
# Severity mapping to native streams (chosen so PowerShellExecutor's existing
# capture/redaction path requires no changes):
#   Debug    -> Write-Debug
#   Verbose  -> Write-Verbose
#   Info     -> Write-Information
#   Success  -> Write-Information (no native "success" stream; the message text
#               itself already says e.g. "Successfully connected to ...")
#   Warning  -> Write-Warning
#   Error    -> Write-Error (non-terminating: ErrorAction defaults to Continue, so
#               this never stops the pipeline or throws -- an error-severity log
#               line is presentation, not outcome. Job success/failure is decided
#               solely by PowerShellExecutor (terminating exceptions, timeouts,
#               native exit code) and by each wrapper's own throw/return -- see the
#               module header note below.)
#   Critical -> Write-Error (same non-terminating rationale; nothing in the
#               imported transport calls -Severity Critical today, but the mapping
#               is defined so an unrecognized-severity gap can never resurface the
#               original missing-command failure mode)
#
# Redaction: this module never receives a SecureString, PSCredential, or session
# token/cookie -- every call site in the imported transport passes only message
# text it has already composed (e.g. "Connecting to vCenter 'X'..."), never
# credential material. IJobLogBuffer's redaction (raw + JSON-escaped tracked
# secrets) still applies to whatever text does pass through, but this module adds
# no logging path that could bypass it -- there is no file writer, console writer,
# or queue here for a message to leak through instead of the captured streams.

#region Get-LogSplat

<#
.SYNOPSIS
    Build the -Source splat hashtable for Write-Log (and other -Source-aware
    imported-transport functions).

.DESCRIPTION
    Returns @{ Source = $Source } when a source is set, or an empty @{} when it
    is not -- matching the imported transport's own `$WriteLogParams = Get-LogSplat
    $Source; ... @WriteLogParams` calling convention, so it can dot-source and run
    unmodified.

.PARAMETER Source
    The component identifier, or empty/null.

.OUTPUTS
    [hashtable] suitable for splatting.
#>
function Get-LogSplat {
	[CmdletBinding()]
	param(
		[Parameter(Position = 0)]
		[AllowNull()]
		[AllowEmptyString()]
		[string]$Source
	)

	if ($Source) { return @{ Source = $Source } }
	return @{}
}

#endregion

#region Write-Log

<#
.SYNOPSIS
    Routes an imported-transport log call into the native PowerShell stream
    PowerShellExecutor already captures, batches, redacts, and persists as a
    job.log event.

.DESCRIPTION
    Matches the call signature the imported vmware-stig-docker transport files use
    (Message, Severity, LogQueue, Source, Timestamp) so Connect-StigVIServer,
    Get-NsxSessionToken, and Test-TargetReachable can be dot-sourced and invoked
    unmodified. LogQueue is accepted for signature compatibility only and is
    always ignored: this host runs one request per runspace with no parallel
    engine and no second logging pipeline to hand a queue to (per this issue's
    acceptance criteria -- no sibling queue/thread/file writer/console-color
    subsystem is introduced here).

    Writing to a native stream at Error severity is presentation only and never
    redefines job outcome: it does not throw and (with the default ErrorAction)
    does not stop the pipeline. A wrapper that needs to fail the job still does so
    the way every Waypoint wrapper already does -- `throw`, or a
    Success/FailureReason return object inspected by its job handler.

.PARAMETER Message
    The log message text. Never pass credential material (password, SecureString,
    PSCredential, session token/cookie) here -- every current imported-transport
    call site already respects that; this function has no alternate sink a secret
    could reach instead of the redacting job-log path.

.PARAMETER Severity
    One of Debug, Verbose, Info, Success, Warning, Error, Critical. Defaults to
    Info, matching the sibling contract's own default.

.PARAMETER LogQueue
    Accepted, always ignored (see DESCRIPTION).

.PARAMETER Source
    Optional component identifier, appended to the message so it survives being
    flattened onto a single native stream (native streams carry no separate
    "source" field).

.PARAMETER Timestamp
    Accepted for signature compatibility. Not used: PowerShellExecutor's own
    job.log event already carries its own arrival-ordered timestamp; stamping a
    second one here would just be redundant.
#>
function Write-Log {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory, Position = 0)]
		[string]$Message,

		[Parameter()]
		[ValidateSet('Debug', 'Verbose', 'Info', 'Success', 'Warning', 'Error', 'Critical')]
		[string]$Severity = 'Info',

		[Parameter()]
		[object]$LogQueue = $null,

		[Parameter()]
		[AllowNull()]
		[AllowEmptyString()]
		[string]$Source,

		[Parameter()]
		[datetime]$Timestamp = (Get-Date)
	)

	$Line = if ($Source) { "[$Source] $Message" } else { $Message }

	# -*Action Continue on every branch, not ambient $InformationPreference/
	# $DebugPreference/$VerbosePreference: those preference variables are read from
	# THIS function's own scope, which does not inherit a caller's local assignment
	# across a function-call boundary (only dynamic scoping through common
	# parameters or global/script scope reaches here) -- explicit -*Action makes
	# every severity land in job.log regardless of what preference the imported
	# transport's caller happens to have set, which is the whole point of an
	# adapter callers can rely on unconditionally.
	switch ($Severity) {
		'Debug' { Write-Debug $Line -Debug:$true }
		'Verbose' { Write-Verbose $Line -Verbose:$true }
		'Info' { Write-Information $Line -InformationAction Continue }
		'Success' { Write-Information $Line -InformationAction Continue }
		'Warning' { Write-Warning $Line -WarningAction Continue }
		'Error' { Write-Error $Line -ErrorAction Continue }
		'Critical' { Write-Error $Line -ErrorAction Continue }
		default { Write-Information $Line -InformationAction Continue }
	}
}

#endregion

Export-ModuleMember -Function Get-LogSplat, Write-Log
