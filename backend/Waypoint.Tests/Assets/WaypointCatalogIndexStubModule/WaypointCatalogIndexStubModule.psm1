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

# Invented stub for the catalog-index full-loop integration test (issue #194, updated
# #1512). Mirrors Invoke-WaypointCatalogIndex's real signature and output shape
# (including the #1503 RecordType discriminator: 'ArtifactPresence' vs 'UnknownFile')
# without touching the sibling repo or a real depot share -- no vendor code, no real
# hostnames or credentials, everything here is fabricated.

function Invoke-WaypointCatalogIndex {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)]
		[ValidateNotNullOrEmpty()]
		[string]$DepotPath,

		[Parameter()]
		[AllowEmptyString()]
		[AllowNull()]
		[string]$DepotToken,

		[Parameter()]
		[string]$VcfDownloadManagerCommonPath
	)

	# Deliberately touches every PS stream the canary test hunts through, exactly like
	# WaypointStubModule's Write-StubSecretLeak -- if the token ever leaked into this
	# handler's invocation, it would surface here.
	$InformationPreference = 'Continue'
	Write-Information "Indexing stub depot share: $DepotPath (token length $($DepotToken.Length))"

	1..3 | ForEach-Object {
		[pscustomobject]@{
			RecordType   = 'ArtifactPresence'
			ExternalId   = "stub-artifact-$_"
			Sha256       = "0000000000000000000000000000000000000000000000000000000000$_$_"
			Status       = 'present'
			Product      = 'VCF'
			Version      = "9.$_"
			SizeBytes    = 1024 * $_
			RelativePath = "stub/artifact-$_.iso"
		}
	}

	# One unknown file per sweep -- proves CatalogIndexJobHandler's UnknownFile branch
	# through the same full-loop test the ArtifactPresence rows already exercise.
	[pscustomobject]@{
		RecordType   = 'UnknownFile'
		RelativePath = 'stub/unknown-artifact.iso'
		SizeBytes    = 2048
	}

	Write-Information 'Indexed 3 files, 1 unknown file so far...'
	Write-Information 'Indexing complete: 3 files, 1 unknown file.'
}

Export-ModuleMember -Function Invoke-WaypointCatalogIndex
