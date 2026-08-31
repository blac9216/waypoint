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

# Invented fake for CatalogIndexJobHandlerRealModuleEndToEndTests.cs (round-1 review
# finding 3 on issue #1503/PR #1629): unlike WaypointCatalogIndexStubModule.psm1 (which
# replaces Invoke-WaypointCatalogIndex itself and is therefore blind to a real
# contract break between the REAL module and CatalogIndexJobHandler), this file only
# stands in for the sibling repo's vcf-download-manager.common.ps1 -- the REAL,
# unmodified WaypointCatalogIndex.psm1 shim dot-sources it, exactly as it would the
# real script. No network, no real depot; the returned manifest is fabricated.

function Write-Log {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory, Position = 0)]
		[AllowEmptyString()]
		[string]$Message,

		[Parameter()]
		[string]$Severity = 'Info',

		[Parameter()]
		[string]$Source
	)
}

function Get-FileManifest {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[string]$Directory,

		[Parameter()]
		[switch]$IncludeHash,

		[Parameter()]
		[string]$HashAlgorithm = 'SHA256'
	)

	return [ordered]@{
		'vcsa-patch.iso' = @{ Size = 100; Hash = 'AAAA' }
	}
}
