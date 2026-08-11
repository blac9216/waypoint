# ADR-0015: Distribute source; operators build, provision, and export appliances

Status: Accepted

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
