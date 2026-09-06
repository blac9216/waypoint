# ADR-0029: Depot store volume topology — one depot volume, carved-out stores by exception

Status: Accepted
Date: 2026-09-06

## Context

The M1 design put every download artifact in one undifferentiated `artifacts` volume.
The download-parity design (Epic #16, owner grill decision 4) planned to dissolve that
volume's role and give every sidecar store (ESX/UMDS, Photon, VMware Tools, VKS,
content libraries) its **own** named volume, each independently mountable and
serve-able. Implementation (#1502, PR #1587) found that plan does not survive contact
with the tool that owns the depot tree: `vcf-download-tool --depot-store` writes
`UMDS/`, `Photon/`, `VKS/`, `VMTools/`, `ContentLibrary/`, `VCSA/`, and `Transfer/` as
subdirectories of **one** root it controls end to end
(`vcf-download-manager.common.ps1`), and the backend indexes that same root as one
`CatalogOptions.DepotPath`. Splitting those subdirectories onto separate Compose
volumes means repointing the tool's own store paths against project policy (AGENTS.md:
the vendor tool's own layout expectations are not project code to rewrite around), and
Compose `volume.subpath` mounts fail closed when the subpath does not exist yet — on a
fresh stack the runner has written nothing, so nginx would refuse to start entirely.
`docs/rationale/deploy.md#nginx-repo-store-subtree-aliases` records the operational
detail; this ADR records the architectural decision it implements.

A second question surfaced independently on #1706 (2026-09-06): the content-library
store is not runner-written at all — the backend and operator UI write library items
directly (chunked upload, add-to-library actions) and must never touch the vendor
`PROD` depot tree the acquisition tool owns. That store needs its own lifecycle
(independent backup/restore, independent capacity accounting, survives a read-only
depot mount) that the shared depot volume cannot give it.

## Decision Drivers

- The vendor tool's own on-disk layout expectations are load-bearing and are not
  project code to rewrite around (AGENTS.md).
- Compose volume subpaths must exist before nginx starts, which a fresh install cannot
  guarantee per-store.
- Backend-written stores (content libraries) must be architecturally incapable of
  writing into the vendor-owned `PROD` tree, and must survive a depot mount going
  read-only for maintenance.
- Per-store operator-facing auth/access dials (decision 15) must be enforceable without
  the volume boundary being the only enforcement mechanism.

## Considered Options

1. **Six-plus independent named volumes, one per store** (the original decision-4
   plan) — clean per-store isolation and capacity accounting, but breaks on the
   `--depot-store` single-root contract for every runner-written store and hits the
   Compose subpath-must-exist-on-boot failure on a fresh stack.
2. **One volume for everything, including content libraries** — simplest Compose
   shape, but a backend-written store sharing a volume with the vendor-tool-owned tree
   means a nginx/Compose misconfiguration (or a bug in the backend's own write path)
   could reach into `PROD`, and the library can never be backed up, restored, or made
   read-only independently of the depot.
3. **One shared depot volume for every runner-written store (subtree aliasing at
   nginx) plus a separate named volume for the content-library store** (this
   decision) — matches what `vcf-download-tool` actually requires for the stores it
   writes, while giving the one store the backend itself writes its own volume,
   isolation boundary, and lifecycle.

## Decision

The runner-written stores (ESX/patch, Photon, VMware Tools, VKS, plus `VCSA`/
`Transfer` staging) live as subdirectories of **one** `depot` volume, mounted
read-write at `/vcf` in `download-runner` and read-only at `/srv/repo` in nginx; nginx
enforces per-store isolation at the **location** layer, not the volume layer — each
store gets its own `alias`ed location, and a regex location denies direct access to
the shared root (`/repo/depot/`) ahead of any prefix match, so no store subtree is
reachable through another store's path or the shared root. The content-library store
gets its **own** named volume, mounted only where the backend writes library items;
the backend never writes into the depot tree, and the depot volume may be mounted
read-only for maintenance without affecting library operations. The M1 `artifacts`
volume's role is retired to the retirement of the legacy `download` job type
(ADR-0030) — it is not repurposed as either of these.

## Consequences

- A new runner-written store directory name must be added to nginx's subtree-deny
  regex at the same time it is added to the tool's `--depot-store` layout, or it
  becomes reachable through the shared root (`docs/rationale/deploy.md
  #nginx-repo-store-subtree-aliases` names the smoke-test guard).
- Per-store auth/access dials (decision 15, ADR-0031) are enforced per nginx
  `location`, not per Compose volume — the volume boundary alone was never going to be
  fine-grained enough for that requirement even under the original six-volume plan.
- Content-library backup/restore, capacity accounting, and any future read-only-depot
  maintenance mode are independent of the depot volume from day one.
- Decision 4's "each sidecar store gets its own volume" is not implemented as written;
  this ADR is the record of why, and is the one future readers should cite instead of
  the frozen design-record body on #16.
