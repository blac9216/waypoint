# ADR-0015: Distribute source; operators build, provision, and export appliances

Status: Accepted
Amends: 0001, 0009, 0010
Date: 2026-08-11

Supersedes the prebuilt-image delivery portion of
[ADR-0001](0001-packaging.md) and clarifies
[ADR-0009](0009-self-update.md) and
[ADR-0010](0010-deployment-topology.md).

## Context

ADR-0001 described v1 air-gap delivery as a project-supplied `docker save` archive of
all images. That is not the intended distribution or entitlement boundary. Waypoint is
a public source repository. Some tools and content required by operators may be
downloadable only under the operator's own account or entitlement, so the project
cannot publish them or completed images containing them.

The product's central purpose is nevertheless to acquire and manage the software and
content an authorized operator needs, then move a functional appliance and selected
managed content across an air gap. Avoiding project redistribution must not turn into
an appliance that loses required tooling when transferred.

## Decision Drivers

_Backfilled under ADR-0027 from #431, #432. Issue #431 requested recording "that
Waypoint distributes source/build definitions while operators build, provision,
export, and transfer their own appliance images and required tooling"; PR #432 is the
ADR's own originating commit. Neither records a separate comparison session — the
drivers below are this ADR's own original Context text, restated as a bullet list:_

- Waypoint is a public source repository, and some tools/content required by
  operators are downloadable only under the operator's own account or entitlement, so
  the project cannot publish them or completed images containing them (Context).
- The product's central purpose is nevertheless to acquire and manage the software and
  content an authorized operator needs, then move a functional appliance and selected
  managed content across an air gap (Context).
- Avoiding project redistribution must not turn into an appliance that loses required
  tooling when transferred (Context).

## Considered Options

_Backfilled under ADR-0027 from #431, #432, and ADR-0001 (Amends: above). The sources
do not record a named alternative distribution model beside the one this ADR
supersedes — ADR-0001's original v1 delivery, which this ADR's own header and opening
paragraph identify as the packaging portion being replaced:_

- **Project-supplied `docker save` archive of all images** (ADR-0001's original v1
  air-gap delivery) — superseded for this purpose. A project-built and
  project-distributed image cannot contain entitlement-restricted tools or content
  that the project has no right to redistribute, so this option cannot satisfy the
  entitlement driver above.
- **Public source and build definitions; operators build, provision entitled tools
  through Waypoint, and export their own images** (chosen). Keeps entitled material
  out of anything the project publishes, while the operator-built appliance still
  acquires and carries everything a receiving appliance needs across the air gap.

## Decision

1. **The project publishes source and build definitions, not completed container
   images or prebuilt appliance archives.** An operator obtains the repository and
   runs Compose to build the Waypoint images in the connected environment.

2. **Project-owned execution code moves into this repository.** The Dockerfiles,
   orchestration code, and PowerShell maintained in the predecessor compliance and
   download repositories become the build contexts for the two runner images. Their
   histories and required attribution are preserved according to the repository's
   licensing policy.

3. **Entitlement-restricted tools are installed by the operator through Waypoint.** A
   connected appliance may fetch a tool from its authorized upstream repository using
   operator-supplied credentials, or accept an operator-provided local/manual source.
   Installed tools and managed content live in persistent appliance state rather than
   mutating immutable runner images.

4. **Operator-created transfer bundles carry everything required for the receiving
   appliance's selected functions.** This includes applicable installed tooling,
   compliance content, catalog/index data, selected software artifacts, repository
   deltas, and other declared managed state. Inclusion is determined by the bundle's
   function/content selection and manifest, not by an artificial project-distribution
   restriction. The operator initiates and performs the export and remains responsible
   for the terms governing acquired and transferred material.

5. **Before automated export exists, operators export their locally built images and
   move them manually.** The disconnected side loads those images and deploys the same
   Compose topology in disconnected mode.

6. **The future updater/exporter automates operator-side image export.** A connected
   appliance can place its locally built Waypoint images, immutable tags/digests,
   compatibility metadata, and selected managed content into the signed, versioned
   transfer format shared with updates.

7. **Import stages updates; it does not apply them.** When an imported bundle contains
   newer compatible appliance images, Settings reports **Appliance update available**.
   Applying the update is a separate, explicit Admin action that performs re-auth,
   image loading, service recreation, health gating, and rollback handling under
   ADR-0009.

## Rationale

- The public project never needs to publish restricted binaries or images containing
  them.
- Operators can use their own accounts and entitlements without exposing those values
  to the project.
- Persistent managed state survives runner-image replacement and can be described,
  checksummed, signed, and transferred explicitly.
- The connected appliance remains the composition point for a functional disconnected
  appliance, which is the core cross-enclave product value.
- Separating import from apply prevents a content transfer from unexpectedly restarting
  the appliance.

## Consequences

- Source builds require the connected environment to reach all permitted build-time
  dependency sources or provide an equivalent local package source.
- Release/version metadata must identify source revision and locally built image
  digests; a public registry tag cannot be assumed.
- Transfer manifests must distinguish appliance images, installed tools, managed
  content, and ordinary artifacts while preserving one signature/checksum envelope.
- Tool/content volumes require export/import adapters and compatibility rules; image
  export alone is not a complete appliance transfer.
- Project documentation must say what Waypoint distributes factually and leave license
  compliance for acquired/transferred material with the operator; it must not promise
  a legal conclusion.

### Consequence learned from the real VCFDT distribution (2026-08-24, #669)

Broadcom does not publish a detached signature beside each VCF Download Tool archive.
The offline depot instead publishes the artifact's byte size and SHA-256 in
`PROD/metadata/productVersionCatalog/v1/productVersionCatalog.json`, with an RSA
PKCS#1 v1.5/SHA-256 signature envelope in `productVersionCatalog.sig`. A local
repository install therefore authenticates the exact catalog bytes against an
independently provisioned VMware/Broadcom certificate, then verifies the candidate's
catalog size and SHA-256 before activation. A certificate embedded in the signature
envelope is not trusted merely because it is embedded there. Manual-upload and
connected-fetch delivery are tracked separately under Epic #667 because their
metadata sources differ.
