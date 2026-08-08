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

# Invented stub for the credential-test full-loop integration test (issue #245).
# Mirrors WaypointCredentialTest's real function signatures/output shape without
# touching the sibling repo, PowerCLI, or a real vCenter/NSX/SSH host -- no vendor
# code, no real hostnames or credentials, everything here is fabricated (fictional
# example.internal hosts only, per CLAUDE.md's sanitization gate).
#
# $env:WAYPOINT_CREDENTIAL_TEST_STUB_MODE drives which canned outcome each call
# reports:
#   'success' (default) -- Success = $true.
#   'failure' -- Success = $false with an invented non-auth FailureReason.
#   'auth' -- Success = $false with a FailureReason containing an
#     AuthFailureMarkers-recognized token ("401"), so the handler's classification
#     path is exercised without a real credential ever being rejected by anything.

function Get-CredentialTestStubOutcome {
	param([Parameter(Mandatory)][string]$Label)

	$Mode = $env:WAYPOINT_CREDENTIAL_TEST_STUB_MODE
	if (-not $Mode) { $Mode = 'success' }

	switch ($Mode) {
		'auth' { return [pscustomobject]@{ Success = $false; FailureReason = "$Label stub: 401 Unauthorized" } }
		'failure' { return [pscustomobject]@{ Success = $false; FailureReason = "$Label stub: connection refused" } }
		default { return [pscustomobject]@{ Success = $true; FailureReason = $null } }
	}
}

function Invoke-WaypointVCenterCredentialTest {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$VCenter,
		[Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Username,
		[Parameter(Mandatory)][AllowEmptyString()][string]$Password,
		[Parameter()][string]$VmwareStigDockerTransportPath
	)

	# Deliberately touches the Information stream, exactly like the other stub
	# modules -- if the decrypted password ever leaked into this handler's
	# invocation, the canary test would catch it here.
	$InformationPreference = 'Continue'
	Write-Information "Testing stub vCenter '$VCenter' as '$Username' (password length $($Password.Length))"
	return Get-CredentialTestStubOutcome -Label 'vcenter'
}

function Invoke-WaypointNsxCredentialTest {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Manager,
		[Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Username,
		[Parameter(Mandatory)][AllowEmptyString()][string]$Password,
		[Parameter()][string]$VmwareStigDockerNsxApiPath
	)

	$InformationPreference = 'Continue'
	Write-Information "Testing stub NSX manager '$Manager' as '$Username' (password length $($Password.Length))"
	return Get-CredentialTestStubOutcome -Label 'nsx'
}

function Invoke-WaypointSshCredentialTest {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$SshHost,
		[Parameter()][int]$Port = 22,
		[Parameter()][string]$VmwareStigDockerCommonPath
	)

	$InformationPreference = 'Continue'
	Write-Information "Testing stub ssh host '$SshHost' port $Port"
	return Get-CredentialTestStubOutcome -Label 'ssh'
}

Export-ModuleMember -Function Invoke-WaypointVCenterCredentialTest, Invoke-WaypointNsxCredentialTest, Invoke-WaypointSshCredentialTest
