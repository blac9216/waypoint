// Copyright 2026 Justin Black
//
// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace Waypoint.Core.Downloads;

/// <summary>
/// A configured push target for the OCI bundle store (<c>push_target_consumers</c>,
/// migration 0118) -- issue #1403, split from the design record #1161: the operator's
/// own depot-registry (Software Depot / Harbor / Bootstrap Registry Appliance, #1157's
/// Q3 findings) that a staged <see cref="OciBundle"/> is pushed into. This child models
/// the consumer only; the write-mode bracket (enable -> push -> disable, #1157's
/// unauthenticated-write-window finding) and the push operation itself belong to #1441.
/// </summary>
/// <param name="Id">Primary key.</param>
/// <param name="Name">Operator-facing label for this push target.</param>
/// <param name="RegistryFqdn">The depot-registry FQDN <c>imgpkg copy --to-repo</c> targets.</param>
/// <param name="WriteModeEnabled">
/// Placeholder safety flag mirroring the registry's own <c>offlineWriteEnabled</c>-style
/// toggle (#1157: pushes are unauthenticated while this is on, so the vendor's own
/// guidance is to flip it only to bracket a single push). Always <c>false</c> until
/// #1441 implements the enable/disable bracket that actually drives it.
/// </param>
/// <param name="CreatedAt">When this push target was configured.</param>
/// <param name="UpdatedAt">When this row last changed.</param>
public sealed record PushTargetConsumer(
	Guid Id,
	string Name,
	string RegistryFqdn,
	bool WriteModeEnabled,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);
