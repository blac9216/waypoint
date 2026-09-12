# ADR-0009: Signed update bundles applied by a dedicated updater sidecar

Status: Accepted
Amended-by: 0015
Date: 2026-08-02

## Context

The appliance should update itself from the UI. Half the deployments are air-gapped, so
registry polling (Watchtower-style) is out; updates arrive as operator-uploaded bundles.
Anything holding the Docker socket is root-equivalent on the host — a fact the
STIG-literate audience will scrutinize.

## Decision Drivers

_Backfilled under ADR-0027 from #18, #41, #45, #46._ The sources record no issue or PR
that captures the original evaluation session — this ADR was authored in the repo's
initial skeleton commit ("Add architecture docs, ADRs, Claude workflow skills, and repo
skeleton", `2fef4614`, 2026-08-02T09:43:45Z), which predates any tracked issue or PR.
#18, #41, #45, #46 are the earliest tracked issues naming ADR-0009, all opened the same
day, and confirm the drivers below by consuming them directly. The bullets themselves
are drawn from this ADR's own original Context text, not invented:

- The appliance needs to update itself from the UI rather than relying on an operator's
  own tooling (Context, above).
- Half the deployments are air-gapped, so registry polling (Watchtower-style) has no
  registry to reach; updates must arrive as operator-uploaded bundles instead.
- Anything holding the Docker socket is root-equivalent on the host, and the
  STIG-literate audience will scrutinize whatever holds it — privilege containment is
  required.

## Considered Options

_Backfilled under ADR-0027 from #18, #41, #45, #46._ As with Decision Drivers above, no
issue or PR beyond this ADR's own original Context text records the comparison; #18,
#41, #45, #46 are the earliest tracked issues naming ADR-0009. Signed update bundles
applied by a dedicated updater sidecar were chosen; registry polling (Watchtower-style)
was named and rejected in Context.

1. **Signed update bundles applied by a dedicated updater sidecar** (this decision) —
   realised by #41 (bundle format shared with transfer bundles), #45 (`upgrade.sh`,
   the v1 offline apply path), and #46 (updater sidecar + in-UI self-update), all
   tracked under Epic #18.
2. **Registry polling (Watchtower-style)** — named in Context as ruled out because it
   assumes network reachability to a registry, which half the target deployments (the
   air-gapped ones) do not have. The sources record no further evaluation beyond that
   rejection reason.

## Decision

- **Update bundle**: signed tarball — images (`docker save`), compose file, manifest
  with versions/checksums. Shares its signing/manifest format with transfer bundles
  (ADR-0010).
- **Flow**: UI upload → backend verifies signature + version compatibility → re-auth
  required → `docker load` → **updater sidecar** recreates changed services
  (`compose up -d` semantics) → per-service health-check gate → previous tags retained
  for one-command rollback. DB migrations run before the new backend takes traffic.
- **Privilege containment**: only the updater sidecar reaches the Docker socket, via a
  socket proxy allowlisting the needed API calls. The backend *requests* updates over
  an internal API; it never touches the socket.
- **Updater self-update**: the updater spawns a transient one-shot runner container to
  replace it (nothing being replaced performs the replacing).
- **Connected mode** may additionally check for update availability online; air-gapped
  instances only see version + upload.
- In the eventual OVA (ADR-0001), the same bundle is applied by a host-side systemd
  unit instead of the sidecar.

## Consequences

- Brief per-service downtime during updates — accepted appliance behavior (ADR-0001).
- The bundle format is load-bearing and versioned; build it before the in-UI apply
  (an `upgrade.sh` that consumes the same bundle is the v1 milestone).
- Key management for bundle signing (who signs releases, how keys are distributed to
  verify air-gapped) must be specified before first release.
