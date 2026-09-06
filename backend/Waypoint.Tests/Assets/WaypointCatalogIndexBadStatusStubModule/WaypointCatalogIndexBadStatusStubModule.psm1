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

# Invented stub for issue #1705 Option B's defense-in-depth test: mirrors
# Invoke-WaypointCatalogIndex's real signature and output shape (same shape as
# WaypointCatalogIndexStubModule) but emits one row with a status value the
# depot_artifacts_status_check constraint deliberately does not allow, alongside two
# valid rows -- proving one rejected row is skipped rather than aborting the whole
# batch. No vendor code, no real hostnames or credentials, everything here is
# fabricated.

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

	# Identities are prefixed wp1705- (distinct from WaypointCatalogIndexStubModule's
	# "stub-artifact-N" identities, which collide across the shared
	# [Collection("Postgres")] fixture's depot_artifacts table via the ON CONFLICT
	# (relative_path) upsert key) so this suite can assert on its own rows without a
	# table-wide row count, which the shared fixture does not otherwise guarantee.
	[pscustomobject]@{
		RecordType   = 'ArtifactPresence'
		ExternalId   = 'wp1705-badstatus-artifact-1'
		Sha256       = '00000000000000000000000000000000000000000000000000000000000011'
		Status       = 'present'
		Product      = 'VCF'
		Version      = '9.1'
		SizeBytes    = 1024
		RelativePath = 'wp1705/badstatus/artifact-1.iso'
	}
	[pscustomobject]@{
		RecordType   = 'ArtifactPresence'
		ExternalId   = 'wp1705-badstatus-artifact-bad'
		Sha256       = $null
		Status       = 'not-a-real-status'
		Product      = 'VCF'
		Version      = '9.1'
		SizeBytes    = $null
		RelativePath = 'wp1705/badstatus/artifact-bad.iso'
	}
	[pscustomobject]@{
		RecordType   = 'ArtifactPresence'
		ExternalId   = 'wp1705-badstatus-artifact-2'
		Sha256       = '00000000000000000000000000000000000000000000000000000000000022'
		Status       = 'missing'
		Product      = 'VCF'
		Version      = '9.2'
		SizeBytes    = $null
		RelativePath = 'wp1705/badstatus/artifact-2.iso'
	}
}

Export-ModuleMember -Function Invoke-WaypointCatalogIndex
